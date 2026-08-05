namespace Teamscop.Engine.Tracking;

/// <summary>
/// Windows multi-monitor capture. Full GDI/JPEG path is enabled only when built on Windows
/// with drawing packs; Linux CI uses the stub in <see cref="ScreenshotEngine"/>.
/// </summary>
internal static class WindowsScreenCapture
{
#if WINDOWS
    public static IReadOnlyList<DisplayCapture> CaptureAll(int targetBytes)
        => WindowsScreenCaptureGdi.CaptureAll(targetBytes);
#else
    public static IReadOnlyList<DisplayCapture> CaptureAll(int targetBytes)
        => Array.Empty<DisplayCapture>();
#endif
}
