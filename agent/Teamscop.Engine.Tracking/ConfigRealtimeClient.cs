using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Receives admin tracking config immediately via SignalR; falls back to REST snapshot.
/// </summary>
public sealed class ConfigRealtimeClient : IAsyncDisposable
{
    private readonly string _baseUrl;
    private HubConnection? _connection;
    private StaffTrackingConfig _config = new();
    private readonly object _gate = new();

    public event Action<StaffTrackingConfig>? ConfigChanged;

    public ConfigRealtimeClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public StaffTrackingConfig Current
    {
        get { lock (_gate) return _config; }
    }

    public async Task StartAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/config", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<StaffTrackingConfig>("TrackingConfigUpdated", cfg =>
        {
            lock (_gate) _config = cfg;
            ConfigChanged?.Invoke(cfg);
        });

        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StaffTrackingConfig?> PullSnapshotAsync(HttpClient http, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tracking/config/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var cfg = await JsonSerializer.DeserializeAsync<StaffTrackingConfig>(stream, cancellationToken: ct).ConfigureAwait(false);
        if (cfg is not null)
        {
            lock (_gate) _config = cfg;
            ConfigChanged?.Invoke(cfg);
        }

        return cfg;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
