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

    /// <summary>A thumbnail is fetched when its tile reaches the tree, never for the whole day.</summary>
    private void OnTileAttached(object? sender, VisualTreeAttachmentEventArgs e) => EnsureTile(sender);

    /// <summary>
    /// The bands virtualise, and a recycled container is handed a new band rather than
    /// re-attached, so realisation alone would miss the tiles inside it. EnsureThumb is
    /// idempotent per tile.
    /// </summary>
    private void OnTileDataContextChanged(object? sender, EventArgs e) => EnsureTile(sender);

    private static void EnsureTile(object? sender)
    {
        if (sender is Control { DataContext: ScreenshotTileViewModel tile })
        {
            tile.EnsureThumb();
        }
    }
}
