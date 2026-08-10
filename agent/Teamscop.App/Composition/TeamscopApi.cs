using System.Net.Http.Headers;
using System.Text.Json;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Composition;

/// <summary>§5.1 — coarse presence for a staff row's badge. Working → green, rest → red, offline → grey.</summary>
public enum StaffPresenceState
{
    /// <summary>No answer yet: the badge is not drawn until presence is known.</summary>
    Unknown,
    Offline,
    Rest,
    Working
}

/// <summary>
/// §14 — the machine's engine status, which is a different question from whether the employee is at
/// the keyboard. Green working, grey unreachable, black something is wrong, yellow not installed.
/// </summary>
public enum StaffAgentStatusState
{
    /// <summary>No answer yet, or a value this build does not recognise. No dot is drawn.</summary>
    Unknown,

    /// <summary>Reporting, and capture is working.</summary>
    Online,

    /// <summary>Nothing is reporting. Off, asleep, or cut off — genuinely unknowable.</summary>
    Offline,

    /// <summary>A live reporter on that machine says the other half is broken.</summary>
    Broken,

    /// <summary>Removed with authorisation, or never installed.</summary>
    Uninstalled
}

/// <summary>One staff member's live presence, mapped onto their roster row.</summary>
public sealed record StaffPresence(
    Guid UserId,
    StaffPresenceState State,
    StaffAgentStatusState Status = StaffAgentStatusState.Unknown,
    string? StatusReason = null)
{
    public static StaffPresenceState Parse(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "working" => StaffPresenceState.Working,
        "rest" => StaffPresenceState.Rest,
        "offline" => StaffPresenceState.Offline,
        _ => StaffPresenceState.Offline
    };

    /// <summary>
    /// Unrecognised values fall to Unknown rather than to any of the four. A future server that
    /// adds a fifth status must leave an old client silent about it, not confidently wrong.
    /// </summary>
    public static StaffAgentStatusState ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "online" => StaffAgentStatusState.Online,
        "offline" => StaffAgentStatusState.Offline,
        "broken" => StaffAgentStatusState.Broken,
        "uninstalled" => StaffAgentStatusState.Uninstalled,
        _ => StaffAgentStatusState.Unknown
    };
}

/// <summary>
/// One facade over every engine API client, all sharing a single connection pool and one timeout.
/// The access token is read from <see cref="SessionStore"/> here, so no caller loads state or
/// passes tokens around, and every call is cancellable.
/// </summary>
public class TeamscopApi : IDisposable
{
    private static readonly JsonSerializerOptions PresenceJson = new() { PropertyNameCaseInsensitive = true };

    private readonly SessionStore? _session;
    private readonly HttpStack? _http;
    private readonly object _gate = new();
    private ClientSet? _clients;

    private sealed record PresenceRow(Guid UserId, string State, string? Status, string? StatusReason);

    public TeamscopApi(SessionStore session, HttpStack http)
    {
        _session = session;
        _http = http;
        _clients = ClientSet.Create(session.ApiBaseUrl, http.Shared);
    }

    /// <summary>
    /// For a test double that overrides the calls it needs and never opens a socket. No client set
    /// is built, so any un-overridden member throws rather than silently reaching the network.
    /// </summary>
    protected TeamscopApi()
    {
    }

    // ---- auth -------------------------------------------------------------------------------

    public Task<AuthSession> LoginAsync(string deviceKey, string password, CancellationToken ct)
        => Clients().Auth.LoginAsync(deviceKey, password, ct);

    public Task<AuthSession> AdminSignupAsync(
        string deviceKey,
        string companyName,
        string password,
        Stream? avatar,
        string? avatarFileName,
        CancellationToken ct)
        => Clients().Auth.AdminSignupAsync(deviceKey, companyName, password, avatar, avatarFileName, ct);

    public Task<AuthSession> StaffSignupAsync(
        string deviceKey,
        string username,
        string password,
        string companyToken,
        Stream? avatar,
        string? avatarFileName,
        CancellationToken ct)
        => Clients().Auth.StaffSignupAsync(
            deviceKey, username, password, companyToken, avatar, avatarFileName, ct);

