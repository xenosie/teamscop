using Teamscop.App.Services;
using Teamscop.Engine.Auth;

namespace Teamscop.App.Composition;

/// <summary>
/// One connection pool for the whole process. A second HttpClient only ever appears if the API
/// host changes at runtime, and it still rides the same pooled handler.
/// </summary>
public sealed class HttpStack : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    public HttpStack()
    {
        Shared = CreateClient();
    }

    public HttpClient Shared { get; }

    public HttpClient CreateClient()
        => new(_handler, disposeHandler: false) { Timeout = RequestTimeout };

    public void Dispose()
    {
        Shared.Dispose();
        _handler.Dispose();
    }
}

/// <summary>
/// Hand-rolled composition root, created once in <see cref="Program.Main"/> before the UI starts.
/// Every ViewModel takes what it needs from here through its constructor — there is no container
/// and no service locator beyond <see cref="Current"/>, which only windows touch.
/// </summary>
public sealed class AppServices : IDisposable
{
    private static readonly Lazy<AppServices> Instance = new(() => new AppServices(), isThreadSafe: true);

    public AppServices()
        : this(null, null, null)
    {
    }

    /// <summary>
    /// Composition with the two outward-facing pieces substitutable. Everything downstream — the
    /// ViewModels, the image loader, the gap cache — is built from them exactly as in production,
    /// so a test exercises the real code and only the socket and the state file are stand-ins.
    /// </summary>
    public AppServices(UiLog? log, SessionStore? session, TeamscopApi? api)
    {
        Log = log ?? new UiLog();
        DeviceKeys = new DeviceKeyProvider();
        Session = session ?? new SessionStore(DeviceKeys, Log);
        Http = new HttpStack();
        Api = api ?? new TeamscopApi(Session, Http);
        Authority = new AuthorityState();
        Clock = new CompanyClock();
        Images = new ImageLoader(Http.Shared, Session, Log);
        AgentHealth = new AgentHealthReader(Session, Log);
        Sticker = new StickerHost(Session, Log);
        StatusReporter = new AppStatusReporter(Api, Session, Log);

        // The guard reads business-clock.json from the staff Session directory to verify codes on
        // company time with no network. The app is often the only component that knows the zone
        // before the service has ever run, so it writes the same file. See CompanyClock.Persist.
        Clock.Persist = cfg =>
        {
            var dir = Path.GetDirectoryName(new Engine.Lifecycle.LocalAgentStore(Engine.Lifecycle.AgentRole.Staff).StatePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Engine.Tracking.BusinessClockStore.Save(dir, cfg);
            }
        };
    }

    public static AppServices Current => Instance.Value;

    public UiLog Log { get; }
    public IDeviceKeyProvider DeviceKeys { get; }
    public SessionStore Session { get; }
    public HttpStack Http { get; }
    public TeamscopApi Api { get; }
    public AuthorityState Authority { get; }
    public CompanyClock Clock { get; }
    public ImageLoader Images { get; }

    /// <summary>A15 — what the tracking engine on this machine is actually doing (§14.4).</summary>
    public AgentHealthReader AgentHealth { get; }

    /// <summary>Creates no window until <see cref="StickerHost.Show"/> — this runs before Avalonia.</summary>
    public StickerHost Sticker { get; }

    /// <summary>
    /// §14.2 — the independent channel that lets the server distinguish a stopped service from a
    /// switched-off machine. Idle until Start() is called on a monitored machine.
    /// </summary>
    public AppStatusReporter StatusReporter { get; }

    /// <summary>Builds the graph eagerly so the first UI frame never pays for it.</summary>
    public static AppServices Initialize()
    {
        var services = Current;
        services.Log.Info($"Teamscop.App starting · api={services.Session.ApiBaseUrl} · role={services.Session.ActiveRole}");
        return services;
    }

    public void Dispose()
    {
        Api.Dispose();
        Images.Dispose();
        Http.Dispose();
    }
}
