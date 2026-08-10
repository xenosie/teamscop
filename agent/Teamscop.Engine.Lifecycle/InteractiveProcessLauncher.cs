using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Starts a process on the employee's desktop from a service that lives in session 0.
///
/// <c>Teamscop.StaffService</c> runs as LocalSystem in session 0, and session 0 has been isolated
/// from interactive logons since Windows Vista: a plain <c>Process.Start</c> from the service
/// creates a window nobody can ever see. This takes the active console session's user token and
/// creates the process with it, on <c>winsta0\default</c>. Two callers depend on it — the USB
/// approval sticker (§9.3) and, decisively, the capture SessionHelper, which the installer's HKCU
/// Run key could never launch under the locked ACL and which the service must therefore own.
///
/// <c>WTSQueryUserToken</c> needs SeTcbPrivilege, which LocalSystem has and an ordinary
/// administrator does not; every failure path returns a "did not start" result so the caller can
/// fall back or retry rather than lose the child entirely.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InteractiveProcessLauncher
{
    private const uint NoActiveSession = 0xFFFFFFFF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNoWindow = 0x08000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint WaitObject0 = 0x00000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    /// <summary>True when a user is logged on at the physical console, i.e. a desktop exists to draw on.</summary>
    public static bool HasActiveConsoleSession()
        => OperatingSystem.IsWindows() && WTSGetActiveConsoleSessionId() != NoActiveSession;

    /// <summary>
    /// Launches <paramref name="exePath"/> in the interactive session and RETURNS immediately with
    /// the child's PID, without waiting on it and without ever terminating it. This is the variant
    /// the SessionHelper needs: the helper is meant to run for the whole logon, so waiting on it or
    /// killing it on a timeout — as <see cref="TryRunInActiveSession"/> does — would be wrong.
    /// </summary>
    /// <returns>The child process id, or null when it could not be started.</returns>
    public static int? TryStartInActiveSession(string exePath, string arguments, bool createNoWindow = true)
    {
        return LaunchAsUser(exePath, arguments, createNoWindow, waitTimeout: null, out var pid) ? pid : null;
    }

    /// <summary>Launch, wait up to <paramref name="timeout"/>, terminate if it overran. True when it ran.</summary>
    public static bool TryRunInActiveSession(string exePath, string arguments, TimeSpan timeout)
        => LaunchAsUser(exePath, arguments, createNoWindow: false, timeout, out _);

    private static bool LaunchAsUser(
        string exePath,
        string arguments,
        bool createNoWindow,
        TimeSpan? waitTimeout,
        out int pid)
    {
        pid = 0;
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession)
        {
            // Nobody is logged on at the console — there is no desktop to run on.
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            return false;
        }

        var duplicated = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var process = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out duplicated))
            {
                return false;
            }

            // Without the user's environment block the child inherits SYSTEM's %TEMP% and %PATH% —
            // and, worse for the helper, SYSTEM's %LOCALAPPDATA% (systemprofile), where no Chrome
            // profile lives. The block is what makes the helper resolve the real user's paths.
            if (!CreateEnvironmentBlock(out environment, duplicated, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            var flags = CreateUnicodeEnvironment | (createNoWindow ? CreateNoWindow : CreateNewConsole);
            var commandLine = new System.Text.StringBuilder($"\"{exePath}\" {arguments}");
            if (!CreateProcessAsUserW(
                    duplicated,
                    exePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    flags,
                    environment,
                    Path.GetDirectoryName(exePath),
                    ref startup,
                    out var info))
            {
                return false;
            }

            process = info.hProcess;
            thread = info.hThread;
            pid = info.dwProcessId;

            if (waitTimeout is not { } timeout)
            {
                // Fire and forget: the child owns its own lifetime.
                return true;
            }

            var waited = WaitForSingleObject(process, (uint)Math.Max(1000, timeout.TotalMilliseconds));
            if (waited != WaitObject0)
            {
                TerminateProcess(process, 2);
            }

            return true;
        }
        finally
        {
            CloseHandle(userToken);
            if (duplicated != IntPtr.Zero) CloseHandle(duplicated);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
