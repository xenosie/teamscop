using System.Net;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Tracking;

public sealed class TrackingEventItem
{
    public Guid Id { get; set; }
    public Guid StaffUserId { get; set; }
    public string StaffUsername { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
}

public sealed class ScreenshotDisplayMeta
{
    public int Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Size { get; set; }
}

public sealed class ScreenshotMetaItem
{
    public Guid Id { get; set; }
    public Guid StaffUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
    public int DisplayCount { get; set; }
    public List<ScreenshotDisplayMeta> Displays { get; set; } = [];
}

public sealed class BrowsingDomainSummary
{
    public string Domain { get; set; } = "";
    public int VisitCount { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
}

public sealed class BrowsingVisitItem
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Profile { get; set; } = "";
    public DateTimeOffset VisitedAt { get; set; }
    public long? VisitId { get; set; }
}

public sealed class BrowsingDomainDetail
{
    public string Domain { get; set; } = "";
    public int VisitCount { get; set; }
    public List<BrowsingVisitItem> Visits { get; set; } = [];
}

public sealed class BrowsingTopUrlItem
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int VisitCount { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
}

public sealed class TimeTrackTimeline
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public double TotalSeconds { get; set; }
    public List<TimeTrackSegmentItem> Segments { get; set; } = [];
}

public sealed class TimeTrackSegmentItem
{
    /// <summary>working | rest | gap</summary>
    public string Kind { get; set; } = "gap";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public double DurationSeconds { get; set; }
}

// ---- §14.2 / §14.4 server insight DTOs (mirror the server records exactly).

public sealed class LeaderboardRow
{
    public int Rank { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public long WorkedSeconds { get; set; }
    public long IdleSeconds { get; set; }
}

/// <summary>§14.2 leaderboard: one aggregate over a business period, paged.</summary>
public sealed class LeaderboardPage
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public List<LeaderboardRow> Rows { get; set; } = [];
}

/// <summary>
/// §14.4 self liveness for the staff sticker. Carries no captured data, which is why a plain staff
/// member may read their own. status = protected | catching_up | not_reporting | unknown.
/// </summary>
public sealed class AgentSelfHealth
{
    public Guid UserId { get; set; }
    public string Status { get; set; } = "unknown";
    public string? StatusDetail { get; set; }
    public bool AgentOffline { get; set; }
    public bool? Online { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastTimeTrackAt { get; set; }
    public bool? HelperAlive { get; set; }
    public bool? TrackingOk { get; set; }
    public int? PendingOutbox { get; set; }
}

/// <summary>Response of <c>PUT /api/business-time</c> and the shape the app should render after a change.</summary>
public sealed class CompanyTimeZone
{
    public Guid CompanyId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public string DisplayName { get; set; } = "";
    public TimeSpan CurrentOffset { get; set; }
}

/// <summary>PHASE9 lifecycle event types shown in App history.</summary>
public static class AppHistoryEventTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AgentEventTypes.Registration,
        AgentEventTypes.PowerOff,
        AgentEventTypes.UsbEvent,
        AgentEventTypes.Uninstall
    };

    public static bool IsAppHistory(string? eventType)
        => !string.IsNullOrWhiteSpace(eventType) && All.Contains(eventType);
}

public sealed class TrackingApiClient : ApiClientBase
{
    public TrackingApiClient(string baseUrl, HttpClient? httpClient = null)
        : base("Tracking API", baseUrl, httpClient)
    {
    }

    // ---- §14 insight reads (leaderboard, self health) ------------------------------------------

    /// <summary>§14.2 leaderboard over a business period (to is exclusive; span ≤ 31 days).</summary>
    public async Task<LeaderboardPage> GetLeaderboardAsync(
        string accessToken,
        DateTimeOffset from,
        DateTimeOffset to,
        int page = 0,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var url = "api/tracking/leaderboard"
                  + Query(("from", from), ("to", to), ("page", page), ("pageSize", pageSize));
        return await GetOrNullAsync<LeaderboardPage>(url, accessToken, ct).ConfigureAwait(false)
               ?? new LeaderboardPage { From = from, To = to };
    }

    /// <summary>§14.4 self liveness — works for plain staff.</summary>
    public async Task<AgentSelfHealth> GetMyAgentHealthAsync(string accessToken, CancellationToken ct = default)
        => await GetOrNullAsync<AgentSelfHealth>("api/tracking/health/me", accessToken, ct).ConfigureAwait(false)
           ?? new AgentSelfHealth();

    // ---- business time (§8.4) ------------------------------------------------------------------

