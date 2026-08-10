using Avalonia.Threading;
using Teamscop.App.Composition;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Services;

/// <summary>
/// Exactly one <see cref="ConfigRealtimeClient"/> for the process. Server pushes land here, are
/// marshalled to the UI thread once, and are applied to the single <see cref="AuthorityState"/> /
/// <see cref="CompanyClock"/> — replacing the two near-identical EnsureRealtimeAsync copies that
/// each owned their own connection and their own copy of the apply logic.
/// </summary>
public sealed class RealtimeCoordinator : IAsyncDisposable
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly AuthorityState _authority;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;

    /// <summary>Start-up and its retries can overlap; without this both would open a connection.</summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private ConfigRealtimeClient? _client;

    public RealtimeCoordinator(
        TeamscopApi api, SessionStore session, AuthorityState authority, CompanyClock clock, UiLog log)
    {
        _api = api;
        _session = session;
        _authority = authority;
        _clock = clock;
        _log = log;
    }

    /// <summary>Teams, leaders or membership moved — visible staff has to be re-read.</summary>
    public event Action? OrgStructureUpdated;

    /// <summary>
    /// Defect 3 — a new employee enrolled. The admin also gets <see cref="OrgStructureUpdated"/>,
    /// but a team leader and an approval-only policeman are outside the management group and would
    /// otherwise never learn: their staff list only ever loaded at cold start, so the dropdown they
    /// opened stayed empty until the next restart.
    /// </summary>
    public event Action? StaffRosterChanged;

    public event Action<IReadOnlyList<PolicemanDto>>? PolicemenUpdated;

    /// <summary>The company zone moved; the clock is already updated when this fires.</summary>
    public event Action<BusinessClockConfig>? BusinessTimeUpdated;

    /// <summary>Connected at least once. Live updates are a bonus, never a precondition.</summary>
    public bool IsConnected => _client is not null;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var token = _session.AccessToken;
        if (string.IsNullOrWhiteSpace(token) || _client is not null)
        {
            return;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked under the gate: the shell retries resolution on a schedule, and two
            // overlapping starts would otherwise both pass the check and leak a live connection
            // with handlers attached, applying every server push twice.
            if (_client is not null)
            {
                return;
            }

            var client = new ConfigRealtimeClient(_session.ApiBaseUrl);
            client.OrgStructureChanged += OnOrgStructure;
            client.StaffRosterChanged += OnStaffRosterChanged;
            client.AuthoritiesChanged += OnAuthorities;
            client.PolicemenChanged += OnPolicemen;
            client.BusinessTimeChanged += OnBusinessTime;
            client.ReconnectedAsync += RefreshAsync;
            try
            {
                await client.StartAsync(token, ct).ConfigureAwait(false);
                _client = client;
            }
            catch (Exception ex)
            {
                _log.Warn("Live updates unavailable — the app runs without them", ex);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Re-pulls both halves of the caller's identity. Used at start-up and on reconnect.</summary>
    public async Task RefreshAsync()
    {
        if (!_session.HasToken)
        {
            return;
        }

        EffectiveAuthoritiesDto? authorities = null;
        MyOrgPlacementDto? placement = null;

        try
        {
            authorities = await _api.GetMyAuthoritiesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not refresh authorities", ex);
        }

        try
        {
            placement = await _api.GetMyPlacementAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not refresh org placement", ex);
        }

        // AuthorityState raises Changed on the calling thread, so apply from the UI thread.
        await Dispatcher.UIThread.InvokeAsync(() => _authority.Apply(authorities, placement));
        await Dispatcher.UIThread.InvokeAsync(() => OrgStructureUpdated?.Invoke());
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
        {
            return;
        }

        var client = _client;
        _client = null;
        client.OrgStructureChanged -= OnOrgStructure;
        client.StaffRosterChanged -= OnStaffRosterChanged;
        client.AuthoritiesChanged -= OnAuthorities;
        client.PolicemenChanged -= OnPolicemen;
        client.BusinessTimeChanged -= OnBusinessTime;
        client.ReconnectedAsync -= RefreshAsync;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    private void OnOrgStructure(OrgStructureDto unused)
        => RefreshAsync().FireAndForget(_log, "Org structure refresh");

    /// <summary>
    /// Contentless by design, so there is nothing to apply — only a roster to re-read. Marshalled
    /// like every other push: the handler reloads a collection the nav is bound to.
    /// </summary>
    private void OnStaffRosterChanged()
        => Dispatcher.UIThread.Post(() => StaffRosterChanged?.Invoke());

    private void OnAuthorities(EffectiveAuthoritiesDto authorities)
        => Dispatcher.UIThread.Post(() => _authority.Apply(authorities));

    private void OnPolicemen(IReadOnlyList<PolicemanDto> list)
        => Dispatcher.UIThread.Post(() => PolicemenUpdated?.Invoke(list));

    private void OnBusinessTime(BusinessClockConfig config)
        => Dispatcher.UIThread.Post(() =>
        {
            _clock.Apply(config);
            BusinessTimeUpdated?.Invoke(config);
        });
}
