using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Receives admin tracking + business-time + org structure updates immediately via SignalR.
/// </summary>
public sealed class ConfigRealtimeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;
    private HubConnection? _connection;
    private StaffTrackingConfig _config = new();
    private BusinessClockConfig _businessTime = new();
    private OrgStructureDto? _org;
    private EffectiveAuthoritiesDto? _authorities;
    private readonly object _gate = new();

    public event Action<StaffTrackingConfig>? ConfigChanged;
    public event Action<BusinessClockConfig>? BusinessTimeChanged;
    public event Action<OrgStructureDto>? OrgStructureChanged;
    public event Action<EffectiveAuthoritiesDto>? AuthoritiesChanged;
    public event Action<IReadOnlyList<PolicemanDto>>? PolicemenChanged;
    /// <summary>Fired after SignalR automatic reconnect — callers should re-pull business time.</summary>
    public event Func<Task>? ReconnectedAsync;

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

    public OrgStructureDto? CurrentOrg
    {
        get { lock (_gate) return _org; }
    }

    public EffectiveAuthoritiesDto? CurrentAuthorities
    {
        get { lock (_gate) return _authorities; }
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

        _connection.On<OrgStructureDto>("OrgStructureUpdated", org =>
        {
            lock (_gate) _org = org;
            OrgStructureChanged?.Invoke(org);
        });

        _connection.On<EffectiveAuthoritiesDto>("AuthoritiesUpdated", auth =>
        {
            lock (_gate) _authorities = auth;
            AuthoritiesChanged?.Invoke(auth);
        });

        _connection.On<List<PolicemanDto>>("PolicemenUpdated", list =>
        {
            PolicemenChanged?.Invoke(list ?? []);
        });

        _connection.Reconnected += async _ =>
        {
            var handler = ReconnectedAsync;
            if (handler is not null)
            {
                await handler().ConfigureAwait(false);
            }
        };

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

    public async Task<MyOrgPlacementDto?> PullOrgPlacementAsync(HttpClient http, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/org/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MyOrgPlacementDto>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<EffectiveAuthoritiesDto?> PullAuthoritiesAsync(HttpClient http, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/police/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var auth = await JsonSerializer.DeserializeAsync<EffectiveAuthoritiesDto>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (auth is not null)
        {
            lock (_gate) _authorities = auth;
            AuthoritiesChanged?.Invoke(auth);
        }

        return auth;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
