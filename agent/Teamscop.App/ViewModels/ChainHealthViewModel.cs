using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

/// <summary>Quiet when healthy; banner when offline / helper down / chain break.</summary>
public sealed partial class ChainHealthViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private int _generation;

    public ChainHealthViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
    }

    [ObservableProperty] private string? _bannerMessage;
    [ObservableProperty] private bool _showBanner;

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        BannerMessage = null;
        ShowBanner = false;
    }

    public async Task LoadAsync(Guid staffUserId)
    {
        var gen = Interlocked.Increment(ref _generation);
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        try
        {
            using var api = new TrackingApiClient(_apiBaseUrl);
            var health = await api.QueryChainHealthAsync(state.AccessToken, staffUserId).ConfigureAwait(false);
            if (gen != _generation)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _generation) return;
                BannerMessage = string.IsNullOrWhiteSpace(health.BannerMessage) ? null : health.BannerMessage;
                ShowBanner = !string.IsNullOrWhiteSpace(BannerMessage);
            });
        }
        catch
        {
            // Keep panels usable if chain endpoint fails.
            if (gen != _generation) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _generation) return;
                BannerMessage = null;
                ShowBanner = false;
            });
        }
    }

    private static string ResolveApiBase(string? apiBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return apiBaseUrl.TrimEnd('/');
        }

        return Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE")?.TrimEnd('/')
               ?? "https://teamscop.com";
    }
}