    public async Task<BusinessClockConfig> GetBusinessTimeAsync(string accessToken, CancellationToken ct = default)
        => await GetOrNullAsync<BusinessClockConfig>("api/business-time/me", accessToken, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty business-time config.");

    /// <summary>Admin-only §8.4 timezone replace — the whole clock is the one setting.</summary>
    public async Task<CompanyTimeZone> SetBusinessTimeZoneAsync(
        string accessToken, string timeZoneId, CancellationToken ct = default)
        => await SendJsonAsync<CompanyTimeZone>(
                   HttpMethod.Put, "api/business-time", new { timeZoneId }, accessToken, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("Empty business-time response.");

    // ---- event / capture reads -----------------------------------------------------------------

    public async Task<IReadOnlyList<TrackingEventItem>> QueryEventsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        string? eventType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
        => await QueryEventsInternalAsync(accessToken, staffUserId, take, eventType, from, to, allowForbidden: false, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Fetch PHASE9 App history types (registration, power_off, usb_event, uninstall, app_broken).
    /// Queries each type separately so dense tracking traffic cannot starve lifecycle rows.
    /// Forbidden types (missing package) are skipped — partial packages still return allowed events.
    /// </summary>
    public async Task<IReadOnlyList<TrackingEventItem>> QueryAppHistoryAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var perType = Math.Clamp(take, 1, 200);
        var tasks = AppHistoryEventTypes.All
            .Select(t => QueryEventsInternalAsync(
                accessToken, staffUserId, perType, eventType: t, from, to, allowForbidden: true, ct))
            .ToArray();
        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches
            .SelectMany(x => x)
            .Where(e => AppHistoryEventTypes.IsAppHistory(e.EventType))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .ToList();
    }

    private async Task<IReadOnlyList<TrackingEventItem>> QueryEventsInternalAsync(
        string accessToken,
        Guid staffUserId,
        int take,
        string? eventType,
        DateTimeOffset? from,
        DateTimeOffset? to,
        bool allowForbidden,
        CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 500);
        var url = "api/tracking/events"
                  + Query(
                      ("staffUserId", staffUserId),
                      ("take", take),
                      ("eventType", string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim()),
                      ("from", from),
                      ("to", to));

        if (allowForbidden)
        {
            return await TryGetAsync<List<TrackingEventItem>>(url, accessToken, HttpStatusCode.Forbidden, ct)
                       .ConfigureAwait(false)
                   ?? [];
        }

        return await GetOrNullAsync<List<TrackingEventItem>>(url, accessToken, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<BrowsingTopUrlItem>> QueryBrowsingTopUrlsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 3,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);
        var url = "api/tracking/browsing/top-urls"
                  + Query(("staffUserId", staffUserId), ("take", take), ("from", from), ("to", to));
        return await GetOrNullAsync<List<BrowsingTopUrlItem>>(url, accessToken, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Metadata-only screenshot list (no JPEG payloads), newest first. Paging is a cursor: pass the
    /// last item's <c>occurredAt</c> as <paramref name="before"/> for the next page. <paramref name="before"/>
    /// is exclusive on occurredAt and only narrows the window — it cannot widen it past <paramref name="to"/>.
    /// </summary>
    public async Task<IReadOnlyList<ScreenshotMetaItem>> QueryScreenshotsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 100,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        // `before` follows the cancellation token deliberately: existing callers pass the token
        // positionally in this slot, so the cursor is added without shifting their binding.
        CancellationToken ct = default,
        DateTimeOffset? before = null)
    {
        take = Math.Clamp(take, 1, 200);
        var url = "api/tracking/screenshots"
                  + Query(
                      ("staffUserId", staffUserId),
                      ("take", take),
                      ("from", from),
                      ("to", to),
                      ("before", before));
        return await GetOrNullAsync<List<ScreenshotMetaItem>>(url, accessToken, ct).ConfigureAwait(false) ?? [];
    }

    public Task<byte[]> GetScreenshotThumbAsync(
        string accessToken,
        Guid eventId,
        int displayIndex = 1,
        int maxWidth = 320,
        CancellationToken ct = default)
        => GetBytesAsync(
            $"api/tracking/screenshots/{eventId:D}/thumb?display={displayIndex}&w={maxWidth}",
            accessToken,
            ct);

    public Task<byte[]> GetScreenshotImageAsync(
        string accessToken,
        Guid eventId,
        int displayIndex = 1,
        CancellationToken ct = default)
        => GetBytesAsync(
            $"api/tracking/screenshots/{eventId:D}/image?display={displayIndex}",
            accessToken,
            ct);

    public async Task<IReadOnlyList<BrowsingDomainSummary>> QueryBrowsingDomainsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var url = "api/tracking/browsing"
                  + Query(("staffUserId", staffUserId), ("take", take), ("from", from), ("to", to));
        return await GetOrNullAsync<List<BrowsingDomainSummary>>(url, accessToken, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<BrowsingDomainDetail> QueryBrowsingDomainDetailAsync(
        string accessToken,
        Guid staffUserId,
        string domain,
        int take = 200,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var url = "api/tracking/browsing/detail"
                  + Query(("staffUserId", staffUserId), ("domain", domain), ("take", take), ("from", from), ("to", to));
        return await GetOrNullAsync<BrowsingDomainDetail>(url, accessToken, ct).ConfigureAwait(false)
               ?? new BrowsingDomainDetail { Domain = domain };
    }

    public async Task<TimeTrackTimeline> QueryTimeTrackTimelineAsync(
        string accessToken,
        Guid staffUserId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var url = "api/tracking/timetrack" + Query(("staffUserId", staffUserId), ("from", from), ("to", to));
        return await GetOrNullAsync<TimeTrackTimeline>(url, accessToken, ct).ConfigureAwait(false)
               ?? new TimeTrackTimeline { From = from, To = to };
    }

    public async Task<StaffTrackingConfig> GetStaffTrackingConfigAsync(
        string accessToken,
        Guid staffUserId,
        CancellationToken ct = default)
        => await GetOrNullAsync<StaffTrackingConfig>($"api/tracking/config/{staffUserId:D}", accessToken, ct)
               .ConfigureAwait(false)
           ?? new StaffTrackingConfig { StaffUserId = staffUserId };

    public async Task<StaffTrackingConfig> UpsertStaffTrackingConfigAsync(
        string accessToken,
        Guid staffUserId,
        StaffTrackingConfig config,
        CancellationToken ct = default)
        => await SendJsonAsync<StaffTrackingConfig>(
                   HttpMethod.Put, $"api/tracking/config/{staffUserId:D}", config, accessToken, ct).ConfigureAwait(false)
           ?? config;
}
