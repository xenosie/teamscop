using Avalonia;
using Avalonia.Controls;
using Teamscop.App.ViewModels;

namespace Teamscop.App.Views;

public partial class ScreenshotGalleryView : UserControl
{
    public ScreenshotGalleryView()
    {
        InitializeComponent();
    }

    private void OnTileAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ScreenshotTileViewModel tile })
        {
            tile.EnsureThumb();
        }
    }
}
