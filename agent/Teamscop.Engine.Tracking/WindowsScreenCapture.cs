using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Multi-monitor GDI capture for Windows 10 / 11 (user32 + gdi32 + System.Drawing).
/// No WinForms dependency — works from SessionHelper on net8.0 win-x64.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScreenCapture
{
    /// <summary>
    /// True when the interactive desktop can actually be captured — i.e. the workstation is not
    /// locked and not on the secure desktop. While locked (which is also the state a machine sits
    /// in through modern standby's brief maintenance wakes), OpenInputDesktop refuses, and a
    /// capture attempt would only burn CPU to produce a black frame of the lock screen. §5.3: a
    /// locked or sleeping machine produces no screenshots, by design rather than by accident.
    /// </summary>
    public static bool IsInteractiveDesktopAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        CloseDesktop(desktop);
        return true;
    }

    public static IReadOnlyList<DisplayCapture> CaptureAll(int targetBytes, int maxWidth = int.MaxValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<DisplayCapture>();
        }

        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
        {
            // Fallback: primary virtual metrics
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0)
            {
                w = GetSystemMetrics(SM_CXSCREEN);
                h = GetSystemMetrics(SM_CYSCREEN);
                x = 0;
                y = 0;
            }

            if (w <= 0 || h <= 0)
            {
                return Array.Empty<DisplayCapture>();
            }

            monitors.Add(new MonitorEntry(
                new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
                IsPrimary: true));
        }

        var results = new List<DisplayCapture>();
        var index = 1;
        // Primary is Display 1, matching Windows Display Settings; the rest follow left to right.
        foreach (var entry in monitors
                     .OrderByDescending(e => e.IsPrimary)
                     .ThenBy(e => e.Rect.Left)
                     .ThenBy(e => e.Rect.Top))
        {
            var m = entry.Rect;
            var width = m.Right - m.Left;
            var height = m.Bottom - m.Top;
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            using var bmp = CaptureRect(m.Left, m.Top, width, height);
            if (bmp is null)
            {
                continue;
            }

            var (webp, quality) = EncodeToTarget(bmp, targetBytes, maxWidth);
            results.Add(new DisplayCapture
            {
                DisplayIndex = index++,
                Width = width,
                Height = height,
                WebpBytes = webp,
                QualityUsed = quality
            });
        }

        return results;
    }

    private static Bitmap? CaptureRect(int x, int y, int width, int height)
    {
        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return null;
        }

        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = CreateCompatibleBitmap(hdcScreen, width, height);
        var old = SelectObject(hdcMem, hBmp);
        try
        {
            if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, x, y, SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            return Image.FromHbitmap(hBmp);
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(hBmp);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private const uint MONITORINFOF_PRIMARY = 0x1;

    private readonly record struct MonitorEntry(RECT Rect, bool IsPrimary);

    /// <summary>
    /// Enumerates monitors and records which one Windows considers primary.
    ///
    /// The primary flag used to be discarded and displays were numbered purely left-to-right, so
    /// "Display 1" meant "leftmost screen". Windows numbers the primary monitor 1 regardless of
    /// where it sits, so anyone whose primary is the right-hand screen saw our labels reversed
    /// against their own Display Settings — and the screenshot viewer's picker with them.
    /// </summary>
    private static List<MonitorEntry> EnumerateMonitors()
    {
        var list = new List<MonitorEntry>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorEntry(info.Monitor, (info.Flags & MONITORINFOF_PRIMARY) != 0));
                }
                else
                {
                    list.Add(new MonitorEntry(rect, false));
                }

                return true;
            },
            IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// §3.1 — WebP now, not JPEG. The pixels are copied out of the GDI bitmap into a tight BGRA grid
    /// and the budget/quality search runs in <see cref="WebpBudget"/>, which is managed and shared
    /// with Linux CI. Kept here (behind the Windows guard) is only the LockBits bridge, because
    /// System.Drawing is the Windows-only half.
    /// </summary>
    private static (byte[] Webp, int Quality) EncodeToTarget(Bitmap bmp, int targetBytes, int maxWidth)
    {
        var bgra = ToBgra(bmp, out var width, out var height);
        return WebpBudget.EncodeBgra(bgra, width, height, targetBytes, maxWidth);
    }

    /// <summary>
    /// Copies the bitmap into a contiguous 32bpp BGRA buffer (the layout ImageSharp's
    /// <c>Bgra32</c> expects). Rows are compacted when GDI hands back a padded stride.
    /// </summary>
    private static byte[] ToBgra(Bitmap bmp, out int width, out int height)
    {
        width = bmp.Width;
        height = bmp.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var tight = width * 4;
            var buffer = new byte[tight * height];
            if (data.Stride == tight)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), buffer, row * tight, tight);
                }
            }

            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
