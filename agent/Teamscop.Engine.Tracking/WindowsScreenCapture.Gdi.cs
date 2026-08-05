#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;

namespace Teamscop.Engine.Tracking;

internal static class WindowsScreenCaptureGdi
{
    public static IReadOnlyList<DisplayCapture> CaptureAll(int targetBytes)
    {
        var results = new List<DisplayCapture>();
        var index = 1;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y))
        {
            var bounds = screen.Bounds;
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            var (jpeg, quality) = EncodeToTarget(bmp, targetBytes);
            results.Add(new DisplayCapture
            {
                DisplayIndex = index++,
                Width = bounds.Width,
                Height = bounds.Height,
                JpegBytes = jpeg,
                QualityUsed = quality
            });
        }

        return results;
    }

    private static (byte[] Jpeg, int Quality) EncodeToTarget(Bitmap bmp, int targetBytes)
    {
        var lo = 5;
        var hi = 90;
        byte[]? best = null;
        var bestQ = 40;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var bytes = Encode(bmp, mid);
            if (bytes.Length <= targetBytes)
            {
                best = bytes;
                bestQ = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best is not null)
        {
            return (best, bestQ);
        }

        using var scaled = new Bitmap(bmp, new Size(Math.Max(320, bmp.Width / 2), Math.Max(180, bmp.Height / 2)));
        return (Encode(scaled, 5), 5);
    }

    private static byte[] Encode(Bitmap bmp, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, encoder, ep);
        return ms.ToArray();
    }
}
#endif
