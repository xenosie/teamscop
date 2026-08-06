using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    public long? BusinessClockVersion { get; set; }
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

public sealed class StaffChainHealth
{
    public Guid StaffUserId { get; set; }
    public long LastVaultSequence { get; set; }
    public long GapCount { get; set; }
    public bool ChainBroken { get; set; }
    public long? BreakAfterSequence { get; set; }
    public long? ExpectedNextSequence { get; set; }
    public long? HighestSequenceFound { get; set; }
    public DateTimeOffset? LastBreakAt { get; set; }
    public string? BreakDetail { get; set; }
    public bool? HelperAlive { get; set; }
    public bool? TrackingOk { get; set; }
    public int? PendingOutbox { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public bool? Online { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool AgentOffline { get; set; }
    public string? BannerMessage { get; set; }
}

public sealed class TimeTrackSegmentItem
{
    /// <summary>working | rest | gap</summary>
    public string Kind { get; set; } = "gap";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public double DurationSeconds { get; set; }
}

/// <summary>PHASE9 lifecycle event types shown in App history.</summary>
public static class AppHistoryEventTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AgentEventTypes.Registration,
        AgentEventTypes.PowerOff,
        AgentEventTypes.UsbEvent,
        AgentEventTypes.Uninstall,
        AgentEventTypes.AppBroken
    };

    public static bool IsAppHistory(string? eventType)
        => !string.IsNullOrWhiteSpace(eventType) && All.Contains(eventType);
}

public sealed class TrackingApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public TrackingApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task<IReadOnlyList<TrackingEventItem>> QueryEventsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        string? eventType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var url = $"api/tracking/events?staffUserId={staffUserId:D}&take={take}";
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            url += $"&eventType={Uri.EscapeDataString(eventType.Trim())}";
        }

        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<TrackingEventItem>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
    }

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
            .Select(t => QueryEventsAllowForbiddenAsync(
                accessToken, staffUserId, perType, eventType: t, from, to, ct))
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

    /// <summary>Like <see cref="QueryEventsAsync"/> but returns [] on 403 (missing authority package).</summary>
    public async Task<IReadOnlyList<TrackingEventItem>> QueryEventsAllowForbiddenAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        string? eventType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var url = $"api/tracking/events?staffUserId={staffUserId:D}&take={take}";
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            url += $"&eventType={Uri.EscapeDataString(eventType.Trim())}";
        }

        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return [];
        }

        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<TrackingEventItem>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
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
        var url = $"api/tracking/browsing/top-urls?staffUserId={staffUserId:D}&take={take}";
        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<BrowsingTopUrlItem>>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? [];
    }

    /// <summary>Metadata-only screenshot list (no JPEG payloads).</summary>
    public async Task<IReadOnlyList<ScreenshotMetaItem>> QueryScreenshotsAsync(
        string accessToken,
        Guid staffUserId,
        int take = 100,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var url = $"api/tracking/screenshots?staffUserId={staffUserId:D}&take={take}";
        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<ScreenshotMetaItem>>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? [];
    }

    public Task<byte[]> GetScreenshotThumbAsync(
        string accessToken,
        Guid eventId,
        int displayIndex = 1,
        int maxWidth = 320,
        CancellationToken ct = default)
        => GetScreenshotBytesAsync(
            accessToken,
            $"api/tracking/screenshots/{eventId:D}/thumb?display={displayIndex}&w={maxWidth}",
            ct);

    public Task<byte[]> GetScreenshotImageAsync(
        string accessToken,
        Guid eventId,
        int displayIndex = 1,
        CancellationToken ct = default)
        => GetScreenshotBytesAsync(
            accessToken,
            $"api/tracking/screenshots/{eventId:D}/image?display={displayIndex}",
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
        var url = $"api/tracking/browsing?staffUserId={staffUserId:D}&take={take}";
        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<BrowsingDomainSummary>>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? [];
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
        var url =
            $"api/tracking/browsing/detail?staffUserId={staffUserId:D}&domain={Uri.EscapeDataString(domain)}&take={take}";
        if (from is { } fromUtc)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}";
        }

        if (to is { } toUtc)
        {
            url += $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<BrowsingDomainDetail>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? new BrowsingDomainDetail { Domain = domain };
    }

    public async Task<TimeTrackTimeline> QueryTimeTrackTimelineAsync(
        string accessToken,
        Guid staffUserId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var url =
            $"api/tracking/timetrack?staffUserId={staffUserId:D}"
            + $"&from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}"
            + $"&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<TimeTrackTimeline>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? new TimeTrackTimeline { From = from, To = to };
    }

    public async Task<StaffChainHealth> QueryChainHealthAsync(
        string accessToken,
        Guid staffUserId,
        CancellationToken ct = default)
    {
        var url = $"api/tracking/chain/{staffUserId:D}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<StaffChainHealth>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? new StaffChainHealth { StaffUserId = staffUserId };
    }

    public async Task<StaffTrackingConfig> GetStaffTrackingConfigAsync(
        string accessToken,
        Guid staffUserId,
        CancellationToken ct = default)
    {
        var url = $"api/tracking/config/{staffUserId:D}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<StaffTrackingConfig>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? new StaffTrackingConfig { StaffUserId = staffUserId };
    }

    public async Task<StaffTrackingConfig> UpsertStaffTrackingConfigAsync(
        string accessToken,
        Guid staffUserId,
        StaffTrackingConfig config,
        CancellationToken ct = default)
    {
        var url = $"api/tracking/config/{staffUserId:D}";
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(config, options: JsonOptions);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<StaffTrackingConfig>(JsonOptions, ct)
                   .ConfigureAwait(false))
               ?? config;
    }

    private async Task<byte[]> GetScreenshotBytesAsync(
        string accessToken,
        string url,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
