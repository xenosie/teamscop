using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Composition;
using Teamscop.App.Services;
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
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public StaffSummaryViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
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
        DateTime? businessEnd,
        CancellationToken ct = default)
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
        if (!_session.HasToken)
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

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
        });

        try
        {
            // Partial packages: load each independently so one 403 does not blank the summary.
            TimeTrackTimeline? timeline = null;
            IReadOnlyList<BrowsingTopUrlItem> topUrls = [];
            try
            {
                timeline = await _api.QueryTimeTrackTimelineAsync(
                    staffUserId, fromUtc.Value, toUtc.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ApiError.IsForbidden(ex))
            {
                // Missing view_timetrack — omit worked time.
            }

            try
            {
                topUrls = await _api.QueryBrowsingTopUrlsAsync(
                    staffUserId, take: 3, from: fromUtc, to: toUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ApiError.IsForbidden(ex))
            {
                // Missing view_browser_history — omit top URLs.
            }

            var workedSeconds = timeline?.Segments
                .Where(s => string.Equals(s.Kind, "working", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.DurationSeconds) ?? 0;
            var periodLabel = CompanyClock.FormatDayRange(businessStart.Value, businessEnd.Value);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                WorkedSentence =
                    $"This staff worked {CompanyClock.FormatDurationLong(workedSeconds)} during {periodLabel}.";
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ClearBody();
                ErrorMessage = ApiError.Describe(ex, "Failed to load summary.");
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

}
