using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeroIconsAvalonia.Enums;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public enum ShellMode
{
    /// <summary>First resolve in flight and nothing cached to show yet.</summary>
    Loading,

    /// <summary>No stored session: the content host holds Login or Enrol, the nav is hidden.</summary>
    SignIn,

    Ready,

    /// <summary>Signed in, but the server never answered and there is nothing cached to fall back to.</summary>
    Degraded
}

/// <summary>
/// A12 / §4.7 — the one window's state machine. It owns the routes, the current content and the
/// identity header, and it is the single place that reacts to an authority change: recompute
/// capabilities, rebuild the nav, clamp the open route, reload staff. No window is created or
/// destroyed, which is what makes a mid-session promotion visible without a restart.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    /// <summary>How far back the diagnostics line looks. Older than this is history, not news.</summary>
    private static readonly TimeSpan ProblemWindow = TimeSpan.FromMinutes(5);

    private readonly AppServices _services;
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly AuthorityState _authority;
    private readonly CompanyClock _clock;
    private readonly StickerHost _sticker;
    private readonly UiLog _log;
    private readonly RealtimeCoordinator _realtime;
    private readonly IReadOnlyList<ShellRouteViewModel> _routes;
    private readonly ShellRouteViewModel _staffRoute;
    private readonly ShellRouteViewModel _noAccessRoute;

    private readonly DispatcherTimer _diagnostics;

    private CancellationToken _lifetime = CancellationToken.None;

    /// <summary>Scopes the loads a route starts, so navigating away drops them.</summary>
    private CancellationTokenSource? _routeCts;
    private int _retryIndex;

    /// <summary>
    /// True while the sticker is the entire UI. Starts true on a monitored machine: App does not
    /// show the shell there, so the default must match reality or ApplyStickerHandoff will never
    /// reveal the window for a leader or policeman.
    /// </summary>
    private bool _stickerOwnsScreen;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        _api = services.Api;
        _session = services.Session;
        _authority = services.Authority;
        _clock = services.Clock;
        _sticker = services.Sticker;
        _log = services.Log;
        _stickerOwnsScreen = _sticker.IsActive;
        _realtime = new RealtimeCoordinator(_api, _session, _authority, _clock, _log);

        Identity = new IdentityHeaderViewModel(services);
        StaffDirectory = new StaffDirectoryViewModel(services);
        StaffDetail = new StaffDetailViewModel(services);
        Leaderboard = new LeaderboardViewModel(services);
        Teams = new TeamsBoardViewModel(services);
        Codes = new TotpCodesViewModel(services);
        Settings = new SettingsViewModel(services);
        NoAccess = new NoAccessViewModel();
        Login = new LoginViewModel(services);
        Enroll = new EnrollViewModel(services);

        // Leaderboard is first, so it is the landing route for anyone who can see staff data (§1.3).
        _staffRoute = new ShellRouteViewModel(ShellRouteId.Staff, "Staff", IconType.Users, () => StaffDetail);
        _routes =
        [
            new ShellRouteViewModel(
                ShellRouteId.Leaderboard, "Leaderboard", IconType.ChartBar, () => Leaderboard),
            _staffRoute,
            new ShellRouteViewModel(ShellRouteId.Teams, "Teams", IconType.UserGroup, () => Teams),
            new ShellRouteViewModel(ShellRouteId.Codes, "Codes", IconType.Key, () => Codes),
            new ShellRouteViewModel(ShellRouteId.Settings, "Settings", IconType.Cog6Tooth, () => Settings)
        ];
        _noAccessRoute = new ShellRouteViewModel(
            ShellRouteId.NoAccess, "No access", IconType.ExclamationTriangle, () => NoAccess);

        StaffDirectory.SelectionChanged += OnStaffSelected;
        StaffDetail.OpenScreenshotViewerRequested += OnOpenScreenshotViewer;
        Login.Authenticated += OnAuthenticated;
        Login.EnrollRequested += () => ShowSignIn(Enroll);
        Enroll.Authenticated += OnAuthenticated;
        Enroll.SignInRequested += () => ShowSignIn(Login);
        _authority.Changed += OnAuthorityChanged;
        _session.Invalidated += OnSessionInvalidated;
        _realtime.OrgStructureUpdated += OnOrgStructureUpdated;
        _realtime.StaffRosterChanged += OnStaffRosterChanged;
        _realtime.PolicemenUpdated += Settings.ApplyPolicemenRealtime;
        _realtime.BusinessTimeUpdated += Settings.ApplyBusinessTimeRealtime;
        // §8.2 — a zone change has to reach the screens that are already open, not just new ones.
        _clock.Changed += OnClockChanged;

        // The app reporting on itself, in the same strip as the retry (§12.3 forbids toasts).
        _diagnostics = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _diagnostics.Tick += OnDiagnosticsTick;
    }

    public IdentityHeaderViewModel Identity { get; }
    public StaffDirectoryViewModel StaffDirectory { get; }
    public StaffDetailViewModel StaffDetail { get; }
    public LeaderboardViewModel Leaderboard { get; }
    public TeamsBoardViewModel Teams { get; }
    public TotpCodesViewModel Codes { get; }
    public SettingsViewModel Settings { get; }
    public NoAccessViewModel NoAccess { get; }
    public LoginViewModel Login { get; }
    public EnrollViewModel Enroll { get; }

    /// <summary>The nav renders exactly this — visibility is derived, never a stored flag.</summary>
    public ObservableCollection<ShellRouteViewModel> VisibleRoutes { get; } = [];

    [ObservableProperty] private ShellMode _mode = ShellMode.Loading;
    [ObservableProperty] private ShellCapabilities _capabilities = ShellCapabilities.None;
    [ObservableProperty] private ShellRouteViewModel? _currentRoute;
    [ObservableProperty] private object? _currentContent;
    [ObservableProperty] private string? _problemMessage;
    [ObservableProperty] private string? _diagnosticsLine;

    /// <summary>§3.4 — the full-screen screenshot viewer, drawn over the whole window when open.</summary>
    [ObservableProperty] private ScreenshotViewerViewModel? _screenshotViewer;

    public bool IsScreenshotViewerOpen => ScreenshotViewer is not null;

    public bool ShowNav => Mode is ShellMode.Ready or ShellMode.Degraded;
    public bool IsResolving => Mode == ShellMode.Loading;
    public bool ShowProblem => !string.IsNullOrWhiteSpace(ProblemMessage) || ShowDiagnostics;
    public bool ShowDiagnostics => !string.IsNullOrWhiteSpace(DiagnosticsLine);
    public bool ShowProblemMessage => !string.IsNullOrWhiteSpace(ProblemMessage);
    public string WindowTitle => Mode == ShellMode.Ready ? $"Teamscop · {Capabilities.RoleLabel}" : "Teamscop";

    /// <summary>
    /// Start-up, from ShellWindow.Opened. Local state decides the first frame (microseconds), then
    /// every server fact is fetched in parallel under one 10 s budget. Nothing here blocks paint.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _lifetime = ct;
        _diagnostics.Start();
        Leaderboard.SetLifetime(ct);
        StaffDetail.SetLifetime(ct);
        var state = _session.Reload();
        Identity.Apply(state);
        NoAccess.UserLine = string.IsNullOrWhiteSpace(state.Username)
            ? state.CompanyName ?? string.Empty
            : $"{state.Username} · {state.CompanyName}";

        // Local state only: a stored token still bound to this hardware, or the login screen.
        if (!_session.IsRegistered)
        {
            ShowSignIn(Login);
            return;
        }

        Mode = ShellMode.Loading;
        await ResolveAsync(ct);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        _retryIndex = 0;
        await ResolveAsync(_lifetime);
    }

    [RelayCommand]
    private void Navigate(ShellRouteViewModel? route)
    {
        if (route is null || route == CurrentRoute)
        {
            return;
        }

        Show(route);
    }

    /// <summary>
    /// The nav chevron the owner calls "the staff dropdown". Opening it is a request to see who is
    /// there now, so it re-reads — it used to do no network work at all, which is how a roster
    /// fetched once at cold start could stay empty for the rest of the session (defect 3).
    /// </summary>
    [RelayCommand]
    private void ToggleStaffList()
    {
        StaffDirectory.ToggleExpanded();
        if (StaffDirectory.IsExpanded)
        {
            ReloadStaff(force: true, _lifetime);
        }
    }

    /// <summary>
    /// A failure anywhere in the app writes to <see cref="UiLog"/>; this is where the operator can
    /// see that it happened. Not an alert about employee data (§12.3) — the app about itself.
    /// </summary>
    private void OnDiagnosticsTick(object? sender, EventArgs e)
    {
        var problem = _log.LastProblem(ProblemWindow);
        DiagnosticsLine = problem is null
            ? null
            : $"Last problem {_clock.FormatTime(problem.At)} — {problem.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        _diagnostics.Stop();
        _diagnostics.Tick -= OnDiagnosticsTick;
        _authority.Changed -= OnAuthorityChanged;
        _session.Invalidated -= OnSessionInvalidated;
        _realtime.OrgStructureUpdated -= OnOrgStructureUpdated;
        _realtime.StaffRosterChanged -= OnStaffRosterChanged;
        _realtime.PolicemenUpdated -= Settings.ApplyPolicemenRealtime;
        _realtime.BusinessTimeUpdated -= Settings.ApplyBusinessTimeRealtime;
        _clock.Changed -= OnClockChanged;
        StaffDetail.OpenScreenshotViewerRequested -= OnOpenScreenshotViewer;
        CloseScreenshotViewer();
        CancelRoute();
        StaffDirectory.StopPresence();
        Codes.Dispose();
        await _realtime.DisposeAsync();
    }

    /// <summary>
    /// §8.2 — every displayed time is company time, so a zone change is not just a setting: the
    /// screens already open are showing the old zone and both period filters are holding UTC
    /// bounds derived from it. May arrive off the UI thread.
    /// </summary>
    private void OnClockChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyClockChange();
            return;
        }

        Dispatcher.UIThread.Post(ApplyClockChange);
    }

    private void ApplyClockChange()
    {
        StaffDetail.ReapplyClock();
        Leaderboard.ReapplyClock();
    }

    // ---- resolution ---------------------------------------------------------------------------

    private async Task ResolveAsync(CancellationToken ct)
    {
        ProblemMessage = null;
        if (_authority.Authorities is null)
        {
            Mode = ShellMode.Loading;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ResolveTimeout);
        var token = cts.Token;

        var me = _api.MeAsync(token);
        var authorities = _api.GetMyAuthoritiesAsync(token);
        var placement = _api.GetMyPlacementAsync(token);
        var businessTime = _api.GetBusinessTimeAsync(token);

        try
        {
            await Task.WhenAll(me, authorities, placement, businessTime).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Individual results are inspected below; this only stops WhenAll from rethrowing.
            _log.Debug($"Shell resolve completed with failures: {ex.GetType().Name}");
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (me.IsCompletedSuccessfully)
        {
            ApplyProfile(me.Result);
        }

        if (businessTime.IsCompletedSuccessfully)
        {
            _clock.Apply(businessTime.Result);
        }

        if (!authorities.IsCompletedSuccessfully)
        {
            EnterProblem(authorities, ct);
            return;
        }

        _authority.Apply(
            authorities.Result,
            placement.IsCompletedSuccessfully ? placement.Result : null);

        _retryIndex = 0;
        Mode = ShellMode.Ready;
        ApplyCapabilities();
        ApplyStickerHandoff();

        _realtime.StartAsync(ct).FireAndForget(_log, "Live updates");

        // Not forced: a cold start has nothing loaded anyway, and forcing would re-fetch the roster
        // the Today route has just pulled through the same cache.
        ReloadStaff(force: false, ct);
    }

    /// <summary>No view package means no staff list to fetch — one fewer round trip per start (§15.2).</summary>
    private void ReloadStaff(bool force, CancellationToken ct)
    {
        if (Capabilities.CanSeeAnyStaffData)
        {
            StaffDirectory.ReloadAsync(force, ct).FireAndForget(_log, "Staff list");
        }
    }

    private void EnterProblem(Task failed, CancellationToken ct)
    {
        var ex = failed.Exception?.GetBaseException();
        var detail = ex is not null
            ? ApiError.Describe(ex, "Could not reach the server.")
            : failed.IsCanceled
                ? "The server did not answer in time."
                : "Could not reach the server.";
        _log.Warn($"Shell could not resolve authorities — {detail}", ex);

        if (ex is not null && ApiError.IsUnauthorized(ex))
        {
            _session.Invalidate();
            return;
        }

        if (_authority.Authorities is null)
        {
            Mode = ShellMode.Degraded;
            CurrentContent = null;
            ProblemMessage = detail;
        }
        else
        {
            // Capabilities from earlier in this session still hold — keep the workspace usable.
            ProblemMessage = $"Working from a cached session — {detail}";
        }

        ScheduleRetry(ct);
    }

    private void ScheduleRetry(CancellationToken ct)
    {
        if (_retryIndex >= RetryDelays.Length)
        {
            return;
        }

        var delay = RetryDelays[_retryIndex++];
        RetryAfterAsync(delay, ct).FireAndForget(_log, "Shell retry");
    }

    private async Task RetryAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ResolveAsync(ct);
    }

    private void ApplyProfile(AuthUser me)
    {
        var state = _session.State;
        state.Username = me.Username;
        state.CompanyName = me.Company.Name;
        state.CompanyId = me.Company.Id;
        state.Role = me.Role;
        state.DeviceKey = me.DeviceKey;
        state.UserId = me.Id;
        state.CompanyAvatarUrl = me.Company.AvatarUrl;
        try
        {
            _session.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not persist the refreshed profile", ex);
        }

        Identity.Apply(state);
        NoAccess.UserLine = $"{state.Username} · {state.CompanyName}";
    }

    // ---- capability-driven structure ----------------------------------------------------------

    /// <summary>
    /// The runtime role change. Authorities moved, so everything derived from them is rebuilt in
    /// place: capabilities, the nav, the open route, the staff sections and the staff list.
    /// </summary>
    private void OnAuthorityChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyAuthorityChange();
            return;
        }

        Dispatcher.UIThread.Post(ApplyAuthorityChange);
    }

    private void ApplyAuthorityChange()
    {
        if (Mode is ShellMode.SignIn)
        {
            return;
        }

        Mode = ShellMode.Ready;
        ApplyCapabilities();
        ApplyStickerHandoff();
        ReloadStaff(force: true, _lifetime);
    }

    private void ApplyCapabilities()
    {
        Capabilities = _authority.Capabilities;
        Identity.Apply(Capabilities, _authority.Placement);
        StaffDirectory.ApplyCapabilities(Capabilities);
        // §5.1 — presence badges poll only while the caller can see staff data.
        if (Capabilities.CanSeeAnyStaffData)
        {
            StaffDirectory.StartPresence();
        }
        else
        {
            StaffDirectory.StopPresence();
        }

        _staffRoute.Label = StaffDirectory.Title;
        StaffDetail.ApplyCapabilities(Capabilities);
        // §7.1 — plain staff have no workspace, so the shell must stay hidden even when a stray
        // open path (tray, second launch, sticker double-click) reaches ShowShell. Feed the gate.
        _sticker.SetHasWorkspace(Capabilities.HasWorkspace);
        RebuildRoutes();
    }

    private void RebuildRoutes()
    {
        VisibleRoutes.Clear();
        foreach (var route in _routes.Where(r => IsVisible(r.Id, Capabilities)))
        {
            VisibleRoutes.Add(route);
        }

        if (CurrentRoute is { } current && VisibleRoutes.Contains(current))
        {
            return;
        }

        Show(VisibleRoutes.FirstOrDefault() ?? _noAccessRoute);
    }

    /// <summary>Route visibility (§4.7). Derived from capabilities on every read — never stored.</summary>
    private static bool IsVisible(ShellRouteId id, ShellCapabilities capabilities) => id switch
    {
        ShellRouteId.Staff => capabilities.CanSeeAnyStaffData,
        ShellRouteId.Leaderboard => capabilities.CanViewTimeTrack,
        ShellRouteId.Teams => capabilities.IsAdmin || capabilities.CanManageTeams,
        ShellRouteId.Codes => capabilities.CanIssueCodes,
        ShellRouteId.Settings => capabilities.IsAdmin,
        _ => false
    };

    private void Show(ShellRouteViewModel route)
    {
        foreach (var candidate in _routes)
        {
            candidate.IsActive = candidate == route;
        }

        _noAccessRoute.IsActive = route == _noAccessRoute;

        // Whatever the route being left had in flight is dropped here: a response that lands after
        // the user has moved on must never repaint the screen that replaced it.
        var previous = CurrentRoute?.Id;
        CancelRoute();
        if (previous == ShellRouteId.Staff)
        {
            StaffDetail.Suspend();
        }
        else if (previous == ShellRouteId.Leaderboard)
        {
            Leaderboard.Suspend();
        }

        CurrentRoute = route;
        CurrentContent = route.Content();

        // Only the open Codes route polls; every other route leaves its timer stopped (§15.2).
        Codes.SetActive(route.Id == ShellRouteId.Codes);

        _routeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var ct = _routeCts.Token;
        switch (route.Id)
        {
            case ShellRouteId.Staff:
                // Entering the route is a request for the current roster, not the one this window
                // happened to load at start-up (defect 3).
                ReloadStaff(force: true, ct);
                StaffDetail.Open();
                break;
            case ShellRouteId.Leaderboard:
                Leaderboard.Open();
                break;
            case ShellRouteId.Teams:
                Teams.LoadAsync(ct).FireAndForget(_log, "Teams board");
                break;
            case ShellRouteId.Codes:
                Codes.LoadAsync(ct).FireAndForget(_log, "Codes");
                break;
            case ShellRouteId.Settings:
                Settings.LoadAsync(ct).FireAndForget(_log, "Settings");
                break;
        }
    }

    /// <summary>
    /// Cancel before dispose: a token whose source is already cancelled short-circuits every later
    /// registration, so the in-flight load unwinds and the linked registration on the shell's
    /// lifetime token is released instead of accumulating one per navigation.
    /// </summary>
    private void CancelRoute()
    {
        var cts = _routeCts;
        _routeCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// §4.4 — a session with no workspace has nothing to show, so the sticker becomes the whole
    /// UI. A promotion later in the session brings the window straight back.
    /// </summary>
    private void ApplyStickerHandoff()
    {
        // Idempotent: covers the machine that had no session when the process started and has
        // just signed in as staff, which is when the sticker first becomes due.
        _sticker.Show();
        if (!_sticker.IsActive)
        {
            return;
        }

        // The second reporting channel must follow the sticker, not the process launch. When the
        // app started before enrolment, the startup branch never called Start(), so a machine's
        // whole first day had no app channel — a stopped service on install day read grey instead
        // of black. Start() is a no-op when already running.
        _services.StatusReporter.Start();

        if (Capabilities.HasWorkspace)
        {
            if (_stickerOwnsScreen)
            {
                _stickerOwnsScreen = false;
                _sticker.ShowShell();
            }

            return;
        }

        if (_stickerOwnsScreen)
        {
            return;
        }

        _stickerOwnsScreen = true;
        _sticker.HideShell();
        _log.Info("No workspace for this account — the sticker is the whole UI (§4.4)");
    }

    // ---- events -------------------------------------------------------------------------------

    private void OnStaffSelected(StaffListItemViewModel? staff)
    {
        StaffDetail.Show(staff);
        if (staff is null || CurrentRoute?.Id == ShellRouteId.Staff)
        {
            return;
        }

        if (VisibleRoutes.Contains(_staffRoute))
        {
            Show(_staffRoute);
        }
    }

    /// <summary>
    /// §3.4 — opens the screenshot viewer over the entire window (nav, section nav and title bar),
    /// which is what makes it a real full-screen mode rather than a panel inside the content column.
    /// </summary>
    private void OnOpenScreenshotViewer(IReadOnlyList<ScreenshotMetaItem> metas, int index)
    {
        CloseScreenshotViewer();
        var viewer = new ScreenshotViewerViewModel(_services, metas, index);
        viewer.CloseRequested += CloseScreenshotViewer;
        ScreenshotViewer = viewer;
    }

    private void CloseScreenshotViewer()
    {
        if (ScreenshotViewer is not { } viewer)
        {
            return;
        }

        ScreenshotViewer = null;
        viewer.CloseRequested -= CloseScreenshotViewer;
        viewer.DisposeCaches();
    }

    private void OnOrgStructureUpdated()
    {
        Identity.Apply(Capabilities, _authority.Placement);
        ReloadStaff(force: true, _lifetime);
    }

    /// <summary>
    /// Defect 3 — someone enrolled. Admins also receive the org chart and refresh through the
    /// handler above; this is the contentless nudge that reaches everyone else, so a team leader
    /// and an approval-only policeman see the new face without restarting the app either.
    /// </summary>
    private void OnStaffRosterChanged() => ReloadStaff(force: true, _lifetime);

    private void OnAuthenticated(AuthSession session)
    {
        _session.SetActiveRole(session.User.Role);
        ProblemMessage = null;
        _retryIndex = 0;
        InitializeAsync(_lifetime).FireAndForget(_log, "Shell start-up");
    }

    /// <summary>The stored token is dead: back to the login screen, same window (§3.2).</summary>
    private void OnSessionInvalidated()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SignOut();
            return;
        }

        Dispatcher.UIThread.Post(SignOut);
    }

    private void SignOut()
    {
        // Mode first: AuthorityState.Clear() raises Changed synchronously, and an authority change
        // handled in any other mode would hand the screen back to the sticker — hiding the very
        // window the user has to sign in from.
        ShowSignIn(Login);
        CloseScreenshotViewer();
        CancelRoute();
        _authority.Clear();
        StaffDirectory.StopPresence();
        StaffDirectory.Clear();
        VisibleRoutes.Clear();
        CurrentRoute = null;
        ProblemMessage = null;
        Capabilities = ShellCapabilities.None;
        _stickerOwnsScreen = false;
        _retryIndex = 0;
    }

    private void ShowSignIn(object content)
    {
        Mode = ShellMode.SignIn;
        CurrentContent = content;
        // Nothing to poll without a session; leaving the timers running would just log 401s.
        Codes.SetActive(false);
        StaffDetail.Suspend();
        Leaderboard.Suspend();
    }

    partial void OnModeChanged(ShellMode value)
    {
        OnPropertyChanged(nameof(ShowNav));
        OnPropertyChanged(nameof(IsResolving));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnCapabilitiesChanged(ShellCapabilities value) => OnPropertyChanged(nameof(WindowTitle));

    partial void OnScreenshotViewerChanged(ScreenshotViewerViewModel? value)
        => OnPropertyChanged(nameof(IsScreenshotViewerOpen));

    partial void OnProblemMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowProblem));
        OnPropertyChanged(nameof(ShowProblemMessage));
    }

    partial void OnDiagnosticsLineChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowProblem));
        OnPropertyChanged(nameof(ShowDiagnostics));
    }
}
