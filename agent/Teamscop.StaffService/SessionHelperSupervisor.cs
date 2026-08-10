using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.StaffService;

/// <summary>
/// Keeps <c>Teamscop.SessionHelper.exe</c> running in the interactive session.
///
/// The service — not the registry — owns the helper's lifetime. The installer's HKCU Run key could
/// never work: it lands in whichever account consented to the elevation prompt (not necessarily the
/// monitored user), it only fires at the NEXT logon, and above all the helper lives in a directory
/// the installer locked to SYSTEM+Administrators, so a non-elevated logon-time launch got
/// ACCESS_DENIED. That is why zero screenshots ever reached the server.
///
/// Instead the service launches the helper into the active console session with the logged-on
/// user's token (<see cref="InteractiveProcessLauncher"/>, the mechanism the USB sticker already
/// proved), and relaunches it whenever it has not pinged in <see cref="RelaunchAfter"/>. This
/// survives logoff/logon, fast user switching and a killed helper, and needs no reboot: the first
/// session is covered the moment the service starts.
/// </summary>
public sealed class SessionHelperSupervisor(
    TrackingCoordinator tracking,
    ILogger<SessionHelperSupervisor> logger)
{
    /// <summary>Give a freshly launched helper this long to ping before we consider relaunching.</summary>
    public static readonly TimeSpan RelaunchAfter = TimeSpan.FromSeconds(90);

    /// <summary>Do not spawn a second helper within this window of the last spawn (launch debounce).</summary>
    private static readonly TimeSpan MinRelaunchInterval = TimeSpan.FromSeconds(45);

    private DateTimeOffset _lastLaunchAt = DateTimeOffset.MinValue;
    private int? _lastPid;

    public string HelperPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Teamscop.SessionHelper.exe");

    /// <summary>
    /// Ensures a helper is (being) started when none has pinged recently and a console user exists.
    /// Called once per service loop; cheap and idempotent.
    /// </summary>
    public void Ensure()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Off Windows there is no interactive-session launch and no capture pipe consumer; the
            // service simply reports the helper as never started.
            return;
        }

        if (tracking.IsSessionHelperAlive)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastLaunchAt < MinRelaunchInterval)
        {
            // A launch is in flight; give it time to connect before trying again.
            return;
        }

        if (!InteractiveProcessLauncher.HasActiveConsoleSession())
        {
            // Nobody is logged on at the console. Nothing to capture; not an error.
            return;
        }

        if (!File.Exists(HelperPath))
        {
            logger.LogWarning("SessionHelper.exe not found next to the service at {Path}", HelperPath);
            return;
        }

        _lastLaunchAt = now;
        try
        {
            var pid = InteractiveProcessLauncher.TryStartInActiveSession(HelperPath, arguments: string.Empty);
            if (pid is { } launched)
            {
                _lastPid = launched;
                logger.LogInformation(
                    "Launched SessionHelper into the active console session (pid {Pid}); helperState was {State}",
                    launched, tracking.HelperState);
            }
            else
            {
                logger.LogWarning(
                    "Could not launch SessionHelper into the active session (no user token or CreateProcessAsUser failed). "
                    + "Capture stays unavailable and the period will surface as a gap.");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "SessionHelper launch API unavailable on this OS");
        }
    }

    public int? LastLaunchedPid => _lastPid;
}
