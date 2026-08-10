using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed class TimeTrackStickerSegment
{
    public TimeTrackStickerSegment(string kind, double durationSeconds)
    {
        Kind = kind;
        DurationSeconds = Math.Max(0.001, durationSeconds);
        // §5.4 — working is green; rest, and time the PC was off/asleep or the agent was not
        // running, is red. "unknown" is neither: it is time outside the machine's lifetime — before
        // it joined, or still ahead of now — and is drawn as nothing, because red there asserted the
        // employee was idle through hours that had not happened yet.
        Brush = kind switch
        {
            "working" => WorkingFill,
            "unknown" => UnknownFill,
            _ => RestFill
        };
    }

    private static readonly IBrush WorkingFill = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush RestFill = SolidColorBrush.Parse("#DC2626");
    private static readonly IBrush UnknownFill = Brushes.Transparent;

    public string Kind { get; }
    public double DurationSeconds { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// A15 / §14.4 — the sticker is the staff member's proof that the tracking engine is running
/// correctly, so it states engine health first and draws the 24 h bar second.
///
/// The rule that matters: it never shows a stale bar. Whenever the state cannot be confirmed the
/// bar is replaced by the reason, because a dead capture pipeline that looks identical to a
/// healthy one is exactly the failure this screen exists to prevent.
///
/// State comes from <c>agent-health.json</c> when the machine writes one — it is the only source
/// that knows about capture and the outbox — and otherwise from <c>GET /api/tracking/health/me</c>,
/// which is self-scoped and therefore readable by a plain staff account.
/// </summary>
public sealed partial class TimeTrackStickerViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);

    private static readonly IBrush DotProtected = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush DotCatchingUp = SolidColorBrush.Parse("#D97706");
    private static readonly IBrush DotNotReporting = SolidColorBrush.Parse("#DC2626");
    private static readonly IBrush DotUnknown = SolidColorBrush.Parse("#94A3B8");

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly AgentHealthReader _health;
    private readonly UiLog _log;
    private readonly DispatcherTimer _timer;
    private Guid? _userId;
    private DateTimeOffset? _lastSyncUtc;
    private int _loadGeneration;
    private bool _disposed;

    public TimeTrackStickerViewModel(AppServices services, LocalAgentState state)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _health = services.AgentHealth;
        _log = services.Log;
        _session.SetActiveRole(state.Role);
        _userId = state.UserId;

        _timer = new DispatcherTimer { Interval = RefreshEvery };
        _timer.Tick += OnTick;
    }

    /// <summary>Set by the window from its own RequestOpenShell, so the menu and the double-click agree.</summary>
    public Action? OpenShellRequested { get; set; }

    public ObservableCollection<TimeTrackStickerSegment> Segments { get; } = [];

    [ObservableProperty] private bool _hasTimeline;
    [ObservableProperty] private AgentHealthStatus _status = AgentHealthStatus.Unknown;
    [ObservableProperty] private string _statusHeadline = "Checking…";
    [ObservableProperty] private string _statusDetail = "Reading the tracking engine's state.";

    /// <summary>The bar is only shown when it is current. Otherwise the reason takes its place.</summary>
    /// <summary>
    /// The bar is always on screen.
    ///
    /// It used to hide whenever health was not Protected or CatchingUp, on the reasoning that a
    /// stale bar is a lie. In practice the sticker simply vanished a few minutes after install and
    /// the employee had no idea whether anything was running — which is worse than an honest bar,
    /// and indistinguishable from the app having crashed. The status dot already states health, so
    /// the bar can stay and let the dot carry the caveat.
    /// </summary>
    public bool ShowBar => true;

    /// <summary>Shown alongside the bar whenever health is not clean, so the reason is still visible.</summary>
    public bool ShowStatusText => Status is not AgentHealthStatus.Protected;

    public IBrush StatusBrush => Status switch
    {
        AgentHealthStatus.Protected => DotProtected,
        AgentHealthStatus.CatchingUp => DotCatchingUp,
        AgentHealthStatus.NotReporting => DotNotReporting,
        _ => DotUnknown
    };

    /// <summary>
    /// Worked time so far in the company's day, and nothing else.
    ///
    /// It used to carry the engine's health headline and detail — internal wording about helpers,
    /// outboxes and sync state that an employee has no use for and can only find unsettling. The
    /// one number they have a legitimate interest in is how long they have worked today. Health
    /// still has a home: the coloured dot, and the right-click menu for anyone who wants the detail.
    /// </summary>
    public string ToolTipText => $"Worked today: {FormatWorked(WorkedSecondsToday)}";

    /// <summary>
    /// The same number, drawn ON the bar rather than only in a tooltip — the owner wants the total
    /// visible at a glance, not behind a hover. Resets at company midnight with the bar itself.
    /// </summary>
    public string WorkedLabel => FormatWorked(WorkedSecondsToday);

    /// <summary>Seconds of working time in the current business day, from the bar's own segments.</summary>
    public double WorkedSecondsToday
        => Segments.Where(s => string.Equals(s.Kind, "working", StringComparison.Ordinal))
            .Sum(s => s.DurationSeconds);

    private static string FormatWorked(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        if (!_disposed)
        {
            _timer.Start();
        }
    }

    /// <summary>
    /// Set by the window. Hiding is per-session only: the sticker reappears at next sign-in, so
    /// getting it out of the way is a convenience, not a way to switch monitoring off.
    /// </summary>
    public Action? HideRequested { get; set; }

    [RelayCommand]
    private void OpenShell() => OpenShellRequested?.Invoke();

    [RelayCommand]
    private void HideBar() => HideRequested?.Invoke();

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var gen = Interlocked.Increment(ref _loadGeneration);
        if (!_session.HasToken)
        {
            await ApplyAsync(
                gen,
                AgentHealthStatus.Unknown,
                "Not signed in",
                "This machine holds no session, so nothing is being recorded.",
                timeline: null);
            return;
        }

        // The engine's own account, when the machine writes one. It wins: it knows about capture
        // and the outbox, neither of which the server can distinguish from an idle employee.
        var file = _health.TryRead();

        AgentSelfHealth? remote = null;
        TimeTrackTimeline? timeline = null;
        Exception? failure = null;
        try
        {
            if (file is null)
            {
                remote = await _api.GetMyAgentHealthAsync(ct).ConfigureAwait(false);
            }

            if (_userId is null || _userId == Guid.Empty)
            {
                var me = await _api.MeAsync(ct).ConfigureAwait(false);
                _userId = me.Id;
                PersistUserId(me.Id);
            }

            // §4.5's one exception: a staff member may read their own timeline.
            //
            // The business day, not a rolling 24 hours. A rolling window means the bar silently
            // drops this time yesterday as the day goes on, so the same working morning reads
            // differently depending on when you look at it. A day that starts at company midnight
            // and resets there is the thing an employee can actually reason about.
            var dayStart = _clock.ToUtc(_clock.Today);
            var dayEnd = _clock.ToUtc(_clock.Today.AddDays(1));
            timeline = await _api
                .QueryTimeTrackTimelineAsync(_userId.Value, dayStart, dayEnd, ct)
                .ConfigureAwait(false);
            _lastSyncUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
            _log.Warn("Sticker could not confirm engine health with the server", ex);
        }

        var (status, headline, detail) = Evaluate(file, remote, failure);
        await ApplyAsync(gen, status, headline, detail, timeline);
    }

    /// <summary>
    /// The file first, the self-health endpoint second, and "cannot confirm" when neither answers.
    /// Nothing here ever guesses at health from the shape of the timeline: a bar that keeps drawing
    /// while the engine is dead is the exact failure §14.4 exists to prevent.
    /// </summary>
    private (AgentHealthStatus Status, string Headline, string Detail) Evaluate(
        AgentHealthFile? file, AgentSelfHealth? remote, Exception? failure)
    {
        var synced = _lastSyncUtc is { } sync ? $" · synced {_clock.FormatTime(sync)}" : string.Empty;
        if (file is not null)
        {
            var fromFile = AgentHealthReader.Evaluate(file, DateTimeOffset.UtcNow, _clock);
            return failure is null
                ? (fromFile.Status, fromFile.Headline, fromFile.Detail + synced)
                : (fromFile.Status, fromFile.Headline, $"{fromFile.Detail} Server not reachable.");
        }

        if (remote is null)
        {
            return (
                AgentHealthStatus.Unknown,
                "Cannot confirm",
                failure is null
                    ? "The tracking engine's state could not be read."
                    : ApiError.Describe(failure, "The server could not be reached."));
        }

        var status = remote.Status switch
        {
            "protected" => AgentHealthStatus.Protected,
            "catching_up" => AgentHealthStatus.CatchingUp,
            "not_reporting" => AgentHealthStatus.NotReporting,
            _ => AgentHealthStatus.Unknown
        };

        var headline = status switch
        {
            AgentHealthStatus.Protected => "Protected",
            AgentHealthStatus.CatchingUp => "Catching up",
            AgentHealthStatus.NotReporting => "Not reporting",
            _ => "Cannot confirm"
        };

        return (status, headline, DescribeRemote(remote) + synced);
    }

    /// <summary>
    /// What §14.4 asks the sticker to prove, in the order that matters: capture, upload, last sync.
    /// The server's own words come first when it has any.
    /// </summary>
    private string DescribeRemote(AgentSelfHealth health)
    {
        if (!string.IsNullOrWhiteSpace(health.StatusDetail))
        {
            return health.StatusDetail;
        }

        var parts = new List<string>
        {
            health.TrackingOk == false || health.HelperAlive == false
                ? "Capture not running"
                : "Capture OK"
        };

        if (health.PendingOutbox is { } pending)
        {
            parts.Add($"{pending} queued");
        }

        parts.Add(health.LastTimeTrackAt is { } tracked
            ? $"last record {_clock.FormatTime(tracked)}"
            : "nothing recorded yet");

        return string.Join(" · ", parts);
    }

    private async Task ApplyAsync(
        int generation,
        AgentHealthStatus status,
        string headline,
        string detail,
        TimeTrackTimeline? timeline)
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            Status = status;
            StatusHeadline = headline;
            StatusDetail = detail;
            ApplyTimeline(timeline);
        });

    private void ApplyTimeline(TimeTrackTimeline? timeline)
    {
        Segments.Clear();
        if (timeline is null)
        {
            // No fresh timeline: draw the track empty rather than removing it. The status dot says
            // why, and a bar that is present but blank cannot be mistaken for a healthy one.
            HasTimeline = false;
            Segments.Add(new TimeTrackStickerSegment("unknown", 1));
            OnPropertyChanged(nameof(ShowBar));
            OnPropertyChanged(nameof(ShowStatusText));
            // The tooltip totals the segments, so it must also reset when they empty out —
            // otherwise it kept showing yesterday's worked time over a blank bar.
            OnPropertyChanged(nameof(WorkedSecondsToday));
            OnPropertyChanged(nameof(WorkedLabel));
            OnPropertyChanged(nameof(ToolTipText));
            return;
        }

        foreach (var seg in timeline.Segments)
        {
            var kind = string.Equals(seg.Kind, "working", StringComparison.OrdinalIgnoreCase) ? "working"
                : string.Equals(seg.Kind, "rest", StringComparison.OrdinalIgnoreCase) ? "rest"
                    : string.Equals(seg.Kind, "unknown", StringComparison.OrdinalIgnoreCase) ? "unknown"
                        : "gap";
            Segments.Add(new TimeTrackStickerSegment(kind, seg.DurationSeconds));
        }

        HasTimeline = Segments.Count > 0;
        OnPropertyChanged(nameof(ShowBar));
        OnPropertyChanged(nameof(ShowStatusText));
        // The tooltip is derived from the segments, so it has to be re-read whenever they change.
        OnPropertyChanged(nameof(WorkedSecondsToday));
        OnPropertyChanged(nameof(WorkedLabel));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private void PersistUserId(Guid userId)
    {
        var state = _session.Reload();
        state.UserId = userId;
        try
        {
            _session.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not persist the sticker's user id", ex);
        }
    }

    private void OnTick(object? sender, EventArgs e)
        => RefreshAsync().FireAndForget(_log, "Sticker health refresh");

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _timer.Tick -= OnTick;
        _timer.Stop();
        Interlocked.Increment(ref _loadGeneration);
        return ValueTask.CompletedTask;
    }

    partial void OnStatusChanged(AgentHealthStatus value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ShowBar));
        OnPropertyChanged(nameof(ShowStatusText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    partial void OnStatusHeadlineChanged(string value) => OnPropertyChanged(nameof(StatusHeadline));

    partial void OnStatusDetailChanged(string value) => OnPropertyChanged(nameof(StatusDetail));

    partial void OnHasTimelineChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBar));
        OnPropertyChanged(nameof(ShowStatusText));
    }
}
