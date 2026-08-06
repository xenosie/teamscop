using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class TimeTrackSegmentViewModel : ObservableObject
{
    public TimeTrackSegmentViewModel(TimeTrackSegmentItem item, double totalSeconds)
    {
        Kind = NormalizeKind(item.Kind);
        Start = item.Start;
        End = item.End;
        DurationSeconds = Math.Max(0.001, item.DurationSeconds);
        Weight = totalSeconds > 0 ? DurationSeconds / totalSeconds : 0;
        Brush = Kind switch
        {
            "working" => WorkingFill,
            "rest" => RestFill,
            _ => Brushes.Transparent
        };
        ToolTip = $"{KindLabel}: {FormatSpan(Start)} – {FormatSpan(End)}";
    }

    private static readonly IBrush WorkingFill = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush RestFill = SolidColorBrush.Parse("#DC2626");

    public string Kind { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public double DurationSeconds { get; }
    public double Weight { get; }
    public IBrush Brush { get; }
    public string ToolTip { get; }

    public string KindLabel => Kind switch
    {
        "working" => "Working",
        "rest" => "Rest",
        _ => "No heartbeat"
    };

    private static string NormalizeKind(string? kind)
    {
        if (string.Equals(kind, "working", StringComparison.OrdinalIgnoreCase))
        {
            return "working";
        }

        if (string.Equals(kind, "rest", StringComparison.OrdinalIgnoreCase))
        {
            return "rest";
        }

        return "gap";
    }

    private static string FormatSpan(DateTimeOffset t)
        => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

public sealed partial class TimeTrackViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public TimeTrackViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        Chain = new ChainHealthViewModel(apiBaseUrl);
    }

    public ChainHealthViewModel Chain { get; }

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
    [ObservableProperty] private string _gapSummary = "";

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
        Chain.Reset();
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
        GapSummary = "";
    }

    public async Task LoadAsync(
        Guid staffUserId,
        bool force,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        DateTime? businessStart,
        DateTime? businessEnd)
    {
        _ = Chain.LoadAsync(staffUserId);
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasTimeline || ShowEmpty || ShowError))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Segments.Clear();
                HasTimeline = false;
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
            });
            return;
        }

        if (fromUtc is null || toUtc is null || businessStart is null || businessEnd is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Segments.Clear();
                HasTimeline = false;
                ErrorMessage = null;
                EmptyMessage = "Select a period in the calendar to view the time track timeline.";
                LeftLabel = "";
                RightLabel = "";
                PeriodCaption = "All time";
                WorkingSummary = "";
                RestSummary = "";
                GapSummary = "";
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = null;
                _loadedToUtc = null;
            });
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
        });

        try
        {
            using var api = new TrackingApiClient(_apiBaseUrl);
            var timeline = await api.QueryTimeTrackTimelineAsync(
                    state.AccessToken, staffUserId, fromUtc.Value, toUtc.Value)
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
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Segments.Clear();
                HasTimeline = false;
                ErrorMessage = FormatError(ex);
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
            });
        }
    }

    private void ApplyTimeline(TimeTrackTimeline timeline, DateTime businessStart, DateTime businessEnd)
    {
        Segments.Clear();
        var total = timeline.TotalSeconds > 0
            ? timeline.TotalSeconds
            : Math.Max(0.001, (timeline.To - timeline.From).TotalSeconds);

        foreach (var seg in timeline.Segments)
        {
            Segments.Add(new TimeTrackSegmentViewModel(seg, total));
        }

        HasTimeline = Segments.Count > 0;
        LeftLabel = FormatBusinessEnd(businessStart, isEnd: false);
        RightLabel = FormatBusinessEnd(businessEnd.Date.AddDays(1), isEnd: true);
        PeriodCaption = businessStart.Date == businessEnd.Date
            ? businessStart.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : $"{businessStart:dd MMM yyyy} – {businessEnd:dd MMM yyyy}";

        var working = Segments.Where(s => s.Kind == "working").Sum(s => s.DurationSeconds);
        var rest = Segments.Where(s => s.Kind == "rest").Sum(s => s.DurationSeconds);
        var gap = Segments.Where(s => s.Kind == "gap").Sum(s => s.DurationSeconds);
        WorkingSummary = FormatDuration(working);
        RestSummary = FormatDuration(rest);
        GapSummary = FormatDuration(gap);

        if (!HasTimeline)
        {
            EmptyMessage = "No time track data in this period.";
        }
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

    private static string FormatDuration(double seconds)
    {
        if (seconds < 1)
        {
            return "0m";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        return $"{Math.Max(1, (int)Math.Round(ts.TotalMinutes))}m";
    }

    private static string FormatError(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return "Could not reach the server.";
        }

        var msg = ex.Message;
        if (msg.Contains("403", StringComparison.Ordinal) || msg.Contains("timetrack", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(msg) ? "Not allowed to view timetrack." : msg;
        }

        return string.IsNullOrWhiteSpace(msg) ? "Failed to load time track." : msg;
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
