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
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(30);
    private HubConnection? _connection;
    private string? _accessToken;
    private CancellationToken _lifetime;
    private volatile bool _disposed;
    private StaffTrackingConfig _config = new();
    private BusinessClockConfig _businessTime = new();
    private OrgStructureDto? _org;
    private EffectiveAuthoritiesDto? _authorities;
    private readonly object _gate = new();

    public event Action<StaffTrackingConfig>? ConfigChanged;
    public event Action<BusinessClockConfig>? BusinessTimeChanged;
    public event Action<OrgStructureDto>? OrgStructureChanged;

    /// <summary>
    /// Defect 3 — somebody joined the company, so whatever roster this caller is allowed to see has
    /// changed. Carried on the company-wide group and deliberately contentless: a team leader and an
    /// approval-only policeman are outside the management group and must never receive the org
    /// chart, but they do have a roster of their own to re-read. Whatever they then fetch is
    /// re-scoped server-side, so this message can never widen anybody's reach.
    /// </summary>
    public event Action? StaffRosterChanged;
    public event Action<EffectiveAuthoritiesDto>? AuthoritiesChanged;
    public event Action<IReadOnlyList<PolicemanDto>>? PolicemenChanged;
    /// <summary>Fired after any reconnect (SignalR automatic or our own) — callers should re-pull.</summary>
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
        _accessToken = accessToken;
        _lifetime = cancellationToken;
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private HubConnection BuildConnection()
    {
        var token = _accessToken;
        var connection = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/config", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<StaffTrackingConfig>("TrackingConfigUpdated", cfg =>
        {
            lock (_gate) _config = cfg;
            ConfigChanged?.Invoke(cfg);
        });

        connection.On<BusinessClockConfig>("BusinessTimeUpdated", cfg =>
        {
            lock (_gate) _businessTime = cfg;
            BusinessTimeChanged?.Invoke(cfg);
        });

        connection.On<OrgStructureDto>("OrgStructureUpdated", org =>
        {
            lock (_gate) _org = org;
            OrgStructureChanged?.Invoke(org);
        });

        // The payload is {companyId, structureVersion} and nothing else; the app needs only the
        // fact, so it is read as a JsonElement and discarded rather than modelled.
        connection.On<JsonElement>("StaffRegistered", _ => StaffRosterChanged?.Invoke());

        connection.On<EffectiveAuthoritiesDto>("AuthoritiesUpdated", auth =>
        {
            lock (_gate) _authorities = auth;
            AuthoritiesChanged?.Invoke(auth);
        });

        connection.On<List<PolicemanDto>>("PolicemenUpdated", list =>
        {
            PolicemenChanged?.Invoke(list ?? []);
        });

        connection.Reconnected += async _ => await RaiseReconnectedAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Builds a fresh hub connection, disposing the previous one first so a restart never leaks the
    /// old WebSocket, then registers <see cref="OnConnectionClosedAsync"/> so a terminal close (after
    /// SignalR's automatic-reconnect attempts are exhausted) restarts rather than dying forever.
    /// </summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _connection;
            _connection = null;
            if (previous is not null)
            {
                previous.Closed -= OnConnectionClosedAsync;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            var connection = BuildConnection();
            connection.Closed += OnConnectionClosedAsync;
            try
            {
                await connection.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                connection.Closed -= OnConnectionClosedAsync;
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _connection = connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private Task OnConnectionClosedAsync(Exception? error)
    {
        // Return immediately: the reconnect loop disposes the just-closed connection and would
        // deadlock if it ran inside that connection's own Closed callback. Detach it instead.
        if (!_disposed && !_lifetime.IsCancellationRequested)
        {
            _ = Task.Run(ReconnectLoopAsync);
        }

        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync()
    {
        // WithAutomaticReconnect has already given up (default 4 attempts); take over and keep
        // trying on the same cadence until the link returns or the agent shuts down.
        while (!_disposed && !_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_reconnectDelay, _lifetime).ConfigureAwait(false);
                await ConnectAsync(_lifetime).ConfigureAwait(false);
                await RaiseReconnectedAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Server still unreachable — loop and retry.
            }
        }
    }

    private async Task RaiseReconnectedAsync()
    {
        var handler = ReconnectedAsync;
        if (handler is not null)
        {
            await handler().ConfigureAwait(false);
        }
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
        _disposed = true;
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            // Detach first so disposing does not trip the reconnect loop.
            connection.Closed -= OnConnectionClosedAsync;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectGate.Dispose();
    }
}
