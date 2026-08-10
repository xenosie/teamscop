using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>The app's claim about the service. Ordered from healthy to worst.</summary>
public enum ServiceState
{
    /// <summary>Could not determine; must never be treated as a defect.</summary>
    Unknown,

    Running,
    StartPending,
    Stopped,
    Disabled,

    /// <summary>SCM says the service does not exist.</summary>
    NotInstalled,

    /// <summary>Registered, but its binary is gone from disk.</summary>
    ExeMissing
}

/// <summary>
/// The desktop app as the second, independent reporter. The staff service reports on itself, which
/// is worth nothing precisely when it matters: a service that has been stopped, disabled or deleted
/// cannot tell anyone. So the app answers one narrow question about <c>TeamscopStaff</c> — is it
/// there and is it running? — and that lets the server separate "this machine is off" (nothing
/// reports at all) from "this machine is on and someone stopped the monitoring service".
///
/// <para>
/// <see cref="ServiceState.Unknown"/> is load-bearing. The server treats it as "no claim" and will
/// never mark a machine broken on it, so every failure path here — a wrong OS, a timeout, access
/// denied, output this code cannot read — must land on Unknown. <see cref="ServiceState.Stopped"/>
/// and <see cref="ServiceState.NotInstalled"/> are accusations against a person, and are returned
/// only when the SCM positively said so. Neither is ever an error fallback.
/// </para>
///
/// <para>
/// The probe shells out to <c>sc.exe</c> rather than using <c>ServiceController</c>: that type lives
/// in the System.ServiceProcess.ServiceController package, this project does not reference it, and
/// adding a dependency to an Avalonia shell for two string reads is not worth it. The cost is that
/// the answer comes back as localized console text; see <see cref="ParseRunState"/> for how that is
/// handled without guessing.
/// </para>
/// </summary>
public static class ServiceProbe
{
    /// <summary>Canonical name, shared with the installer so the two cannot drift apart.</summary>
    internal const string ServiceName = ServiceInstallerHints.ServiceName;

    /// <summary>
    /// <c>ERROR_SERVICE_DOES_NOT_EXIST</c>. sc.exe surfaces the Win32 error from OpenService as its
    /// own exit code, so this is a locale-independent "not installed" — unlike its printed message.
    /// </summary>
    private const int ErrorServiceDoesNotExist = 1060;

    /// <summary><c>SERVICE_DISABLED</c> as reported in the START_TYPE field of <c>sc qc</c>.</summary>
    private const int StartTypeDisabled = 4;

    /// <summary>
    /// Generous for a local SCM read that normally returns in milliseconds, short enough that a
    /// wedged sc.exe cannot stall the caller's reporting loop. On expiry the answer is Unknown.
    /// </summary>
    private const int ProbeMilliseconds = 5_000;

    /// <summary>Drain window for the redirected pipes once the process has already exited.</summary>
    private const int DrainMilliseconds = 2_000;

