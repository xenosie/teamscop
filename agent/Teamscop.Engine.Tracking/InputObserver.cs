using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// One reading of the OS "last input" clock.
///
/// <see cref="LastInputTick"/> is the raw <c>GetTickCount</c> stamp, kept because activity must be
/// inferred from that value MOVING, not from how large <see cref="Idle"/> currently is. A user
/// typing steadily who happens to be 20 s past their last keystroke at every sampling instant is
/// working; a level test at a 30 s cadence would call them idle forever (§5.1).
/// </summary>
public readonly record struct InputReading(uint LastInputTick, TimeSpan Idle);

/// <summary>
/// Where keyboard/mouse activity is read from.
///
/// This exists because the answer is not "the OS" — it is "the OS, asked from inside the user's
/// interactive session". <c>GetLastInputInfo</c> is documented as session-scoped: it reports input
/// for the session of the CALLING process. Asked from a LocalSystem service in session 0 it
/// returns a value frozen at boot, so idle equals machine uptime and grows 1:1 forever. That is
/// exactly what shipped, and it made every employee read as idle for their whole shift.
///
/// So the capability is explicit. When it cannot be observed the answer is <c>null</c> — never a
/// large idle number, which is the same lie wearing a different coat.
/// </summary>
public interface IInputObserver
{
    /// <summary>False when this process cannot see user input at all (session 0, or non-Windows).</summary>
    bool CanObserve { get; }

    /// <summary>Stable machine-readable reason when <see cref="CanObserve"/> is false; empty otherwise.</summary>
    string UnavailableReason { get; }

    /// <summary>A reading, or null when input is not observable from this process.</summary>
    InputReading? Read();
}

/// <summary>
/// The real one. Refuses to answer anywhere the answer would be fiction.
/// </summary>
public sealed class WindowsInputObserver : IInputObserver
{
    /// <summary>Session 0 is the service session and has been isolated from logons since Vista.</summary>
    public const string ReasonSessionZero = "session_0_no_interactive_desktop";

    public const string ReasonNotWindows = "not_windows";
    public const string ReasonApiFailed = "get_last_input_info_failed";

    private readonly bool _canObserve;
    private readonly string _reason;

    public WindowsInputObserver()
    {
        if (!OperatingSystem.IsWindows())
        {
            _canObserve = false;
            _reason = ReasonNotWindows;
            return;
        }

        _canObserve = CurrentSessionId() != 0;
        _reason = _canObserve ? string.Empty : ReasonSessionZero;
    }

    public bool CanObserve => _canObserve;

    public string UnavailableReason => _reason;

    public InputReading? Read()
    {
        if (!_canObserve || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadWindows();
    }

    /// <summary>
    /// True when this process sits in an interactive session, i.e. when an idle reading would mean
    /// something. Exposed so the service can refuse to host capture rather than discover it later.
    /// </summary>
    public static bool IsInteractiveSession()
        => OperatingSystem.IsWindows() && CurrentSessionId() != 0;

    private static int CurrentSessionId()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Unknown session: treat as unobservable. Silence beats a fabricated idle day.
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static InputReading? ReadWindows()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // LASTINPUTINFO uses GetTickCount (32-bit). Subtract as uint so wrap is correct on
        // long-uptime Win10/11 machines (the same wrap applies to both sides).
        var idleMs = unchecked((uint)Environment.TickCount - info.LastInputTime);
        return new InputReading(info.LastInputTime, TimeSpan.FromMilliseconds(idleMs));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint LastInputTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
