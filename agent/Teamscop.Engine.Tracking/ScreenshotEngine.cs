using System.Runtime.InteropServices;
using System.Text.Json;

namespace Teamscop.Engine.Tracking;

public enum ScreenshotQuality
{
    Low = 1,   // §3.2 target <= 12 KB per display, capped at 1280 px wide
    Medium = 2, // §3.2 target <= 30 KB per display, capped at 1600 px wide
    High = 3   // §3.2 target <= 55 KB per display, native resolution
}

public sealed class StaffTrackingConfig
{
    public Guid StaffUserId { get; set; }
    public ScreenshotQuality ScreenshotQuality { get; set; } = ScreenshotQuality.Medium;
    public int ScreenshotPeriodSeconds { get; set; } = 180; // default 3 minutes
    public bool TimeTrackEnabled { get; set; } = true;
    public bool BrowserHistoryEnabled { get; set; } = true;
    public bool ScreenshotEnabled { get; set; } = true;
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // §3.2 — the budget is PER DISPLAY. Enum names are unchanged, so the string stored in
    // staff_tracking_configs.ScreenshotQuality needs no migration.
    //
    // Everything sits under 60 KB by the owner's decision: the whole company shares one office
    // uplink, and screenshots are by far the heaviest thing the agents send. The tiers differ on
    // TWO axes — byte budget and resolution cap — because bytes alone squeeze quality invisibly,
    // while a resolution step is a difference an admin can actually see when choosing a tier.
    public int TargetBytes => ScreenshotQuality switch
    {
        ScreenshotQuality.Low => 12 * 1024,
        ScreenshotQuality.High => 55 * 1024,
        _ => 30 * 1024
    };

    /// <summary>Longest-edge cap per tier. High keeps native resolution; the rest downscale first.</summary>
    public int MaxWidth => ScreenshotQuality switch
    {
        ScreenshotQuality.Low => 1280,
        ScreenshotQuality.High => int.MaxValue,
        _ => 1600
    };
}

public sealed class DisplayCapture
{
    public required int DisplayIndex { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] WebpBytes { get; init; }
    public required int QualityUsed { get; init; }
}

/// <summary>
/// A capture attempt. Carries WHY nothing came back, because "no displays" and "capture threw" and
/// "screenshots are switched off for this employee" are three different facts that used to arrive
/// as the same empty list — so a permanently impossible operation looked identical to "nothing to
/// do" and produced three layers of silence (§6.1: screenshots are proof of work).
/// </summary>
public sealed record ScreenshotCaptureResult(IReadOnlyList<DisplayCapture> Displays, string? UnavailableReason)
{
    public const string ReasonDisabled = "disabled_for_staff";
    public const string ReasonNoDisplays = "no_displays_attached";
    public const string ReasonNotWindows = "capture_unsupported_on_this_os";
    public const string ReasonFailed = "capture_failed";
    public const string ReasonLocked = "workstation_locked";

    public bool Ok => Displays.Count > 0;

    public static ScreenshotCaptureResult Captured(IReadOnlyList<DisplayCapture> displays)
        => new(displays, null);

    public static ScreenshotCaptureResult Unavailable(string reason)
        => new([], reason);
}

/// <summary>
/// Multi-display capture with iterative JPEG quality to meet size budgets.
/// Heavy work runs only when the admin-configured period fires — not every sync tick.
///
/// This runs ONLY in the interactive SessionHelper. A LocalSystem service lives on the
/// <c>Service-0x0-3e7$\Default</c> window station, which is attached to no display: EnumDisplayMonitors
/// finds nothing and the virtual-screen metrics are zero, so every capture there is guaranteed to
/// come back empty. There is deliberately no stub frame for non-Windows any more — a placeholder
/// standing in for a capture that did not happen is worse than a gap, because the gap is reported
/// and the placeholder is not, and a test that passes on invented frames proves nothing.
/// </summary>
public sealed class ScreenshotEngine
{
    public ScreenshotCaptureResult CaptureAllDisplays(StaffTrackingConfig config)
    {
        if (!config.ScreenshotEnabled)
        {
            return ScreenshotCaptureResult.Unavailable(ScreenshotCaptureResult.ReasonDisabled);
        }

        if (!OperatingSystem.IsWindows())
        {
            return ScreenshotCaptureResult.Unavailable(ScreenshotCaptureResult.ReasonNotWindows);
        }

        // Locked, asleep-adjacent, or on the secure desktop: there is nothing capturable, and the
        // encode would only spend CPU on a black lock-screen frame. Skipped BEFORE the expensive
        // BitBlt+encode, not after.
        if (!WindowsScreenCapture.IsInteractiveDesktopAvailable())
        {
            return ScreenshotCaptureResult.Unavailable(ScreenshotCaptureResult.ReasonLocked);
        }

        try
        {
            var captured = WindowsScreenCapture.CaptureAll(config.TargetBytes, config.MaxWidth);
            return captured.Count > 0
                ? ScreenshotCaptureResult.Captured(captured)
                : ScreenshotCaptureResult.Unavailable(ScreenshotCaptureResult.ReasonNoDisplays);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException
                                       or OutOfMemoryException or UnauthorizedAccessException)
        {
            return ScreenshotCaptureResult.Unavailable(
                ScreenshotCaptureResult.ReasonFailed + ":" + ex.GetType().Name);
        }
    }

    public byte[] SerializeCaptures(IReadOnlyList<DisplayCapture> captures, long configVersion)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            configVersion,
            capturedAt = DateTimeOffset.UtcNow,
            displays = captures.Select(c => new
            {
                c.DisplayIndex,
                c.Width,
                c.Height,
                c.QualityUsed,
                size = c.WebpBytes.Length,
                webpBase64 = Convert.ToBase64String(c.WebpBytes)
            })
        });

}
