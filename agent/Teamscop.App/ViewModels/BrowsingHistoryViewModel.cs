using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using CommunityToolkit.Mvvm.Input;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
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
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public BrowsingHistoryViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        Chain = new ChainHealthViewModel(apiBaseUrl);
    }

    public ChainHealthViewModel Chain { get; }

    public ObservableCollection<BrowsingDomainRowViewModel> Items { get; } = [];

    public Action<string, Guid, DateTimeOffset?, DateTimeOffset?>? RequestOpenDomainDetail { get; set; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;
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

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        Chain.Reset();
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        Items.Clear();
        ErrorMessage = null;
        EmptyMessage = null;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
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
        DateTimeOffset? toUtc = null)
    {
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasItems || ShowEmpty || ShowError))
        {
            return;
        }

        _ = Chain.LoadAsync(staffUserId);
        var generation = Interlocked.Increment(ref _loadGeneration);
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
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
            var rows = await api.QueryBrowsingDomainsAsync(
                    state.AccessToken, staffUserId, take: 200, from: fromUtc, to: toUtc)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(new BrowsingDomainRowViewModel(this, row));
                }

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
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                ErrorMessage = FormatError(ex);
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
    }

    private static string FormatError(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        if (ex is ApiClientException api)
        {
            var msg = api.Message;
            const string prefix = "Tracking API: ";
            return msg.StartsWith(prefix, StringComparison.Ordinal) ? msg[prefix.Length..] : msg;
        }

        return ex is HttpRequestException
            ? "Could not reach the server."
            : (string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load browsing history." : ex.Message);
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
