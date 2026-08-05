namespace Teamscop.Engine.Tracking;

/// <summary>
/// Placeholder for OS screen capture. Portable builds use <see cref="ScreenshotEngine"/> stub
/// frames; Windows installer builds can swap in GDI via WindowsScreenCapture.Gdi.cs.
/// </summary>
internal static class WindowsScreenCapture
{
    public static IReadOnlyList<DisplayCapture> CaptureAll(int targetBytes)
        => Array.Empty<DisplayCapture>();
}
