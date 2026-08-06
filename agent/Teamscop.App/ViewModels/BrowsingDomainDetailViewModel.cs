using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class BrowsingVisitRowViewModel : ObservableObject
{
    public BrowsingVisitRowViewModel(BrowsingVisitItem item)
    {
        Url = item.Url;
        Title = string.IsNullOrWhiteSpace(item.Title) ? item.Url : item.Title;
        Profile = item.Profile;
        TimeLabel = item.VisitedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public string Url { get; }
    public string Title { get; }
    public string Profile { get; }
    public string TimeLabel { get; }
}

public sealed partial class BrowsingDomainDetailViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private readonly string _apiBaseUrl;
    private readonly Guid _staffUserId;
    private readonly string _domain;
    private readonly DateTimeOffset? _fromUtc;
    private readonly DateTimeOffset? _toUtc;

    public BrowsingDomainDetailViewModel(
        string domain,
        Guid staffUserId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        _staffUserId = staffUserId;
        _domain = domain;
        _fromUtc = fromUtc;
        _toUtc = toUtc;
        Title = domain;
    }

    public ObservableCollection<BrowsingVisitRowViewModel> Visits { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _errorMessage;

    public bool HasItems => Visits.Count > 0;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasItems));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public async Task LoadAsync()
    {
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            ErrorMessage = "Sign in required.";
            IsLoading = false;
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var baseUrl = ResolveApiBase(string.IsNullOrWhiteSpace(state.ApiBaseUrl) ? _apiBaseUrl : state.ApiBaseUrl);
            using var api = new TrackingApiClient(baseUrl);
            var detail = await api.QueryBrowsingDomainDetailAsync(
                    state.AccessToken, _staffUserId, _domain, take: 200, from: _fromUtc, to: _toUtc)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Visits.Clear();
                foreach (var v in detail.Visits)
                {
                    Visits.Add(new BrowsingVisitRowViewModel(v));
                }

                Title = string.IsNullOrWhiteSpace(detail.Domain) ? _domain : detail.Domain;
                Subtitle = $"{detail.VisitCount} visit{(detail.VisitCount == 1 ? "" : "s")}";
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
                Visits.Clear();
                ErrorMessage = ex is HttpRequestException
                    ? "Could not reach the server."
                    : (string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load visits." : ex.Message);
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
