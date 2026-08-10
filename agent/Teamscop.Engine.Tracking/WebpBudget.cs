using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// §3.1–§3.3 — encodes one captured display to a lossy WebP that fits a per-display byte budget
/// (§3.2: High 100 KB / Medium 60 KB / Low 20 KB) while staying legible.
///
/// It is pure and fully managed (ImageSharp carries no native dll), so the budget/quality search is
/// exercised on Linux CI even though the GDI grab that feeds it is Windows-only. The input is a raw
/// BGRA pixel grid — the exact memory layout <c>System.Drawing.Bitmap.LockBits(Format32bppArgb)</c>
/// produces — so <see cref="WindowsScreenCapture"/> hands pixels over a <c>byte[]</c> and never has
/// to reference an ImageSharp type (which would clash with <c>System.Drawing</c> on
/// <c>Image</c>/<c>Rectangle</c>).
/// </summary>
public static class WebpBudget
{
    // §3.3 — the old GDI+ JPEG path floored quality at 5 and hard-downscaled to 320 px, which is
    // exactly why the owner could not read the output. WebP holds text far better at low bitrate, so
    // the floor is raised and downscaling is a genuine last resort rather than the first move.
    private const int MinQuality = 40;
    private const int MaxQuality = 92;

    // Never shrink below this — a screenshot smaller than this proves nothing (§6.1).
    private const int MinWidth = 640;
    private const int MinHeight = 360;

    /// <summary>
    /// Encodes a BGRA pixel grid (<paramref name="bgra"/> length must be <c>width*height*4</c>) to a
    /// lossy WebP no larger than <paramref name="targetBytes"/> where a fit exists, downscaling only
    /// when even the quality floor at full resolution overflows. The result is always a decodable,
    /// non-empty WebP.
    /// </summary>
    public static (byte[] Bytes, int QualityUsed) EncodeBgra(byte[] bgra, int width, int height, int targetBytes)
        => EncodeBgra(bgra, width, height, targetBytes, maxWidth: int.MaxValue);

    /// <summary>
    /// As above, additionally capping the output's width BEFORE the quality search. The lower
    /// quality tiers cap resolution as well as bytes: shrinking first spends the byte budget on a
    /// crisp small image instead of a blurry large one, and costs one resize instead of several
    /// failed full-size encodes.
    /// </summary>
    public static (byte[] Bytes, int QualityUsed) EncodeBgra(
        byte[] bgra, int width, int height, int targetBytes, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
        if (maxWidth > 0 && image.Width > maxWidth)
        {
            var scaledHeight = Math.Max(1, (int)((long)image.Height * maxWidth / image.Width));
            using var capped = image.Clone(x => x.Resize(maxWidth, scaledHeight));
            return Encode(capped, targetBytes);
        }

        return Encode(image, targetBytes);
    }

    /// <summary>Encodes an already-decoded image to a budgeted WebP. Does not dispose the input.</summary>
    public static (byte[] Bytes, int QualityUsed) Encode(Image<Bgra32> image, int targetBytes)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 1) Full resolution: binary-search quality for the sharpest result at or under budget.
        var best = SearchQuality(image, targetBytes, out var bestQuality);
        if (best is not null)
        {
            return (best, bestQuality);
        }

        // 2) Even the floor quality overflows at full resolution — shrink in steps. A smaller WebP at
        //    a decent quality reads far better than a full-size one crushed to quality 5.
        var width = image.Width;
        var height = image.Height;
        for (var step = 0; step < 5 && (width > MinWidth || height > MinHeight); step++)
        {
            width = Math.Max(MinWidth, width * 3 / 4);
            height = Math.Max(MinHeight, height * 3 / 4);
            using var scaled = image.Clone(x => x.Resize(width, height));
            var fit = SearchQuality(scaled, targetBytes, out var q);
            if (fit is not null)
            {
                return (fit, q);
            }
        }

        // 3) Last resort: the smallest allowed size at the floor quality. Over budget, but always a
        //    real, decodable frame — §6.1 makes a captured screenshot proof of work, never nothing.
        var w = Math.Min(image.Width, MinWidth);
        var h = Math.Min(image.Height, MinHeight);
        using var small = image.Clone(x => x.Resize(w, h));
        return (EncodeAt(small, MinQuality), MinQuality);
    }

    /// <summary>
    /// Remembered winning quality per (size, budget). Desktop content barely changes between
    /// captures, so the previous winner is almost always this capture's winner too — trying it
    /// first turns a ~7-encode binary search into 2 encodes on the steady path, which is the
    /// difference between a visible CPU blip every cycle and none on a weak office PC.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int W, int H, int Target), int>
        QualityHints = new();

    private static byte[]? SearchQuality(Image<Bgra32> image, int targetBytes, out int quality)
    {
        var hintKey = (image.Width, image.Height, targetBytes);
        if (QualityHints.TryGetValue(hintKey, out var hint) && hint > MinQuality && hint < MaxQuality)
        {
            var atHint = EncodeAt(image, hint);
            if (atHint.Length <= targetBytes)
            {
                // Fits at the remembered quality. One step up either still fits (screen got
                // simpler — take the sharper frame) or does not (unchanged — keep the hint).
                var above = EncodeAt(image, Math.Min(MaxQuality, hint + 4));
                if (above.Length <= targetBytes)
                {
                    quality = Math.Min(MaxQuality, hint + 4);
                    QualityHints[hintKey] = quality;
                    return above;
                }

                quality = hint;
                return atHint;
            }

            // The screen got busier; fall through to the full search and re-learn.
        }

        byte[]? best = null;
        quality = MinQuality;
        var lo = MinQuality;
        var hi = MaxQuality;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var bytes = EncodeAt(image, mid);
            if (bytes.Length <= targetBytes)
            {
                best = bytes;
                quality = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best is not null)
        {
            QualityHints[hintKey] = quality;
        }

        return best;
    }

    private static byte[] EncodeAt(Image<Bgra32> image, int quality)
    {
        var encoder = new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossy,
            Quality = quality,
            Method = WebpEncodingMethod.Level4
        };
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
