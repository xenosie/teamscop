using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class StaffSummaryTopUrlViewModel : ObservableObject
{
    public int Rank { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public int VisitCount { get; init; }
    public string DisplayLine => string.IsNullOrWhiteSpace(Title)
        ? Url
        : $"{Url}";
}

public sealed partial class StaffSummaryViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public StaffSummaryViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
    }

    public ObservableCollection<StaffSummaryTopUrlViewModel> TopUrls { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private string _staffName = "";
    [ObservableProperty] private string _workedSentence = "";
    [ObservableProperty] private string _topUrlsIntro = "";
    [ObservableProperty] private bool _hasContent;
    [ObservableProperty] private bool _hasTopUrls;

    public bool ShowEmpty => !IsLoading && !HasContent && string.IsNullOrWhiteSpace(ErrorMessage);
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

    partial void OnHasContentChanged(bool value)
        => OnPropertyChanged(nameof(ShowEmpty));

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        StaffName = "";
        WorkedSentence = "";
        TopUrlsIntro = "";
        TopUrls.Clear();
        HasTopUrls = false;
        HasContent = false;
        ErrorMessage = null;
        EmptyMessage = null;
    }

    public async Task LoadAsync(
        Guid staffUserId,
        string staffName,
        bool force,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        DateTime? businessStart,
        DateTime? businessEnd)
    {
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasContent || ShowEmpty || ShowError))
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
                ClearBody();
                ErrorMessage = "Sign in required.";
                IsLoading = false;
            });
            return;
        }

        StaffName = staffName;
        if (fromUtc is null || toUtc is null || businessStart is null || businessEnd is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ClearBody();
                EmptyMessage = "Select a period in the calendar to view this staff summary.";
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
            // Partial packages: load each independently so one 403 does not blank the summary.
            TimeTrackTimeline? timeline = null;
            IReadOnlyList<BrowsingTopUrlItem> topUrls = [];
            try
            {
                timeline = await api.QueryTimeTrackTimelineAsync(
                    state.AccessToken, staffUserId, fromUtc.Value, toUtc.Value).ConfigureAwait(false);
            }
            catch (ApiClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Missing view_timetrack — omit worked time.
            }

            try
            {
                topUrls = await api.QueryBrowsingTopUrlsAsync(
                    state.AccessToken, staffUserId, take: 3, from: fromUtc, to: toUtc).ConfigureAwait(false);
            }
            catch (ApiClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Missing view_browser_history — omit top URLs.
            }

            var workedSeconds = timeline?.Segments
                .Where(s => string.Equals(s.Kind, "working", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.DurationSeconds) ?? 0;
            var periodLabel = FormatPeriod(businessStart.Value, businessEnd.Value);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                WorkedSentence =
                    $"This staff worked {FormatDuration(workedSeconds)} during {periodLabel}.";
                TopUrlsIntro = "And his top visiting URLs in Chrome is ;";
                TopUrls.Clear();
                var rank = 1;
                foreach (var u in topUrls)
                {
                    TopUrls.Add(new StaffSummaryTopUrlViewModel
                    {
                        Rank = rank++,
                        Url = u.Url,
                        Title = u.Title ?? "",
                        VisitCount = u.VisitCount
                    });
                }

                HasTopUrls = TopUrls.Count > 0;
                if (!HasTopUrls)
                {
                    TopUrlsIntro = "And his top visiting URLs in Chrome is ; (none in this period)";
                }

                HasContent = true;
                EmptyMessage = null;
                ErrorMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ClearBody();
                ErrorMessage = FormatError(ex);
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
            });
        }
    }

    private void ClearBody()
    {
        WorkedSentence = "";
        TopUrlsIntro = "";
        TopUrls.Clear();
        HasTopUrls = false;
        HasContent = false;
        EmptyMessage = null;
        ErrorMessage = null;
    }

    private static string FormatPeriod(DateTime start, DateTime end)
        => start.Date == end.Date
            ? start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : $"{start:dd MMM yyyy} – {end:dd MMM yyyy}";

    private static string FormatDuration(double seconds)
    {
        if (seconds < 1)
        {
            return "0 minutes";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
        {
            var h = (int)ts.TotalHours;
            var m = ts.Minutes;
            return m > 0 ? $"{h} hour{(h == 1 ? "" : "s")} {m} minute{(m == 1 ? "" : "s")}"
                : $"{h} hour{(h == 1 ? "" : "s")}";
        }

        var mins = Math.Max(1, (int)Math.Round(ts.TotalMinutes));
        return $"{mins} minute{(mins == 1 ? "" : "s")}";
    }

    private static string FormatError(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return "Could not reach the server.";
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load summary." : ex.Message;
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
