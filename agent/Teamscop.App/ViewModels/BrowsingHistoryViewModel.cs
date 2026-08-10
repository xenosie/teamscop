using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class BrowsingDomainRowViewModel : ObservableObject
{
    private readonly BrowsingHistoryViewModel _owner;

    public BrowsingDomainRowViewModel(BrowsingHistoryViewModel owner, BrowsingDomainSummary summary)
    {
        _owner = owner;
        Domain = summary.Domain;
        VisitCount = summary.VisitCount;
        LastVisitedAt = summary.LastVisitedAt;
    }

    public string Domain { get; }
    public int VisitCount { get; }
    public DateTimeOffset? LastVisitedAt { get; }
    public string VisitCountLabel => VisitCount.ToString(CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Open() => _owner.OpenDomain(Domain);
}

public sealed partial class BrowsingHistoryViewModel : ObservableObject
{
    /// <summary>A busy day is hundreds of domains; the roll-up shows a page of them (§15.1).</summary>
    private const int PageSize = 25;

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly UiLog _log;
    private readonly PageWindow<BrowsingDomainRowViewModel> _page;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public BrowsingHistoryViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _log = services.Log;
        _page = new PageWindow<BrowsingDomainRowViewModel>(Items, PageSize);
    }

    public ObservableCollection<BrowsingDomainRowViewModel> Items { get; } = [];

    public Action<string, Guid, DateTimeOffset?, DateTimeOffset?>? RequestOpenDomainDetail { get; set; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsPaged => _page.IsPaged;
    public bool HasMore => _page.HasMore;
    public string PageLabel => _page.Label;

    [RelayCommand]
    private void ShowMore()
    {
        _page.More();
        RaisePaging();
    }

    [RelayCommand]
    private void ShowAll()
    {
        _page.All();
        RaisePaging();
    }

    private void RaisePaging()
    {
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
    }

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

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        _page.Reset([]);
        ErrorMessage = null;
        EmptyMessage = null;
        RaisePaging();
    }

    public void OpenDomain(string domain)
    {
        if (_loadedForStaff is null || string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        RequestOpenDomainDetail?.Invoke(domain, _loadedForStaff.Value, _loadedFromUtc, _loadedToUtc);
    }

    public async Task LoadAsync(
        Guid staffUserId,
        bool force = false,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken ct = default)
    {
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasItems || ShowEmpty || ShowError))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!_session.HasToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset([]);
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
                RaisePaging();
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
            var rows = await _api.QueryBrowsingDomainsAsync(
                    staffUserId, take: 200, from: fromUtc, to: toUtc, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset(rows.Select(r => new BrowsingDomainRowViewModel(this, r)).ToList());
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                EmptyMessage = rows.Count == 0
                    ? (fromUtc is not null || toUtc is not null
                        ? "No browsing history in this period."
                        : "No browsing history yet.")
                    : null;
                ErrorMessage = null;
                IsLoading = false;
                RaisePaging();
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
            _log.Warn($"Browsing history for {staffUserId:D} could not be loaded", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset([]);
                ErrorMessage = ApiError.Describe(ex, "Failed to load browsing history.");
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                RaisePaging();
            });
        }
    }

}
