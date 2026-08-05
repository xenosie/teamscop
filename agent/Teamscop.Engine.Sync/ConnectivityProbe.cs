namespace Teamscop.Engine.Sync;

public sealed class ConnectivityStatus
{
    public required bool IsOnline { get; init; }
    public required bool ApiReachable { get; init; }
    public long? LatencyMs { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IConnectivityProbe
{
    Task<ConnectivityStatus> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes internet + Teamscop API health. Prefer API reachability over generic DNS.
/// </summary>
public sealed class ConnectivityProbe(HttpClient http, string healthUrl) : IConnectivityProbe
{
    public async Task<ConnectivityStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var started = Environment.TickCount64;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await http.GetAsync(healthUrl, cts.Token).ConfigureAwait(false);
            var latency = Environment.TickCount64 - started;
            var ok = response.IsSuccessStatusCode;
            return new ConnectivityStatus
            {
                IsOnline = ok,
                ApiReachable = ok,
                LatencyMs = latency,
                Detail = ok ? "ok" : $"HTTP {(int)response.StatusCode}"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ConnectivityStatus
            {
                IsOnline = false,
                ApiReachable = false,
                LatencyMs = Environment.TickCount64 - started,
                Detail = ex.GetType().Name
            };
        }
    }
}
