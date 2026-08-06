using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Teamscop.App.Views;

public partial class StaffPeriodCalendarView : UserControl
{
    public StaffPeriodCalendarView()
    {
        InitializeComponent();
        ApplyWallpaper();
    }

    private void ApplyWallpaper()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Teamscop.App/Assets/calendar-pattern-tile.png"));
            var bitmap = new Bitmap(stream);
            WallpaperRoot.Background = new ImageBrush(bitmap)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.Fill,
                TileMode = TileMode.Tile,
                DestinationRect = new RelativeRect(0, 0, 120, 128, RelativeUnit.Absolute)
            };
        }
        catch
        {
            WallpaperRoot.Background = Brush.Parse("#EFF6FF");
        }
    }
}