    /// <summary>
    /// What this machine can honestly say about the staff service right now. Never throws, and
    /// returns <see cref="ServiceState.Unknown"/> off Windows and on every error.
    ///
    /// <para>
    /// Each call spawns up to two short-lived processes and blocks until both answer, which a
    /// wedged SCM can stretch to roughly fourteen seconds. Call it at reporting cadence from a
    /// background thread, never from the UI thread and never per frame.
    /// </para>
    /// </summary>
    public static ServiceState Current()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceState.Unknown;
        }

        try
        {
            return CurrentWindows();
        }
        catch (Exception)
        {
            // Deliberately unfiltered. The contract this whole file exists to keep is that a
            // failure here is silence, not an accusation, and an escaped exception from an
            // unanticipated corner would break that as surely as returning Stopped would.
            return ServiceState.Unknown;
        }
    }

    /// <summary>
    /// Two reads: the live run state from <c>sc query</c>, and the configuration from <c>sc qc</c>.
    /// The worst fact that was positively established wins, because the configuration outranks the
    /// moment: a service that is running right now but disabled, or running from a binary that has
    /// been deleted, is already gone as far as the next reboot is concerned.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static ServiceState CurrentWindows()
    {
        if (Run("query") is not { } query)
        {
            return ServiceState.Unknown;
        }

        if (query.ExitCode == ErrorServiceDoesNotExist)
        {
            return ServiceState.NotInstalled;
        }

        // Any other non-zero exit (access denied, an SCM that is not accepting calls) means we did
        // not learn the state. It does not mean the service is stopped.
        var running = query.ExitCode == 0 ? ParseRunState(query.Output) : null;

        if (Run("qc") is { } config)
        {
            if (config.ExitCode == ErrorServiceDoesNotExist)
            {
                // Deleted between the two calls, which is exactly the tampering this looks for.
                return ServiceState.NotInstalled;
            }

            if (config.ExitCode == 0)
            {
                if (BinaryIsMissing(config.Output))
                {
                    return ServiceState.ExeMissing;
                }

                if (IsDisabled(config.Output))
                {
                    return ServiceState.Disabled;
                }
            }
        }

        return running ?? ServiceState.Unknown;
    }

    /// <summary>
    /// True only when an ImagePath was read, resolved to a real Windows path, and that file is
    /// positively not there. Every uncertain step returns false: an unreadable path is not evidence
    /// of deletion.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool BinaryIsMissing(string configOutput)
    {
        if (ParseImagePath(configOutput) is not { } image)
        {
            return false;
        }

        // ImagePath is stored raw, so it may still hold %SystemRoot% and friends.
        var expanded = Environment.ExpandEnvironmentVariables(image);
        if (!LooksLikeWindowsPath(expanded) || !IsAscii(expanded))
        {
            return false;
        }

        // The cheap answer first: if the file is visible, there is nothing to report.
        return !File.Exists(expanded) && IsPositivelyAbsent(expanded);
    }

    /// <summary>
    /// Distinguishes "the file is gone" from "this process may not look at it", which
    /// <see cref="File.Exists"/> cannot: it returns false for a denied path exactly as it does for a
    /// deleted one.
    ///
    /// <para>
    /// That difference is the whole finding. The app runs as the monitored standard user, and the
    /// install tree is ACL'd — earlier installs locked <c>bin</c> to SYSTEM and Administrators
    /// outright, and the Administrators SID is deny-only in a UAC-filtered token. On every one of
    /// those machines the service binary is perfectly present and simply invisible to us, so a bare
    /// <c>File.Exists</c> would report a healthy install as tampered with. Opening the file tells
    /// the two apart: only a genuine absence raises the not-found exceptions.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsPositivelyAbsent(string path)
    {
        try
        {
            // Share everything: the binary of a running service is held open as an image section,
            // and a sharing violation here would be a false accusation of the loudest kind.
            using var probe = File.Open(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The only two answers that mean the path was resolvable and nothing was at the end of
            // it. Everything else — denied, locked, offline share, malformed path — is no claim.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when every character survived the console round-trip unambiguously.
    ///
    /// <para>
    /// sc.exe writes its output in a console code page, and a redirected child of a GUI process has
    /// no console to agree with about which one that is. ASCII decodes identically under all of
    /// them; anything above it may already be mojibake, and a mangled path would fail
    /// <see cref="File.Exists"/> for a file that is sitting right there. Non-ASCII therefore buys
    /// silence rather than a guess.
    /// </para>
    /// </summary>
    internal static bool IsAscii(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAscii(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The STATE field of <c>sc query</c>, or null when it cannot be read.
    ///
    /// <para>
    /// Only the numeric code is trusted. sc.exe localizes both the field labels and the symbolic
    /// names, so on a non-English Windows this finds nothing and the probe reports Unknown. That is
    /// the intended trade: a positional guess at which number on which line meant the state would
    /// eventually accuse an innocent machine, and silence costs the server only one of its two
    /// reporters.
    /// </para>
    /// </summary>
    internal static ServiceState? ParseRunState(string queryOutput) => FieldNumber(queryOutput, "STATE") switch
    {
        1 => ServiceState.Stopped,
        2 => ServiceState.StartPending,

        // SERVICE_STOP_PENDING makes no claim. Every ordinary Windows shutdown, service upgrade and
        // crash-recovery restart passes through this state on a machine nobody has touched, and the
        // app is still polling while it does. A stop that was really someone's doing settles into
        // STOPPED within seconds and the next poll reports it, so the cost of waiting is one cycle
        // and the cost of not waiting is accusing people who rebooted.
        3 => null,

        4 => ServiceState.Running,

        // SERVICE_CONTINUE_PENDING, on its way back up.
        5 => ServiceState.StartPending,

        // Paused states cannot occur for this service, and an unrecognised code is not a defect.
        _ => null
    };

    /// <summary>True when START_TYPE says the service will not start again on its own.</summary>
    internal static bool IsDisabled(string configOutput)
        => FieldNumber(configOutput, "START_TYPE") == StartTypeDisabled;

    /// <summary>
    /// The executable out of the BINARY_PATH_NAME field, without its arguments, or null when the
    /// line is absent or cannot be split with confidence.
    /// </summary>
    internal static string? ParseImagePath(string configOutput)
    {
        if (FieldValue(configOutput, "BINARY_PATH_NAME") is not { } value || value.Length == 0)
        {
            return null;
        }

        var text = value;

        // Object-manager prefix used by driver-style registrations; Win32 file APIs reject it.
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        // Unquoted, and Windows paths contain spaces, so whitespace cannot delimit the executable
        // from its arguments. The suffix can, but only where it ends a token: a directory called
        // "tools.exec" would otherwise cut the path in half and hand us a file that is absent
        // because it never existed. Without a clean split we make no claim, which costs at most a
        // missed ExeMissing rather than a false one.
        for (var i = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
             i >= 0;
             i = text.IndexOf(".exe", i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + 4;
            if (end == text.Length || char.IsWhiteSpace(text[end]))
            {
                return text[..end];
            }
        }

        return null;
    }

    /// <summary>
    /// A drive-rooted or UNC Windows path. Hand-rolled rather than <c>Path.IsPathRooted</c> so the
    /// parsing stays pure and gives the same answer on the Linux test runner.
    /// </summary>
    internal static bool LooksLikeWindowsPath(string path)
    {
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return true;
        }

        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);

        static bool IsSeparator(char c) => c is '\\' or '/';
    }

    /// <summary>The text after the colon on the line whose label is exactly <paramref name="label"/>.</summary>
    private static string? FieldValue(string output, string label)
    {
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(colon + 1)..].Trim();
        }

        return null;
    }

    /// <summary>
    /// The leading integer of a field, as in "4  RUNNING". Null when the field is missing or does
    /// not start with a number, which is how localized output falls through to Unknown.
    /// </summary>
    private static int? FieldNumber(string output, string label)
    {
        if (FieldValue(output, label) is not { } value)
        {
            return null;
        }

        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        return digits > 0
               && int.TryParse(value.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Runs one sc.exe verb against the service. Null when the tool could not be run or did not
    /// finish in time, which the caller reads as "no claim".
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (int ExitCode, string Output)? Run(string verb)
    {
        if (SystemExe("sc.exe") is not { } sc)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = sc,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // The child inherits our current directory, and the shortcut this app is launched from
            // can leave that pointing at the folder the employee downloaded the installer into. The
            // system directory is the one place a standard user cannot drop a DLL for sc.exe to
            // pick up.
            WorkingDirectory = Path.GetDirectoryName(sc) ?? string.Empty
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(ServiceName);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        // Read both pipes concurrently with the wait: sc.exe writes its failures to stderr, and a
        // full pipe nobody is draining is the classic way to hang a redirected child.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ProbeMilliseconds))
        {
            TryKill(process);
            Abandon(stdout);
            Abandon(stderr);
            return null;
        }

        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, DrainMilliseconds);
        }
        catch (AggregateException)
        {
            // A broken pipe loses the text but not the exit code, which alone still distinguishes
            // "no such service" from everything else.
        }

        return (process.ExitCode, string.Concat(Text(stdout), "\n", Text(stderr)));

        static string Text(Task<string> read)
        {
            if (read.IsCompletedSuccessfully)
            {
                return read.Result;
            }

            Abandon(read);
            return string.Empty;
        }

        // A read still running when we walk away keeps a reference to a stream this method is about
        // to dispose, and will fault on it. Nothing would ever look at that fault, and an
        // unobserved task exception in a probe that runs every thirty seconds forever is a slow
        // drip of finalizer-thread noise at best. Observing it costs one continuation.
        static void Abandon(Task<string> read) => _ = read.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Absolute path to a Windows-supplied tool. A bare file name resolves from the calling
    /// executable's directory first, and this app launches from a directory a standard user can
    /// write to, so a planted sc.exe would otherwise get to dictate what the server is told about
    /// the monitoring service. Null when the system directory cannot be determined: no answer is
    /// better than an answer from an unknown binary.
    /// </summary>
    private static string? SystemExe(string fileName)
    {
        var systemDirectory = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(systemDirectory)
            ? null
            : Path.Combine(systemDirectory, fileName);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception or AggregateException)
        {
            // It exited on its own between the timeout and here, or refuses to die. Either way the
            // caller is already getting Unknown.
        }
    }
}
