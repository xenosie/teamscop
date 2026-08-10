using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed class BrowsingVisitRowViewModel
{
    public BrowsingVisitRowViewModel(BrowsingVisitItem item, CompanyClock clock)
    {
        Url = item.Url;
        Title = string.IsNullOrWhiteSpace(item.Title) ? item.Url : item.Title;
        Profile = item.Profile;
        VisitedAtUtc = item.VisitedAt;
        TimeLabel = clock.FormatDateTime(item.VisitedAt);
    }

    public string Url { get; }
    public string Title { get; }
    public string Profile { get; }
    public DateTimeOffset VisitedAtUtc { get; }
    public string TimeLabel { get; }
}

public sealed partial class BrowsingDomainDetailViewModel : ObservableObject
{
    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;
    private readonly Guid _staffUserId;
    private readonly string _domain;
    private readonly DateTimeOffset? _fromUtc;
    private readonly DateTimeOffset? _toUtc;
    private int _visitCount;

    public BrowsingDomainDetailViewModel(
        AppServices services,
        string domain,
        Guid staffUserId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _log = services.Log;
        _staffUserId = staffUserId;
        _domain = domain;
        _fromUtc = fromUtc;
        _toUtc = toUtc;
        Title = domain;
    }

    /// <summary>Visits newest-first.</summary>
    public ObservableCollection<BrowsingVisitRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _errorMessage;

    public bool HasItems => _visitCount > 0;
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

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!_session.HasToken)
        {
            ErrorMessage = "Sign in required.";
            IsLoading = false;
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var detail = await _api.QueryBrowsingDomainDetailAsync(
                    _staffUserId, _domain, take: 200, from: _fromUtc, to: _toUtc, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Build(detail);
                Title = string.IsNullOrWhiteSpace(detail.Domain) ? _domain : detail.Domain;
                Subtitle = $"{detail.VisitCount} visit{(detail.VisitCount == 1 ? "" : "s")}";
                ErrorMessage = null;
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The window closed while loading.
        }
        catch (Exception ex)
        {
            _log.Warn($"Browsing detail for {_domain} could not be loaded", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Rows.Clear();
                _visitCount = 0;
                ErrorMessage = ApiError.Describe(ex, "Failed to load visits.");
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
    }

    private void Build(BrowsingDomainDetail detail)
    {
        Rows.Clear();
        var visits = detail.Visits
            .Select(v => new BrowsingVisitRowViewModel(v, _clock))
            .OrderByDescending(v => v.VisitedAtUtc);
        _visitCount = 0;
        foreach (var visit in visits)
        {
            Rows.Add(visit);
            _visitCount++;
        }
    }
}
