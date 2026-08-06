using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class ScreenshotTileViewModel : ObservableObject
{
    private readonly ScreenshotGalleryViewModel _gallery;

    public ScreenshotTileViewModel(ScreenshotGalleryViewModel gallery, ScreenshotMetaItem meta)
    {
        _gallery = gallery;
        Meta = meta;
        EventId = meta.Id;
        DisplayIndex = meta.Displays.FirstOrDefault()?.Index is > 0 and var idx ? idx : 1;
        TimeLabel = FormatTime(meta);
    }

    public ScreenshotMetaItem Meta { get; }
    public Guid EventId { get; }
    public int DisplayIndex { get; }
    public string TimeLabel { get; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isLoadingThumb;
    [ObservableProperty] private bool _thumbFailed;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasThumbnail));

    public void EnsureThumb() => _gallery.RequestThumb(this);

    [RelayCommand]
    private void Open() => _gallery.OpenViewer(this);

    private static string FormatTime(ScreenshotMetaItem meta)
    {
        if (meta.BusinessOccurredAt is { } biz)
        {
            return biz.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return meta.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }
}

public sealed partial class ScreenshotGalleryViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private readonly BitmapLruCache _thumbCache = new(80);
    private string _apiBaseUrl;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;
    private readonly HashSet<Guid> _thumbInFlight = [];

    public ScreenshotGalleryViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
        Chain = new ChainHealthViewModel(apiBaseUrl);
    }

    public ChainHealthViewModel Chain { get; }

    public ObservableCollection<ScreenshotTileViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private ScreenshotViewerViewModel? _viewer;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems && Viewer is null;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage) && Viewer is null;
    public bool IsViewerOpen => Viewer is not null;
    public bool ShowGrid => HasItems && Viewer is null;

    partial void OnViewerChanged(ScreenshotViewerViewModel? value)
    {
        OnPropertyChanged(nameof(IsViewerOpen));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowGrid));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    public void Reset()
    {
        CloseViewer();
        Chain.Reset();
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        Interlocked.Increment(ref _loadGeneration);
        Items.Clear();
        ErrorMessage = null;
        EmptyMessage = null;
        _thumbCache.Clear();
        lock (_thumbInFlight)
        {
            _thumbInFlight.Clear();
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowGrid));
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
                OnPropertyChanged(nameof(ShowGrid));
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
            var metas = await api.QueryScreenshotsAsync(
                    state.AccessToken, staffUserId, take: 200, from: fromUtc, to: toUtc)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                foreach (var meta in metas)
                {
                    Items.Add(new ScreenshotTileViewModel(this, meta));
                }

                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                EmptyMessage = metas.Count == 0
                    ? (fromUtc is not null || toUtc is not null
                        ? "No screenshots in this period."
                        : "No screenshots yet.")
                    : null;
                ErrorMessage = null;
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(ShowGrid));
            });

            // Kick off thumbs for the first visible page (lazy; cells also request on bind).
            foreach (var tile in Items.Take(24).ToList())
            {
                RequestThumb(tile);
            }
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
                OnPropertyChanged(nameof(ShowGrid));
            });
        }
    }

    public void RequestThumb(ScreenshotTileViewModel tile)
    {
        if (tile.Thumbnail is not null || tile.IsLoadingThumb)
        {
            return;
        }

        var key = ThumbKey(tile.EventId, tile.DisplayIndex);
        if (_thumbCache.TryGet(key, out var cached) && cached is not null)
        {
            tile.Thumbnail = cached;
            return;
        }

        lock (_thumbInFlight)
        {
            if (!_thumbInFlight.Add(tile.EventId))
            {
                return;
            }
        }

        tile.IsLoadingThumb = true;
        var generation = _loadGeneration;
        _ = LoadThumbAsync(tile, generation);
    }

    public void OpenViewer(ScreenshotTileViewModel tile)
    {
        var metas = Items.Select(i => i.Meta).ToList();
        var index = Items.IndexOf(tile);
        if (index < 0 || metas.Count == 0)
        {
            return;
        }

        CloseViewer();
        var viewer = new ScreenshotViewerViewModel(metas, index, _apiBaseUrl);
        viewer.CloseRequested += CloseViewer;
        Viewer = viewer;
    }

    public void CloseViewer()
    {
        if (Viewer is null)
        {
            return;
        }

        var viewer = Viewer;
        Viewer = null;
        viewer.CloseRequested -= CloseViewer;
        viewer.DisposeCaches();
    }

    private async Task LoadThumbAsync(ScreenshotTileViewModel tile, int generation)
    {
        try
        {
            var state = _store.Load();
            if (string.IsNullOrWhiteSpace(state.AccessToken) || generation != _loadGeneration)
            {
                return;
            }

            _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
            using var api = new TrackingApiClient(_apiBaseUrl);
            var bytes = await api.GetScreenshotThumbAsync(
                    state.AccessToken, tile.EventId, tile.DisplayIndex, maxWidth: 140)
                .ConfigureAwait(false);

            if (generation != _loadGeneration)
            {
                return;
            }

            Bitmap bitmap;
            await using (var ms = new MemoryStream(bytes))
            {
                bitmap = Bitmap.DecodeToWidth(ms, 140);
            }

            var key = ThumbKey(tile.EventId, tile.DisplayIndex);
            _thumbCache.Set(key, bitmap);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                tile.Thumbnail = bitmap;
                tile.IsLoadingThumb = false;
                tile.ThumbFailed = false;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                tile.IsLoadingThumb = false;
                tile.ThumbFailed = true;
            });
        }
        finally
        {
            lock (_thumbInFlight)
            {
                _thumbInFlight.Remove(tile.EventId);
            }
        }
    }

    private static string ThumbKey(Guid eventId, int display)
        => $"{eventId:D}:{display}:thumb";

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
            : (string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load screenshots." : ex.Message);
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
