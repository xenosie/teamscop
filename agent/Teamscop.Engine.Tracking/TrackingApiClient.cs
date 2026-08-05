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
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var url = $"api/tracking/events?staffUserId={staffUserId:D}&take={take}";
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            url += $"&eventType={Uri.EscapeDataString(eventType.Trim())}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ApiClientException.ThrowIfUnsuccessfulAsync(resp, "Tracking API", ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<List<TrackingEventItem>>(JsonOptions, ct).ConfigureAwait(false))
               ?? [];
    }

    /// <summary>Fetch recent events and keep only PHASE9 App history types (newest first).</summary>
    public async Task<IReadOnlyList<TrackingEventItem>> QueryAppHistoryAsync(
        string accessToken,
        Guid staffUserId,
        int take = 200,
        CancellationToken ct = default)
    {
        var events = await QueryEventsAsync(accessToken, staffUserId, take, eventType: null, ct)
            .ConfigureAwait(false);
        return events
            .Where(e => AppHistoryEventTypes.IsAppHistory(e.EventType))
            .OrderByDescending(e => e.OccurredAt)
            .ToList();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
