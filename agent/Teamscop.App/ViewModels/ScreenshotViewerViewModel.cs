using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

/// <summary>
/// Lightbox viewer state: fixed host viewport, zoom scales the bitmap inside a ScrollViewer.
/// Fit / zoom level is preserved across Previous / Next.
/// </summary>
public sealed partial class ScreenshotViewerViewModel : ObservableObject
{
    private static readonly double[] ZoomSteps =
        [0.25, 0.35, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0];

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;
    private readonly BitmapLruCache _fullCache = new(4);
    private readonly List<ScreenshotMetaItem> _items;
    private int _loadGeneration;
    private bool _fitMode = true;

    public event Action? CloseRequested;

    /// <summary>Raised when Fit/Zoom/Image changes so the host can re-layout the bitmap.</summary>
    public event Action? ViewChanged;

    public ScreenshotViewerViewModel(
        AppServices services,
        IReadOnlyList<ScreenshotMetaItem> items,
        int startIndex)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _log = services.Log;
        _items = items.ToList();
        CurrentIndex = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
        RebuildDisplayOptions();
        LoadCurrentAsync(prefetchNeighbor: true).FireAndForget(_log, "Screenshot viewer");
    }

    public ObservableCollection<int> DisplayOptions { get; } = [];

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private int _selectedDisplayIndex = 1;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private string _zoomLabel = "Fit";
    [ObservableProperty] private string _title = "Screenshot";
    [ObservableProperty] private bool _canGoPrev;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private bool _showDisplayPicker;

    /// <summary>
    /// What the ComboBox binds to. Nullable because a ComboBox whose items were just replaced has no
    /// selection and pushes null back through the binding — clearing DisplayOptions on every
    /// navigation made that happen constantly. Swallowing the null here, rather than letting it hit
    /// the int, is what stops the cast error; a real selection still flows through.
    /// </summary>
    public int? SelectedDisplay
    {
        get => SelectedDisplayIndex;
        set
        {
            if (value is { } display)
            {
                SelectedDisplayIndex = display;
            }

            OnPropertyChanged();
        }
    }

    public bool HasImage => Image is not null && !IsLoading;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsFitMode => _fitMode;
    public string PositionLabel =>
        _items.Count == 0 ? "" : $"{CurrentIndex + 1} / {_items.Count}";

    partial void OnImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasImage));
        ViewChanged?.Invoke();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnErrorMessageChanged(string? value)
        => OnPropertyChanged(nameof(ShowError));

    partial void OnCurrentIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PositionLabel));
        UpdateNavFlags();
        RebuildDisplayOptions();
        LoadCurrentAsync(prefetchNeighbor: true).FireAndForget(_log, "Screenshot viewer");
    }

    partial void OnSelectedDisplayIndexChanged(int value)
    {
        // Keep the ComboBox in step after a rebuild reset its selection to null.
        OnPropertyChanged(nameof(SelectedDisplay));
        LoadCurrentAsync(prefetchNeighbor: false).FireAndForget(_log, "Screenshot viewer");
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Prev()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentIndex < _items.Count - 1)
        {
            CurrentIndex++;
        }
    }

    [RelayCommand]
    private void First()
    {
        if (_items.Count > 0 && CurrentIndex != 0)
        {
            CurrentIndex = 0;
        }
    }

    [RelayCommand]
    private void Last()
    {
        if (_items.Count > 0 && CurrentIndex != _items.Count - 1)
        {
            CurrentIndex = _items.Count - 1;
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        if (_fitMode)
        {
            SetFitMode(false);
            SetZoom(1.0);
            return;
        }

        SetZoom(NextZoomAbove(Zoom));
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (_fitMode)
        {
            SetFitMode(false);
            SetZoom(0.75);
            return;
        }

        SetZoom(PreviousZoomBelow(Zoom));
    }

    [RelayCommand]
    private void Fit()
    {
        SetFitMode(true);
        Zoom = 1.0;
        ZoomLabel = "Fit";
        ViewChanged?.Invoke();
    }

    private void SetFitMode(bool fit)
    {
        if (_fitMode == fit)
        {
            return;
        }

        _fitMode = fit;
        OnPropertyChanged(nameof(IsFitMode));
    }

    private void SetZoom(double zoom)
    {
        Zoom = zoom;
        ZoomLabel = $"{(int)Math.Round(Zoom * 100)}%";
        OnPropertyChanged(nameof(Zoom));
        ViewChanged?.Invoke();
    }

    private static double NextZoomAbove(double current)
    {
        var next = ZoomSteps.FirstOrDefault(z => z > current + 0.001);
        return next > 0 ? next : ZoomSteps[^1];
    }

    private static double PreviousZoomBelow(double current)
    {
        var prev = ZoomSteps.LastOrDefault(z => z < current - 0.001);
        return prev > 0 ? prev : ZoomSteps[0];
    }

    public void DisposeCaches()
    {
        Interlocked.Increment(ref _loadGeneration);
        _fullCache.Clear();
    }

    private void UpdateNavFlags()
    {
        CanGoPrev = CurrentIndex > 0;
        CanGoNext = CurrentIndex < _items.Count - 1;
    }

    private void RebuildDisplayOptions()
    {
        DisplayOptions.Clear();
        if (_items.Count == 0)
        {
            ShowDisplayPicker = false;
            return;
        }

        var meta = _items[CurrentIndex];
        var indexes = meta.Displays.Select(d => d.Index > 0 ? d.Index : 1).Distinct().OrderBy(i => i).ToList();
        if (indexes.Count == 0)
        {
            indexes.Add(1);
        }

        foreach (var i in indexes)
        {
            DisplayOptions.Add(i);
        }

        ShowDisplayPicker = indexes.Count > 1;
        if (!indexes.Contains(SelectedDisplayIndex))
        {
            SelectedDisplayIndex = indexes[0];
        }

        Title = ScreenshotTime.Format(meta, _clock);
    }

    private async Task LoadCurrentAsync(bool prefetchNeighbor)
    {
        if (_items.Count == 0)
        {
            Image = null;
            ErrorMessage = "No screenshots.";
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        var meta = _items[CurrentIndex];
        var display = SelectedDisplayIndex;
        var key = FullKey(meta.Id, display);

        UpdateNavFlags();
        Title = ScreenshotTime.Format(meta, _clock);

        if (_fullCache.TryGet(key, out var cached) && cached is not null)
        {
            Image = cached;
            IsLoading = false;
            ErrorMessage = null;
            if (prefetchNeighbor)
            {
                PrefetchNeighbor(generation);
            }

            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            if (!_session.HasToken)
            {
                ErrorMessage = "Sign in required.";
                IsLoading = false;
                return;
            }

            var bytes = await _api.GetScreenshotImageAsync(meta.Id, display, CancellationToken.None)
                .ConfigureAwait(false);

            if (generation != _loadGeneration)
            {
                return;
            }

            Bitmap bitmap;
            await using (var ms = new MemoryStream(bytes))
            {
                bitmap = new Bitmap(ms);
            }

            _fullCache.Set(key, bitmap);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Image = bitmap;
                IsLoading = false;
                ErrorMessage = null;
            });

            if (prefetchNeighbor)
            {
                PrefetchNeighbor(generation);
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Image = null;
                IsLoading = false;
                ErrorMessage = ApiError.Describe(ex, "Failed to load image.");
            });
        }
    }

    private void PrefetchNeighbor(int generation)
    {
        var neighbor = CurrentIndex + 1;
        if (neighbor >= _items.Count)
        {
            neighbor = CurrentIndex - 1;
        }

        if (neighbor < 0 || neighbor >= _items.Count)
        {
            return;
        }

        var meta = _items[neighbor];
        var display = meta.Displays.FirstOrDefault()?.Index is > 0 and var idx ? idx : 1;
        var key = FullKey(meta.Id, display);
        if (_fullCache.TryGet(key, out _))
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                if (generation != _loadGeneration || !_session.HasToken)
                {
                    return;
                }

                var bytes = await _api.GetScreenshotImageAsync(meta.Id, display, CancellationToken.None)
                    .ConfigureAwait(false);
                if (generation != _loadGeneration)
                {
                    return;
                }

                await using var ms = new MemoryStream(bytes);
                var bitmap = new Bitmap(ms);
                _fullCache.Set(key, bitmap);
            }
            catch (Exception ex)
            {
                // Prefetch is best-effort — the next/prev click reloads it for real.
                _log.Debug($"Screenshot prefetch skipped: {ApiError.Describe(ex)}");
            }
        }).FireAndForget(_log, "Screenshot prefetch");
    }

    private static string FullKey(Guid id, int display) => $"{id:D}:{display}:full";
}
