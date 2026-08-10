using System.Net;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Tests;

/// <summary>
/// Defect 5 — "even though admin and staff uploaded the photo when registering, in admin side, no
/// picture is seen."
///
/// Two independent causes, both here. B12 moved every media route behind <c>RequireAuthorization()</c>
/// and <see cref="ImageLoader"/> was the one client in the tree that sent no bearer, so production
/// nginx recorded eight avatar fetches and eight 401s — including the correctly-pathed staff photo.
/// Separately, rows written before B12 still carry <c>/media/avatars/…</c>, a path the server's
/// catch-all answers with HTTP 200 and a 24-byte <c>text/plain</c> banner, which is not a failure
/// any client would notice.
/// </summary>
public class AvatarLoadingTests
{
    private const string Avatar = "/api/media/avatars/ca643c846ddf4ff69513d95a415ebcc1.png";

    [Fact]
    public async Task EveryAvatarRequestCarriesTheBearerToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        using var host = TestServices.SignedIn(new StubApi());
        using var loader = new ImageLoader(http, host.Session, new UiLog());

        var bitmap = await loader.LoadAsync(Avatar);

        Assert.Null(bitmap);
        var request = Assert.Single(handler.Requests);
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A200ThatIsNotAnImage_IsRejectedRatherThanDecoded()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Text(
            HttpStatusCode.OK, "Teamscop API is online.\n", "text/plain"));
        using var http = new HttpClient(handler);
        using var host = TestServices.SignedIn(new StubApi());
        using var loader = new ImageLoader(http, host.Session, new UiLog());

        // The dead avatar path returns exactly this. It used to reach the bitmap decoder and only
        // fail by accident of Avalonia throwing on it.
        Assert.Null(await loader.LoadAsync("/media/avatars/79383d0f074f46908f95a9627d817430.png"));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData(null, true)]
    [InlineData("text/plain", false)]
    [InlineData("text/html", false)]
    [InlineData("application/json", false)]
    public void OnlyAnImageResponseIsWorthDecoding(string? contentType, bool accepted)
        => Assert.Equal(accepted, ImageLoader.IsImageResponse(contentType));

    [Fact]
    public void ARequestWithoutASession_IsStillWellFormed()
    {
        using var request = ImageLoader.BuildRequest("https://example.test/x.png", null);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("/media/avatars/abc.png", "/api/media/avatars/abc.png")]
    [InlineData("/api/media/avatars/abc.png", "/api/media/avatars/abc.png")]
    [InlineData("/media/other/abc.png", "/media/other/abc.png")]
    [InlineData("https://cdn.test/media/avatars/abc.png", "https://cdn.test/media/avatars/abc.png")]
    public void TheLegacyAvatarPrefixIsRepairedOnceAndOnlyOnce(string stored, string expected)
        => Assert.Equal(expected, SessionStore.NormalizeMediaPath(stored));

    [Fact]
    public void ARowStillHoldingTheOldPath_ResolvesOntoTheAuthorizedRoute()
    {
        using var host = TestServices.SignedIn(new StubApi());

        Assert.Equal(
            "https://example.test/api/media/avatars/79383d.png",
            host.Session.ToAbsoluteUrl("/media/avatars/79383d.png"));
    }

    [Fact]
    public async Task AnAvatarIsFetchedOncePerUrl_NoMatterHowManyRowsAskAtOnce()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        using var host = TestServices.SignedIn(new StubApi());
        using var loader = new ImageLoader(http, host.Session, new UiLog());

        // §15.2 — 25 realised rows must not open 25 sockets on cheap hardware. Every request is
        // parked at the handler so the asks genuinely overlap, which is what a scrolling nav does.
        handler.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = Task.WhenAll(Enumerable.Range(0, 25).Select(_ => loader.LoadAsync(Avatar)));
        await Task.Delay(200);
        var concurrent = handler.Requests.Count;
        handler.Hold.SetResult(true);
        await all;

        Assert.Equal(1, concurrent);
    }
}

/// <summary>
/// Defect 8 — the marker the agent's capture gate reads. It is best effort by design (HKLM is not
/// writable by a standard user), but the value it derives must never be wrong.
/// </summary>
public class InstalledRoleTests
{
    [Theory]
    [InlineData(AgentRole.Admin, "admin", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Admin, "staff", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Staff, "admin", InstalledRoleWriter.Admin)]
    [InlineData(AgentRole.Staff, "staff", InstalledRoleWriter.Staff)]
    [InlineData(AgentRole.Staff, null, InstalledRoleWriter.Staff)]
    public void AdminWinsWhereverTheStoreAndTheAccountDisagree(
        AgentRole store, string? accountRole, string expected)
        => Assert.Equal(expected, InstalledRoleWriter.RoleFor(store, accountRole));

    [Fact]
    public void AnAdminSessionIsNeverAStaffSession_SoItNeverGetsAMonitoringSticker()
    {
        using var host = TestServices.SignedIn(new StubApi(), role: "admin");

        Assert.True(host.Session.IsAdminSession);
        Assert.False(
            host.Session.IsStaffRole,
            "defect 8 — an admin must never be presented to themselves as a monitored subject.");
    }

    [Fact]
    public void AnAdminTokenFiledUnderTheStaffStore_StillCountsAsAnAdminSession()
    {
        using var host = TestServices.SignedIn(new StubApi(), role: "staff");

        // A machine enrolled as staff and later re-registered as the admin console: the store says
        // one thing and the account says another, and "admin" has to win or the owner gets a
        // sticker telling him he is being watched on his own PC.
        host.Session.SaveFor(AgentRole.Staff, new LocalAgentState
        {
            AccessToken = "test-token",
            DeviceKey = "test-device-key",
            Role = "admin",
            ApiBaseUrl = "https://example.test"
        });

        Assert.False(host.Session.IsStaffRole);
        Assert.True(host.Session.IsAdminSession);
    }
}
