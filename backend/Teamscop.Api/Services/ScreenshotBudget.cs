namespace Teamscop.Api.Services;

/// <summary>
/// The §3.2 per-display size budget, keyed to the staff member's screenshot-quality setting:
/// High 100 KB, Medium 60 KB, Low 20 KB. The agent encodes to fit this (WebP, §3.1); the server
/// treats it only as a sanity check. A capture is NEVER dropped for exceeding it — §13.1 forbids
/// dropping anything — but a gross overshoot is logged so a mis-encoding agent is visible.
/// </summary>
public static class ScreenshotBudget
{
    private const int Kib = 1024;

    /// <summary>Per-display byte budget for a quality name (case-insensitive). Defaults to Medium.</summary>
    public static int BytesFor(string? quality) => (quality?.Trim().ToLowerInvariant()) switch
    {
        "low" => 20 * Kib,
        "high" => 100 * Kib,
        _ => 60 * Kib,
    };

    /// <summary>
    /// The threshold above which a display's size is worth a warning. A generous multiple of the
    /// budget: real WebP output lands comfortably under budget, so only a genuinely mis-encoded
    /// (or uncompressed) frame trips it, never a frame that is merely a little large.
    /// </summary>
    public static int WarnAbove(string? quality) => BytesFor(quality) * 2;
}
