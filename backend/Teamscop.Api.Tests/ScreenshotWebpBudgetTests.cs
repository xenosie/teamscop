using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// §3.1–§3.3 — the WebP encode budget. The GDI grab that feeds it is Windows-only, but the
/// budget/quality search that decides legibility is pure managed code, so it is exercised here on
/// Linux. These are the checks the owner cared about: the per-display budgets (§3.2) are respected,
/// the output is a real decodable WebP, and quality is not floored into illegibility (§3.3).
/// </summary>
public class ScreenshotWebpBudgetTests
{
    private static int Budget(ScreenshotQuality q) => new StaffTrackingConfig { ScreenshotQuality = q }.TargetBytes;

    [Theory]
    // Everything under 60 KB by the owner's decision — the whole company shares one uplink.
    [InlineData(ScreenshotQuality.Low, 12 * 1024)]
    [InlineData(ScreenshotQuality.Medium, 30 * 1024)]
    [InlineData(ScreenshotQuality.High, 55 * 1024)]
    public void EachTierMapsToItsPerDisplayBudget(ScreenshotQuality quality, int expectedBytes)
        => Assert.Equal(expectedBytes, Budget(quality));

    [Theory]
    [InlineData(ScreenshotQuality.Low)]
    [InlineData(ScreenshotQuality.Medium)]
    [InlineData(ScreenshotQuality.High)]
    public void ACompressibleDesktop_FitsTheBudget_AtFullResolution_AndDecodesAsWebp(ScreenshotQuality quality)
    {
        // A real desktop is mostly large flat regions — a smooth field stands in for that low
        // entropy. WebP fits it in every tier without shrinking, which is the point of §3.1.
        var bgra = Gradient(1920, 1080);
        var (bytes, used) = WebpBudget.EncodeBgra(bgra, 1920, 1080, Budget(quality));

        Assert.NotEmpty(bytes);
        Assert.True(bytes.Length <= Budget(quality), $"{bytes.Length} bytes exceeds the {Budget(quality)} budget");

        var info = Image.Identify(bytes);
        Assert.Equal("Webp", info.Metadata.DecodedImageFormat?.Name);
        Assert.Equal(1920, info.Width); // fit was found at full resolution — no downscale
        Assert.Equal(1080, info.Height);

        // §3.3 — the old JPEG path floored quality at 5; WebP holds text at far higher quality inside
        // the same budget, so a legible floor is the observable improvement.
        Assert.InRange(used, 40, 92);
    }

    [Fact]
    public void HighEntropy_UnderATightBudget_Downscales_RatherThanCrushingQuality()
    {
        // Pure noise cannot be compressed; a full-frame fit at any decent quality is impossible under
        // 20 KB, so the budget encoder shrinks the frame instead of dropping to an unreadable
        // quality. Either way it must remain a real, decodable WebP — never nothing (§6.1).
        var bgra = Noise(1600, 1000, seed: 7);
        var (bytes, used) = WebpBudget.EncodeBgra(bgra, 1600, 1000, Budget(ScreenshotQuality.Low));

        Assert.NotEmpty(bytes);
        var info = Image.Identify(bytes);
        Assert.Equal("Webp", info.Metadata.DecodedImageFormat?.Name);
        Assert.True(info.Width < 1600, "an incompressible frame over budget must downscale");
        Assert.InRange(used, 40, 92); // quality never falls below the legibility floor
    }

    [Fact]
    public void ABiggerBudget_NeverProducesFewerBytes_ForTheSameFrame()
    {
        var bgra = Gradient(1440, 900);
        var low = WebpBudget.EncodeBgra(bgra, 1440, 900, Budget(ScreenshotQuality.Low)).Bytes.Length;
        var high = WebpBudget.EncodeBgra(bgra, 1440, 900, Budget(ScreenshotQuality.High)).Bytes.Length;
        Assert.True(high >= low, $"high tier {high} should not be smaller than low tier {low}");
    }

    /// <summary>A smooth 2-D gradient — large flat neighbourhoods, the low-entropy shape WebP is
    /// meant to fit at full resolution inside even the tightest tier.</summary>
    private static byte[] Gradient(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                buffer[i] = (byte)(x * 255 / width);       // B
                buffer[i + 1] = (byte)(y * 255 / height);  // G
                buffer[i + 2] = (byte)((x + y) * 255 / (width + height)); // R
                buffer[i + 3] = 255;                       // A
            }
        }

        return buffer;
    }

    private static byte[] Noise(int width, int height, int seed)
    {
        var buffer = new byte[width * height * 4];
        var rnd = new Random(seed);
        rnd.NextBytes(buffer);
        for (var i = 3; i < buffer.Length; i += 4)
        {
            buffer[i] = 255; // opaque alpha
        }

        return buffer;
    }
}
