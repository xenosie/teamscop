using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class ScreenshotLightboxView : UserControl
{
    private ScreenshotViewerViewModel? _vm;
    private bool _layoutQueued;

    public ScreenshotLightboxView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachVm(DataContext as ScreenshotViewerViewModel);
        DetachedFromVisualTree += (_, _) => AttachVm(null);
    }

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
        }
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        => QueueApplyView();

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
