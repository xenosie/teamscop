using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class ScreenshotTileViewModel : ObservableObject
{
    private readonly ScreenshotGalleryViewModel _gallery;

    public ScreenshotTileViewModel(ScreenshotGalleryViewModel gallery, ScreenshotMetaItem meta, CompanyClock clock)
    {
        _gallery = gallery;
        Meta = meta;
        EventId = meta.Id;
        DisplayIndex = meta.Displays.FirstOrDefault()?.Index is > 0 and var idx ? idx : 1;
        TimeLabel = ScreenshotTime.Format(meta, clock);
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
}

/// <summary>A band in the gallery: a row of up to six captures.</summary>
public interface IGalleryRow;

/// <summary>
/// A fixed-width band of tiles. Rows — not tiles — are what the gallery virtualises, so a day of
/// captures realises the handful of bands on screen instead of every thumbnail at once (§15.1).
/// </summary>
public sealed class ScreenshotRowViewModel : IGalleryRow
{
    public const int Columns = 6;

    public ScreenshotRowViewModel(IReadOnlyList<ScreenshotTileViewModel> tiles) => Tiles = tiles;

    public IReadOnlyList<ScreenshotTileViewModel> Tiles { get; }
}

/// <summary>Capture time in company time (§8.2): the server's projection first, then the clock.</summary>
public static class ScreenshotTime
{
    public static string Format(ScreenshotMetaItem meta, CompanyClock clock)
        => meta.BusinessOccurredAt is { } biz
            ? biz.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : clock.FormatDateTime(meta.OccurredAt);
}

public sealed partial class ScreenshotGalleryViewModel : ObservableObject
{
    /// <summary>One page. The endpoint caps take at 200; 60 is roughly two screens of bands.</summary>
    private const int PageSize = 60;

    /// <summary>Thumbnail fetches in flight. Cheap hardware must not open one socket per tile.</summary>
    private const int MaxThumbParallel = 4;

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;
    private readonly BitmapLruCache _thumbCache = new(80);
    private readonly SemaphoreSlim _thumbGate = new(MaxThumbParallel, MaxThumbParallel);
    private readonly HashSet<Guid> _thumbInFlight = [];
    private readonly List<ScreenshotTileViewModel> _tiles = [];
    private readonly HashSet<Guid> _seen = [];

    /// <summary>Tiles not yet laid into a band, so a page boundary never splits one.</summary>
    private readonly List<ScreenshotTileViewModel> _band = [];

    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private DateTimeOffset? _cursorUtc;
    private CancellationToken _sectionCt = CancellationToken.None;
    private int _loadGeneration;

    public ScreenshotGalleryViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _log = services.Log;
    }

    /// <summary>
    /// §3.4 — a tile was clicked. The viewer is a full-screen overlay hosted by the shell, not a
    /// panel inside this section, so the gallery hands up the page and start index and the shell
    /// opens it over everything (nav, section nav, title bar).
    /// </summary>
    public event Action<IReadOnlyList<ScreenshotMetaItem>, int>? OpenViewerRequested;

    /// <summary>Bands of captures for the period, newest first.</summary>
    public ObservableCollection<IGalleryRow> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _pageLabel = string.Empty;

    public bool HasItems => _tiles.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowGrid => HasItems;
    public bool ShowPaging => ShowGrid && HasMore;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowPaging));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(ShowPaging));

    public void Reset()
    {
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        Interlocked.Increment(ref _loadGeneration);
        ClearPages();
        ErrorMessage = null;
        EmptyMessage = null;
        HasMore = false;
        _thumbCache.Clear();
        lock (_thumbInFlight)
        {
            _thumbInFlight.Clear();
        }

        RaiseGrid();
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

        _sectionCt = ct;
        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!_session.HasToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                ClearPages();
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
                RaiseGrid();
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
            ClearPages();
            HasMore = false;
        });

        try
        {
            var metas = await _api.QueryScreenshotsAsync(
                    staffUserId, take: PageSize, from: fromUtc, to: toUtc, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Append(metas);
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
                RaiseGrid();
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
                ClearPages();
                ErrorMessage = ApiError.Describe(ex, "Failed to load screenshots.");
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                RaiseGrid();
            });
        }
    }

    /// <summary>
    /// The next page of older captures, through the endpoint's own cursor: the oldest capture on
    /// screen is passed as <c>before</c>, which narrows the window without touching the period's
    /// bounds. Paging by moving <c>to</c> lost every capture that shared the cursor's second.
    /// </summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !HasMore || _loadedForStaff is not { } staffUserId || _cursorUtc is not { } cursor)
        {
            return;
        }

        var generation = _loadGeneration;
        IsLoadingMore = true;
        try
        {
            var metas = await _api.QueryScreenshotsAsync(
                    staffUserId,
                    take: PageSize,
                    from: _loadedFromUtc,
                    to: _loadedToUtc,
                    _sectionCt,
                    before: cursor)
                .ConfigureAwait(true);

            if (generation != _loadGeneration)
            {
                return;
            }

            Append(metas);
            RaiseGrid();
        }
        catch (OperationCanceledException) when (_sectionCt.IsCancellationRequested)
        {
            // The section was left; the next entry starts a fresh first page.
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
            {
                ErrorMessage = ApiError.Describe(ex, "Failed to load older screenshots.");
            }
        }
        finally
        {
            IsLoadingMore = false;
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
        LoadThumbAsync(tile, generation).FireAndForget(_log, "Screenshot thumbnail");
    }

    public void OpenViewer(ScreenshotTileViewModel tile)
    {
        var metas = _tiles.Select(i => i.Meta).ToList();
        var index = _tiles.IndexOf(tile);
        if (index < 0 || metas.Count == 0)
        {
            return;
        }

        OpenViewerRequested?.Invoke(metas, index);
    }

    // ---- paging internals -----------------------------------------------------------------------

    private void ClearPages()
    {
        Rows.Clear();
        _tiles.Clear();
        _band.Clear();
        _seen.Clear();
        _cursorUtc = null;
        PageLabel = string.Empty;
    }

    /// <summary>
    /// Appends a page. The trailing partial band is rolled back into the buffer first so the new
    /// page continues it rather than starting a short row beside it.
    /// </summary>
    private void Append(IReadOnlyList<ScreenshotMetaItem> metas)
    {
        if (Rows.Count > 0
            && Rows[^1] is ScreenshotRowViewModel tail
            && tail.Tiles.Count < ScreenshotRowViewModel.Columns)
        {
            Rows.RemoveAt(Rows.Count - 1);
            _band.AddRange(tail.Tiles);
        }

        var added = 0;
        foreach (var meta in metas)
        {
            // The cursor is exclusive, but a retry or a clock skew can repeat one — dedupe by id.
            if (!_seen.Add(meta.Id))
            {
                continue;
            }

            var tile = new ScreenshotTileViewModel(this, meta, _clock);
            _tiles.Add(tile);
            added++;

            _band.Add(tile);
            if (_band.Count == ScreenshotRowViewModel.Columns)
            {
                FlushBand();
            }
        }

        _cursorUtc = _tiles.Count > 0 ? _tiles[^1].Meta.OccurredAt : null;
        HasMore = added > 0 && metas.Count >= PageSize;

        FlushBand();

        PageLabel = _tiles.Count == 0
            ? string.Empty
            : HasMore ? $"{_tiles.Count} captures loaded" : $"{_tiles.Count} captures";
    }

    private void FlushBand()
    {
        if (_band.Count == 0)
        {
            return;
        }

        Rows.Add(new ScreenshotRowViewModel(_band.ToArray()));
        _band.Clear();
    }

    private async Task LoadThumbAsync(ScreenshotTileViewModel tile, int generation)
    {
        try
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            await _thumbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != _loadGeneration)
                {
                    return;
                }

                var bytes = await _api.GetScreenshotThumbAsync(
                        tile.EventId, tile.DisplayIndex, maxWidth: 140, CancellationToken.None)
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

                _thumbCache.Set(ThumbKey(tile.EventId, tile.DisplayIndex), bitmap);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _loadGeneration) return;
                    tile.Thumbnail = bitmap;
                    tile.IsLoadingThumb = false;
                    tile.ThumbFailed = false;
                });
            }
            finally
            {
                _thumbGate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Thumbnail {tile.EventId:D} not loaded: {ApiError.Describe(ex)}");
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

    private void RaiseGrid()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowPaging));
    }

    private static string ThumbKey(Guid eventId, int display)
        => $"{eventId:D}:{display}:thumb";
}
