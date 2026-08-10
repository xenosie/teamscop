using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

/// <summary>
/// §3.4 — the rebuilt screenshot viewer. A fixed dark viewport hosts the image inside a ScrollViewer;
/// zoom scales the bitmap rather than the window, the wheel zooms, a drag pans, and the keyboard
/// drives next/prev/first/last/zoom/fit/close. It lives as a shell-level overlay so it is genuinely
/// full-screen — the old lightbox was cramped into the staff-detail content column.
/// </summary>
public partial class ScreenshotViewerView : UserControl
{
    private ScreenshotViewerViewModel? _vm;
    private bool _layoutQueued;
    private bool _panning;
    private Point _panStart;
    private Vector _panOffsetStart;

    public ScreenshotViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachVm(DataContext as ScreenshotViewerViewModel);
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += (_, _) => AttachVm(null);

        KeyDown += OnKeyDown;
        ImageScroll.AddHandler(PointerWheelChangedEvent, OnImageWheel, RoutingStrategies.Tunnel);
        ImageScroll.PointerPressed += OnPanPressed;
        ImageScroll.PointerMoved += OnPanMoved;
        ImageScroll.PointerReleased += OnPanReleased;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => FocusSoon();

    private void AttachVm(ScreenshotViewerViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.ViewChanged -= QueueApplyView;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.ViewChanged += QueueApplyView;
            QueueApplyView();
            FocusSoon();
        }
    }

    /// <summary>The overlay owns the keyboard while it is up, so it takes focus once it is on screen.</summary>
    private void FocusSoon() => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left or Key.PageUp:
                _vm.PrevCommand.Execute(null);
                break;
            case Key.Right or Key.PageDown:
                _vm.NextCommand.Execute(null);
                break;
            case Key.Home:
                _vm.FirstCommand.Execute(null);
                break;
            case Key.End:
                _vm.LastCommand.Execute(null);
                break;
            case Key.Add or Key.OemPlus:
                _vm.ZoomInCommand.Execute(null);
                break;
            case Key.Subtract or Key.OemMinus:
                _vm.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0 or Key.F:
                _vm.FitCommand.Execute(null);
                break;
            case Key.Escape:
                _vm.CloseCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null || e.Delta.Y == 0)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            _vm.ZoomInCommand.Execute(null);
        }
        else
        {
            _vm.ZoomOutCommand.Execute(null);
        }

        // Handled before the ScrollViewer sees it, so the wheel zooms rather than scrolls.
        e.Handled = true;
    }

    private void OnPanPressed(object? sender, PointerPressedEventArgs e)
    {
        FocusSoon();
        if (_vm is null || _vm.IsFitMode)
        {
            return;
        }

        if (!e.GetCurrentPoint(ImageScroll).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Nothing to pan when the image already fits the viewport.
        if (ImageScroll.Extent.Width <= ImageScroll.Viewport.Width + 0.5
            && ImageScroll.Extent.Height <= ImageScroll.Viewport.Height + 0.5)
        {
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(this);
        _panOffsetStart = ImageScroll.Offset;
        e.Pointer.Capture(ImageScroll);
        e.Handled = true;
    }

    private void OnPanMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var delta = e.GetPosition(this) - _panStart;
        ImageScroll.Offset = new Vector(_panOffsetStart.X - delta.X, _panOffsetStart.Y - delta.Y);
        e.Handled = true;
    }

    private void OnPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e) => QueueApplyView();

    private void QueueApplyView()
    {
        if (_layoutQueued)
        {
            return;
        }

        _layoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _layoutQueued = false;
            ApplyView();
        }, DispatcherPriority.Render);
    }

    private void ApplyView()
    {
        if (_vm?.Image is null || !IsVisible)
        {
            return;
        }

        var bmp = _vm.Image;
        var naturalW = Math.Max(1.0, bmp.PixelSize.Width);
        var naturalH = Math.Max(1.0, bmp.PixelSize.Height);
        if (bmp.Dpi.X > 1)
        {
            naturalW = bmp.PixelSize.Width * 96.0 / bmp.Dpi.X;
            naturalH = bmp.PixelSize.Height * 96.0 / bmp.Dpi.Y;
        }

        var viewport = ImageScroll.Viewport;
        if (viewport.Width <= 1 || viewport.Height <= 1)
        {
            viewport = ImageScroll.Bounds.Size;
        }

        if (viewport.Width <= 1 || viewport.Height <= 1)
        {
            return;
        }

        if (_vm.IsFitMode)
        {
            // Fill the fixed viewport; no window resizing involved.
            ViewImage.Width = double.NaN;
            ViewImage.Height = double.NaN;
            ViewImage.Stretch = Avalonia.Media.Stretch.Uniform;
            ViewImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            ViewImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            ImageScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            ImageScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            return;
        }

        var imgW = naturalW * _vm.Zoom;
        var imgH = naturalH * _vm.Zoom;
        ViewImage.Stretch = Avalonia.Media.Stretch.Fill;
        ViewImage.Width = imgW;
        ViewImage.Height = imgH;
        ViewImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        ViewImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        var needsScroll = imgW > viewport.Width + 0.5 || imgH > viewport.Height + 0.5;
        ImageScroll.HorizontalScrollBarVisibility = needsScroll
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        ImageScroll.VerticalScrollBarVisibility = needsScroll
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    }
}
