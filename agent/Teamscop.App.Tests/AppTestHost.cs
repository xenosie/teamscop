using System.Net;
using Avalonia.Headless;
using Avalonia.Threading;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

// One dispatcher thread serves the whole assembly, so the tests that use it must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Teamscop.App.Tests;

/// <summary>
/// The ViewModels marshal every mutation onto Avalonia's UI thread, so a test that simply awaits
/// them deadlocks — which is a large part of why this project never existed and why every defect
/// below shipped. One headless dispatcher session is started for the whole assembly and each test
/// body runs inside it, on the UI thread, exactly as it does in the product.
/// </summary>
public static class AppTestHost
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AppTestHost).Assembly),
            isThreadSafe: true);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Runs the body on the dispatcher thread and rethrows whatever it threw.</summary>
    public static void Run(Func<Task> body)
    {
        using var cts = new CancellationTokenSource(Budget);
        Session.Value.Dispatch(body, cts.Token).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits for work the product deliberately does not await — the ViewModels start code fetches
    /// and avatar loads with FireAndForget, which is correct for a UI and awkward for a test.
    /// </summary>
    public static async Task Until(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }
}

/// <summary>A device key that never shells out to SMBIOS — start-up must not depend on hardware.</summary>
public sealed class FakeDeviceKeys : IDeviceKeyProvider
{
    public string GetDeviceKey() => "test-device-key";
}

/// <summary>
/// A composition root with a real <see cref="SessionStore"/> pointed at a scratch directory and a
/// stub API. Everything between them — the ViewModels, the image loader, the gap cache — is the
/// production object graph.
/// </summary>
public sealed class TestServices : IDisposable
{
    private readonly string _root;

    private TestServices(string root, AppServices services, SessionStore session)
    {
        _root = root;
        Services = services;
        Session = session;
    }

    public AppServices Services { get; }
    public SessionStore Session { get; }

    public static TestServices SignedIn(TeamscopApi api, string role = "admin", Guid? userId = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new UiLog();
        var session = new SessionStore(new FakeDeviceKeys(), log, root);
        session.SaveFor(
            SessionStore.ToAgentRole(role),
            new LocalAgentState
            {
                AccessToken = "test-token",
                DeviceKey = "test-device-key",
                Role = role,
                UserId = userId ?? Guid.NewGuid(),
                ApiBaseUrl = "https://example.test",
                Username = "Owner",
                CompanyName = "Ocean Group"
            });

        return new TestServices(root, new AppServices(log, session, api), session);
    }

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that outlives the run is harmless.
        }
    }
}

/// <summary>
/// Stands in for the network. Only the calls the screens under test actually make are overridden,
/// so anything else reaching for a socket fails loudly instead of quietly hitting the internet.
/// </summary>
public sealed class StubApi : TeamscopApi
{
    private readonly List<string> _codeRequests = [];

    public List<OrgStaffDto> Staff { get; } = [];
    public List<TotpStatusResult> TotpRows { get; } = [];
    public List<StaffPresence> Presence { get; } = [];
    public Exception? StaffFailure { get; set; }
    public int StaffCalls { get; private set; }
    public int PresenceCalls { get; private set; }
    public IReadOnlyList<string> CodeRequests => _codeRequests;

    public override Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(CancellationToken ct)
    {
        StaffCalls++;
        if (StaffFailure is not null)
        {
            return Task.FromException<IReadOnlyList<OrgStaffDto>>(StaffFailure);
        }

        return Task.FromResult<IReadOnlyList<OrgStaffDto>>(Staff.ToList());
    }

    public override Task<IReadOnlyList<StaffPresence>> GetPresenceAsync(CancellationToken ct)
    {
        PresenceCalls++;
        return Task.FromResult<IReadOnlyList<StaffPresence>>(Presence.ToList());
    }

    public override Task<IReadOnlyList<TotpStatusResult>> ListStaffTotpAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TotpStatusResult>>(TotpRows.ToList());

    public List<ScreenshotMetaItem> Screenshots { get; } = [];
    public TimeTrackTimeline? Timeline { get; set; }

    public override Task<IReadOnlyList<ScreenshotMetaItem>> QueryScreenshotsAsync(
        Guid staffUserId, int take, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct,
        DateTimeOffset? before = null)
        => Task.FromResult<IReadOnlyList<ScreenshotMetaItem>>(Screenshots.ToList());

    public override Task<TimeTrackTimeline> QueryTimeTrackTimelineAsync(
        Guid staffUserId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => Task.FromResult(Timeline ?? new TimeTrackTimeline { From = from, To = to });

    // The viewer decodes these bytes; empty is enough to exercise open/close without a socket.
    public override Task<byte[]> GetScreenshotImageAsync(Guid eventId, int displayIndex, CancellationToken ct)
        => Task.FromResult(Array.Empty<byte>());

    public override Task<TotpCodeResult> GetTotpCodeAsync(Guid staffUserId, string purpose, CancellationToken ct)
    {
        _codeRequests.Add(purpose);
        var row = TotpRows.FirstOrDefault(r => r.StaffUserId == staffUserId);
        return Task.FromResult(new TotpCodeResult
        {
            StaffUserId = staffUserId,
            StaffUsername = row?.StaffUsername ?? "Someone",
            Code = "266355",
            PeriodSeconds = 30,
            RemainingSeconds = 12
        });
    }
}

/// <summary>Records the outgoing request so a test can assert what the app actually sent.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    private readonly object _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>Set to keep every in-flight request parked, so concurrency is observable.</summary>
    public TaskCompletionSource<bool>? Hold { get; set; }

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        if (Hold is { } hold)
        {
            await hold.Task.ConfigureAwait(false);
        }

        return _respond(request);
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }
}
