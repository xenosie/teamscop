using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;

namespace Teamscop.App.ViewModels;

/// <summary>One rank. The bar is normalised to the leader, so first place is always a full bar.</summary>
public sealed partial class LeaderboardRowViewModel : ObservableObject
{
    public const double BarTrack = 260;

    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
    public required int Rank { get; init; }
    public required double WorkedSeconds { get; init; }
    public required string WorkedLabel { get; init; }

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private double _barWidth;

    public bool HasAvatar => Avatar is not null;
    public bool AvatarRequested { get; set; }

    /// <summary>The top three read as a medal position; after that the number is enough.</summary>
    public bool IsPodium => Rank is >= 1 and <= 3;

    partial void OnAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAvatar));
}

/// <summary>
/// A11 / §14.2 — staff ranked by hours worked over any period from the calendar (§14.3).
///
/// The ranking is a server aggregate (<c>GET /api/tracking/leaderboard</c>): ranks, totals, paging
/// and the §4.5 self-exclusion all come back decided, so a 50-member company costs one request per
/// page instead of one per member. The server caps the span at 31 days.
/// </summary>
public sealed partial class LeaderboardViewModel : ObservableObject
{
    private const int PageSize = 25;

    /// <summary>The server's own limit, checked here so an over-long period never round-trips.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(31);

    private readonly TeamscopApi _api;
    private readonly CompanyClock _clock;
    private readonly ImageLoader _images;
    private readonly UiLog _log;

    private CancellationToken _lifetime = CancellationToken.None;
    private CancellationTokenSource? _inFlight;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private double _topWorkedSeconds;
    private int _total;
    private int _nextPage;
    private int _generation;
    private bool _isOpen;

    public LeaderboardViewModel(AppServices services)
    {
        _api = services.Api;
        _clock = services.Clock;
        _images = services.Images;
        _log = services.Log;
        PeriodFilter = new StaffPeriodFilterViewModel(services);
        PeriodFilter.FilterChanged += OnPeriodChanged;
    }

    public StaffPeriodFilterViewModel PeriodFilter { get; }

    public ObservableCollection<LeaderboardRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _periodLabel = string.Empty;
    [ObservableProperty] private bool _hasMore;

    public string PageLabel => _total == 0 ? string.Empty : $"Showing {Rows.Count} of {_total}";
    public bool IsPaged => _total > PageSize;
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmpty => !IsLoading && !HasRows && Problem is null;
    public bool ShowProblem => Problem is not null;

    public void SetLifetime(CancellationToken token) => _lifetime = token;

    /// <summary>Route entry. Defaults to the current company week the first time it is opened.</summary>
    public void Open()
    {
        _isOpen = true;
        if (!PeriodFilter.HasActiveFilter)
        {
            var today = _clock.Today;
            // Monday-first, matching the calendar grid.
            var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            PeriodFilter.ApplyRange(start, today);
            return;
        }

        LoadAsync(force: false).FireAndForget(_log, "Leaderboard");
    }

    /// <summary>Leaving the route drops whatever is in flight rather than repainting behind it.</summary>
    public void Suspend()
    {
        _isOpen = false;
        PeriodFilter.CloseCalendarCommand.Execute(null);
        CancelInFlight();
    }