    public Task<AuthUser> MeAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Auth.MeAsync(token, ct);
    }

    // ---- org --------------------------------------------------------------------------------

    public virtual Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.ListVisibleStaffAsync(token, ct);
    }

    public Task<OrgStructureDto> GetStructureAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.GetStructureAsync(token, ct);
    }

    public Task<MyOrgPlacementDto> GetMyPlacementAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.GetMyPlacementAsync(token, ct);
    }

    public Task<TeamDto> CreateTeamAsync(string name, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.CreateTeamAsync(token, name, leaderUserId: null, ct);
    }

    public Task<TeamDto> UpdateTeamAsync(
        Guid teamId,
        string? name = null,
        Guid? leaderUserId = null,
        bool clearLeader = false,
        CancellationToken ct = default)
    {
        var (clients, token) = Authed();
        return clients.Org.UpdateTeamAsync(token, teamId, name, leaderUserId, clearLeader, ct);
    }

    public Task DeleteTeamAsync(Guid teamId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.DeleteTeamAsync(token, teamId, ct);
    }

    public Task<TeamDto> AddTeamMemberAsync(Guid teamId, Guid staffUserId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.AddMemberAsync(token, teamId, staffUserId, ct);
    }

    public Task<TeamDto> RemoveTeamMemberAsync(Guid teamId, Guid staffUserId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Org.RemoveMemberAsync(token, teamId, staffUserId, ct);
    }

    // ---- authorities ------------------------------------------------------------------------

    public Task<EffectiveAuthoritiesDto> GetMyAuthoritiesAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Police.GetMineAsync(token, ct);
    }

    public Task<IReadOnlyList<AuthorityPackageInfo>> ListPackagesAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Police.ListPackagesAsync(token, ct);
    }

    public Task<IReadOnlyList<PolicemanDto>> ListPolicemenAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Police.ListPolicemenAsync(token, ct);
    }

    public Task<PolicemanDto> UpsertPolicemanAsync(
        Guid staffUserId, IReadOnlyList<string> packages, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Police.UpsertPolicemanAsync(token, staffUserId, packages, ct);
    }

    public Task RevokePolicemanAsync(Guid staffUserId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Police.RevokePolicemanAsync(token, staffUserId, ct);
    }

    // ---- §14 server aggregates --------------------------------------------------------------

    /// <summary>§14.2 — one ranked page. <paramref name="to"/> is exclusive; the span must be ≤ 31 days.</summary>
    public Task<LeaderboardPage> GetLeaderboardAsync(
        DateTimeOffset from, DateTimeOffset to, int page, int pageSize, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetLeaderboardAsync(token, from, to, page, pageSize, ct);
    }

    /// <summary>§14.4 — self liveness for the sticker; works for a plain staff account.</summary>
    public Task<AgentSelfHealth> GetMyAgentHealthAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetMyAgentHealthAsync(token, ct);
    }

    /// <summary>
    /// §5.1 — coarse presence (working|rest|offline) for exactly the staff the caller sees on the
    /// roster, so each row can carry a live badge in one request for 20–50 members (§15.2). Read as
    /// a raw authenticated GET rather than through an engine client: the badge is an app concern and
    /// the shared client core has no presence surface. Virtual so a test double answers offline.
    /// </summary>
    public virtual async Task<IReadOnlyList<StaffPresence>> GetPresenceAsync(CancellationToken ct)
    {
        var token = _session?.AccessToken;
        if (string.IsNullOrWhiteSpace(token) || _http is null || _session is null)
        {
            throw new NotSignedInException();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_session.ApiBaseUrl}/api/tracking/presence");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.Shared
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var rows = await JsonSerializer
            .DeserializeAsync<List<PresenceRow>>(stream, PresenceJson, ct)
            .ConfigureAwait(false);
        return rows is null
            ? []
            : rows.Select(r => new StaffPresence(
                r.UserId,
                StaffPresence.Parse(r.State),
                StaffPresence.ParseStatus(r.Status),
                r.StatusReason)).ToList();
    }

    // ---- tracking ---------------------------------------------------------------------------

    /// <param name="before">
    /// Cursor for the next (older) page: the last item's OccurredAt. Exclusive, and it only ever
    /// narrows the window — paging never widens past <paramref name="to"/>.
    /// </param>
    public virtual Task<IReadOnlyList<ScreenshotMetaItem>> QueryScreenshotsAsync(
        Guid staffUserId,
        int take,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct,
        DateTimeOffset? before = null)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryScreenshotsAsync(token, staffUserId, take, from, to, ct, before);
    }

    public Task<byte[]> GetScreenshotThumbAsync(
        Guid eventId, int displayIndex, int maxWidth, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetScreenshotThumbAsync(token, eventId, displayIndex, maxWidth, ct);
    }

    public virtual Task<byte[]> GetScreenshotImageAsync(Guid eventId, int displayIndex, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetScreenshotImageAsync(token, eventId, displayIndex, ct);
    }

    public Task<IReadOnlyList<BrowsingDomainSummary>> QueryBrowsingDomainsAsync(
        Guid staffUserId, int take, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryBrowsingDomainsAsync(token, staffUserId, take, from, to, ct);
    }

    public Task<BrowsingDomainDetail> QueryBrowsingDomainDetailAsync(
        Guid staffUserId,
        string domain,
        int take,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryBrowsingDomainDetailAsync(
            token, staffUserId, domain, take, from, to, ct);
    }

    public Task<IReadOnlyList<BrowsingTopUrlItem>> QueryBrowsingTopUrlsAsync(
        Guid staffUserId, int take, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryBrowsingTopUrlsAsync(token, staffUserId, take, from, to, ct);
    }

    public virtual Task<TimeTrackTimeline> QueryTimeTrackTimelineAsync(
        Guid staffUserId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryTimeTrackTimelineAsync(token, staffUserId, from, to, ct);
    }

    public Task<IReadOnlyList<TrackingEventItem>> QueryAppHistoryAsync(
        Guid staffUserId, int take, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.QueryAppHistoryAsync(token, staffUserId, take, from, to, ct);
    }

    public Task<StaffTrackingConfig> GetStaffTrackingConfigAsync(Guid staffUserId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetStaffTrackingConfigAsync(token, staffUserId, ct);
    }

    public Task<StaffTrackingConfig> UpsertStaffTrackingConfigAsync(
        Guid staffUserId, StaffTrackingConfig config, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.UpsertStaffTrackingConfigAsync(token, staffUserId, config, ct);
    }

    // ---- business time ----------------------------------------------------------------------

    public Task<BusinessClockConfig> GetBusinessTimeAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.GetBusinessTimeAsync(token, ct);
    }

    /// <summary>§8.4 — the timezone is the whole setting, so the whole request is the zone id.</summary>
    public Task<CompanyTimeZone> SetBusinessTimeZoneAsync(string timeZoneId, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Tracking.SetBusinessTimeZoneAsync(token, timeZoneId, ct);
    }

    // ---- lifecycle --------------------------------------------------------------------------

    public virtual Task<IReadOnlyList<TotpStatusResult>> ListStaffTotpAsync(CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Lifecycle.ListStaffTotpAsync(token, ct);
    }

    public virtual Task<TotpCodeResult> GetTotpCodeAsync(Guid staffUserId, string purpose, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Lifecycle.GetTotpCodeAsync(token, staffUserId, ct, purpose);
    }

    /// <summary>§14.2 — the app's independent report. See <c>AppStatusReporter</c>.</summary>
    public virtual Task AppReportAsync(AppStatusReport report, CancellationToken ct)
    {
        var (clients, token) = Authed();
        return clients.Lifecycle.AppReportAsync(token, report, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _clients?.Dispose();
        }
    }

    private (ClientSet Clients, string Token) Authed()
    {
        var token = _session?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new NotSignedInException();
        }

        return (Clients(), token);
    }

    /// <summary>
    /// Clients for the current host. An engine client pins BaseAddress on construction, so a host
    /// change (enrolment against a different server) needs a fresh client over the same pool.
    /// </summary>
    private ClientSet Clients()
    {
        var baseUrl = _session!.ApiBaseUrl;
        lock (_gate)
        {
            if (_clients is null || !string.Equals(_clients.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _clients?.Dispose();
                _clients = ClientSet.Create(baseUrl, _http!.CreateClient(), ownsHttp: true);
            }

            return _clients;
        }
    }

    private sealed class ClientSet : IDisposable
    {
        private ClientSet(
            string baseUrl,
            HttpClient http,
            bool ownsHttp,
            AuthApiClient auth,
            OrgApiClient org,
            PoliceApiClient police,
            TrackingApiClient tracking,
            LifecycleApiClient lifecycle)
        {
            BaseUrl = baseUrl;
            Http = http;
            OwnsHttp = ownsHttp;
            Auth = auth;
            Org = org;
            Police = police;
            Tracking = tracking;
            Lifecycle = lifecycle;
        }

        public string BaseUrl { get; }
        public HttpClient Http { get; }
        public bool OwnsHttp { get; }
        public AuthApiClient Auth { get; }
        public OrgApiClient Org { get; }
        public PoliceApiClient Police { get; }
        public TrackingApiClient Tracking { get; }
        public LifecycleApiClient Lifecycle { get; }

        /// <summary>All five clients over one HttpClient — they only ever set the same BaseAddress.</summary>
        public static ClientSet Create(string baseUrl, HttpClient http, bool ownsHttp = false)
            => new(
                baseUrl,
                http,
                ownsHttp,
                new AuthApiClient(baseUrl, http),
                new OrgApiClient(baseUrl, http),
                new PoliceApiClient(baseUrl, http),
                new TrackingApiClient(baseUrl, http),
                new LifecycleApiClient(baseUrl, http));

        public void Dispose()
        {
            // The engine clients do not own the shared HttpClient, so disposing them is a no-op;
            // only a replacement client created for a changed host has to go.
            if (OwnsHttp)
            {
                Http.Dispose();
            }
        }
    }
}
