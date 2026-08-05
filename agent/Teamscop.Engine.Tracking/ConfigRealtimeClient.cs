using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Receives admin tracking + business-time config immediately via SignalR.
/// </summary>
public sealed class ConfigRealtimeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;
    private HubConnection? _connection;
    private StaffTrackingConfig _config = new();
    private BusinessClockConfig _businessTime = new();
    private readonly object _gate = new();

    public event Action<StaffTrackingConfig>? ConfigChanged;
    public event Action<BusinessClockConfig>? BusinessTimeChanged;

    public ConfigRealtimeClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public StaffTrackingConfig Current
    {
        get { lock (_gate) return _config; }
    }

    public BusinessClockConfig CurrentBusinessTime
    {
        get { lock (_gate) return _businessTime; }
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

        _connection.On<BusinessClockConfig>("BusinessTimeUpdated", cfg =>
        {
            lock (_gate) _businessTime = cfg;
            BusinessTimeChanged?.Invoke(cfg);
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
        var cfg = await JsonSerializer.DeserializeAsync<StaffTrackingConfig>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (cfg is not null)
        {
            lock (_gate) _config = cfg;
            ConfigChanged?.Invoke(cfg);
        }

        return cfg;
    }

    public async Task<BusinessClockConfig?> PullBusinessTimeAsync(HttpClient http, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/business-time/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var cfg = await JsonSerializer.DeserializeAsync<BusinessClockConfig>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (cfg is not null)
        {
            lock (_gate) _businessTime = cfg;
            BusinessTimeChanged?.Invoke(cfg);
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