    /// <summary>
    /// The company zone moved: the same calendar days are now different instants (§8.2). What is
    /// on screen was ranked over the old bounds, so it is stale either way — reloaded now if the
    /// route is open, on the next visit if it is not.
    /// </summary>
    public void ReapplyClock()
    {
        PeriodFilter.ReapplyClock();
        PeriodLabel = PeriodFilter.RangeLabel;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        if (_isOpen)
        {
            LoadAsync(force: true).FireAndForget(_log, "Leaderboard");
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(force: true);

    [RelayCommand]
    private Task ShowMoreAsync() => LoadPageAsync(_nextPage, append: true);

    /// <summary>Called by the view as a row is realised, so only visible avatars are fetched.</summary>
    public void EnsureAvatar(LeaderboardRowViewModel row)
    {
        if (row.AvatarRequested || row.Avatar is not null || string.IsNullOrWhiteSpace(row.AvatarUrl))
        {
            return;
        }

        row.AvatarRequested = true;
        LoadAvatarAsync(row).FireAndForget(_log, "Staff avatar");
    }

    public Task LoadAsync(bool force, CancellationToken ct = default)
    {
        if (PeriodFilter.AppliedFromUtc is not { } fromUtc || PeriodFilter.AppliedToUtc is not { } toUtc)
        {
            ClearRows();
            Problem = null;
            IsLoading = false;
            return Task.CompletedTask;
        }

        // Re-entering the route with the same period must not re-run the aggregate.
        if (!force && _loadedFromUtc == fromUtc && _loadedToUtc == toUtc && (HasRows || ShowProblem))
        {
            return Task.CompletedTask;
        }

        if (toUtc - fromUtc > MaxSpan)
        {
            ClearRows();
            IsLoading = false;
            Problem = "Pick a period of 31 days or less.";
            _loadedFromUtc = fromUtc;
            _loadedToUtc = toUtc;
            return Task.CompletedTask;
        }

        return LoadPageAsync(0, append: false, ct);
    }

    private async Task LoadPageAsync(int page, bool append, CancellationToken ct = default)
    {
        if (PeriodFilter.AppliedFromUtc is not { } fromUtc || PeriodFilter.AppliedToUtc is not { } toUtc)
        {
            return;
        }

        CancelInFlight();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, ct);
        _inFlight = cts;
        var generation = Interlocked.Increment(ref _generation);

        PeriodLabel = PeriodFilter.RangeLabel;
        IsLoading = true;
        Problem = null;

        try
        {
            var result = await _api
                .GetLeaderboardAsync(fromUtc, toUtc, page, PageSize, cts.Token)
                .ConfigureAwait(true);

            if (generation != _generation)
            {
                return;
            }

            if (!append)
            {
                ClearRows();
            }

            _total = result.Total;
            _nextPage = result.Page + 1;
            foreach (var row in result.Rows)
            {
                if (Rows.Count == 0 && !append)
                {
                    _topWorkedSeconds = row.WorkedSeconds;
                }

                Rows.Add(new LeaderboardRowViewModel
                {
                    UserId = row.UserId,
                    Name = row.Username,
                    AvatarUrl = row.AvatarUrl,
                    Rank = row.Rank,
                    WorkedSeconds = row.WorkedSeconds,
                    WorkedLabel = CompanyClock.FormatDuration(row.WorkedSeconds),
                    BarWidth = _topWorkedSeconds > 0
                        ? Math.Max(2, LeaderboardRowViewModel.BarTrack * (row.WorkedSeconds / _topWorkedSeconds))
                        : 0
                });
            }

            HasMore = Rows.Count < _total && result.Rows.Count > 0;
            _loadedFromUtc = fromUtc;
            _loadedToUtc = toUtc;
            IsLoading = false;
            RaiseState();
        }
        catch (OperationCanceledException)
        {
            // Route left or period changed under us — the newer load owns the screen.
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation)
            {
                return;
            }

            _log.Warn("Leaderboard could not load", ex);
            Problem = ApiError.Describe(ex, "Could not load the leaderboard.");
            _loadedFromUtc = fromUtc;
            _loadedToUtc = toUtc;
            IsLoading = false;
            RaiseState();
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cts))
            {
                _inFlight = null;
            }

            cts.Dispose();
        }
    }

    private void OnPeriodChanged()
    {
        PeriodLabel = PeriodFilter.RangeLabel;
        LoadAsync(force: true).FireAndForget(_log, "Leaderboard");
    }

    private void CancelInFlight()
    {
        var cts = _inFlight;
        _inFlight = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed by its own finally.
        }
    }

    private void ClearRows()
    {
        Rows.Clear();
        _total = 0;
        _nextPage = 0;
        _topWorkedSeconds = 0;
        HasMore = false;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        RaiseState();
    }

    private async Task LoadAvatarAsync(LeaderboardRowViewModel row)
    {
        var bitmap = await _images.LoadAsync(row.AvatarUrl).ConfigureAwait(false);
        if (bitmap is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => row.Avatar = bitmap);
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowProblem));
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
