using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed class TimeTrackSegmentViewModel
{
    public TimeTrackSegmentViewModel(TimeTrackSegmentItem item, double totalSeconds, CompanyClock clock)
    {
        Kind = NormalizeKind(item.Kind);
        Start = item.Start;
        End = item.End;
        DurationSeconds = Math.Max(0.001, item.DurationSeconds);
        Weight = totalSeconds > 0 ? DurationSeconds / totalSeconds : 0;

        // §5.4 / §2.5 — working is green; everything else is red. Rest, and time the engine could
        // not account for because the PC was off, asleep or the agent was not running, are one and
        // the same "not working" state. The owner asked for "only the bar box, red and green".
        Fill = Kind == "working" ? WorkingFill : RestFill;

        var range = $"{clock.FormatDateTime(Start)} – {clock.FormatDateTime(End)}";
        ToolTip = $"{KindLabel}: {range}";
    }

    private static readonly IBrush WorkingFill = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush RestFill = SolidColorBrush.Parse("#DC2626");

    public string Kind { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public double DurationSeconds { get; }
    public double Weight { get; }
    public IBrush Fill { get; }
    public string ToolTip { get; }

    public string KindLabel => Kind == "working" ? "Working" : "Rest";

    private static string NormalizeKind(string? kind)
        => string.Equals(kind, "working", StringComparison.OrdinalIgnoreCase) ? "working"
            : string.Equals(kind, "rest", StringComparison.OrdinalIgnoreCase) ? "rest"
                : "gap";
}

public sealed partial class TimeTrackViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public TimeTrackViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _log = services.Log;
    }

    public ObservableCollection<TimeTrackSegmentViewModel> Segments { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private string _leftLabel = "";
    [ObservableProperty] private string _rightLabel = "";
    [ObservableProperty] private string _periodCaption = "";
    [ObservableProperty] private bool _hasTimeline;
    [ObservableProperty] private string _workingSummary = "";
    [ObservableProperty] private string _restSummary = "";

    /// <summary>§2.5 — fraction 0..1 of the bar where "now" sits, drawn only when <see cref="HasNowMarker"/>.</summary>
    [ObservableProperty] private double _nowFraction;
    [ObservableProperty] private bool _hasNowMarker;
    [ObservableProperty] private string _nowLabel = "";

    public bool ShowEmpty => !IsLoading && !HasTimeline && string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnHasTimelineChanged(bool value)
        => OnPropertyChanged(nameof(ShowEmpty));

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        Segments.Clear();
        HasTimeline = false;
        ErrorMessage = null;
        EmptyMessage = null;
        LeftLabel = "";
        RightLabel = "";
        PeriodCaption = "";
        WorkingSummary = "";
        RestSummary = "";
        ClearNowMarker();
    }

    public async Task LoadAsync(
        Guid staffUserId,
        bool force,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        DateTime? businessStart,
        DateTime? businessEnd,
        CancellationToken ct = default)
    {
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasTimeline || ShowEmpty || ShowError))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!_session.HasToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Clear();
                ErrorMessage = "Sign in required.";
                IsLoading = false;
            });
            return;
        }

        if (fromUtc is null || toUtc is null || businessStart is null || businessEnd is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Clear();
                EmptyMessage = "Select a period in the calendar to view the time track timeline.";
                PeriodCaption = "All time";
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = null;
                _loadedToUtc = null;
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
        });

        try
        {
            var timeline = await _api.QueryTimeTrackTimelineAsync(
                    staffUserId, fromUtc.Value, toUtc.Value, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ApplyTimeline(timeline, businessStart.Value, businessEnd.Value);
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                IsLoading = false;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled by a newer selection or by leaving the route. Hand the section back rather
            // than leaving the spinner up: a newer load owns the flag once the generation moves.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Time track for {staffUserId:D} could not be loaded", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Clear();
                ErrorMessage = ApiError.Describe(ex, "Failed to load time track.");
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
            });
        }
    }

    private void Clear()
    {
        Segments.Clear();
        HasTimeline = false;
        ErrorMessage = null;
        EmptyMessage = null;
        LeftLabel = "";
        RightLabel = "";
        WorkingSummary = "";
        RestSummary = "";
        ClearNowMarker();
    }

    private void ClearNowMarker()
    {
        HasNowMarker = false;
        NowFraction = 0;
        NowLabel = "";
    }

    private void ApplyTimeline(
        TimeTrackTimeline timeline,
        DateTime businessStart,
        DateTime businessEnd)
    {
        Segments.Clear();

        var total = timeline.TotalSeconds > 0
            ? timeline.TotalSeconds
            : Math.Max(0.001, (timeline.To - timeline.From).TotalSeconds);

        foreach (var seg in timeline.Segments)
        {
            Segments.Add(new TimeTrackSegmentViewModel(seg, total, _clock));
        }

        HasTimeline = Segments.Count > 0;
        LeftLabel = FormatBusinessEnd(businessStart, isEnd: false);
        RightLabel = FormatBusinessEnd(businessEnd.Date.AddDays(1), isEnd: true);
        PeriodCaption = businessStart.Date == businessEnd.Date
            ? businessStart.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : $"{businessStart:dd MMM yyyy} – {businessEnd:dd MMM yyyy}";

        WorkingSummary = CompanyClock.FormatDuration(
            Segments.Where(s => s.Kind == "working").Sum(s => s.DurationSeconds));
        // §5.4 — rest is everything that is not working, including time the engine could not account for.
        RestSummary = CompanyClock.FormatDuration(
            Segments.Where(s => s.Kind != "working").Sum(s => s.DurationSeconds));

        // §2.5 — the bar span IS the server's period bounds; place "now" against those same instants.
        ApplyNowMarker(timeline.From, timeline.To);

        if (!HasTimeline)
        {
            EmptyMessage = "No time track data in this period.";
        }
    }

    /// <summary>
    /// §2.5 — where "now" sits on the bar, in the SAME UTC frame the server derived the period from
    /// (<c>timeline.From/To</c>). The fraction math is frame-independent, so it needs no company-local
    /// conversion; only the label is company time (§2.4). Absent for a period that does not contain now.
    /// </summary>
    private void ApplyNowMarker(DateTimeOffset from, DateTimeOffset to)
    {
        var span = (to - from).TotalSeconds;
        var nowUtc = DateTimeOffset.UtcNow;
        if (span <= 0 || nowUtc < from || nowUtc >= to)
        {
            ClearNowMarker();
            return;
        }

        NowFraction = Math.Clamp((nowUtc - from).TotalSeconds / span, 0, 1);
        NowLabel = $"Now {_clock.FormatTime(nowUtc)}";
        HasNowMarker = true;
    }

    private static string FormatBusinessEnd(DateTime localDayOrNextMidnight, bool isEnd)
    {
        // Left: D 00:00, Right: exclusive next-midnight shown as end-of-day 24:00 label.
        if (isEnd)
        {
            var day = localDayOrNextMidnight.Date.AddDays(-1);
            return day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + " 24:00";
        }

        return localDayOrNextMidnight.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + " 00:00";
    }
}
