using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Teamscop.App.Composition;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// Defect 8 — stamps <c>HKLM\SOFTWARE\Teamscop\InstalledRole</c> with what this machine actually is.
///
/// The installer cannot know: it runs before anybody has signed in, so it writes "unassigned" and
/// installs the staff service on every PC including the admin's. The agent's capture gate reads
/// this marker (<c>StaffAgentWorker.CaptureEnabledFor</c>) and refuses to capture, to arm the USB
/// floor, or to upload anything when it reads "admin". The desktop app is the only component that
/// ever learns the answer, so it is the component that has to write it — at registration and at
/// every sign-in, since a machine can be re-registered into the other role.
///
/// Best effort by design. HKLM is not writable by a standard user, so this succeeds when the app is
/// launched elevated (which is exactly what happens at the end of an install) and is logged and
/// skipped otherwise. It only ever tightens: the agent treats an absent or unreadable marker as
/// "not admin", so a failed write cannot switch capture ON for a machine that should not have it.
/// </summary>
public static class InstalledRoleWriter
{
    public const string Admin = "admin";
    public const string Staff = "staff";
    private const string KeyPath = @"SOFTWARE\Teamscop";
    private const string ValueName = "InstalledRole";

    /// <summary>
    /// The marker value for a session. Both halves are considered and "admin" wins: the store the
    /// state was filed under and the role the server said the account has must agree before a
    /// machine is treated as monitored.
    /// </summary>
    public static string RoleFor(AgentRole store, string? accountRole)
        => store == AgentRole.Admin || SessionStore.IsAdminRole(accountRole) ? Admin : Staff;

    /// <summary>True when the marker now reads <paramref name="role"/>. False is never fatal.</summary>
    public static bool TryStamp(string role, UiLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TryStampWindows(role, log);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryStampWindows(string role, UiLog log)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var previous = key.GetValue(ValueName) as string;
            if (string.Equals(previous, role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            key.SetValue(ValueName, role, RegistryValueKind.String);
            log.Info($"This machine is registered as {role} (HKLM\\{KeyPath}\\{ValueName})");

            // admin -> staff: the staff service deleted itself when this machine was an admin
            // console (§8.2), and nothing puts it back. A PC repurposed from the owner's desk to an
            // employee would then look enrolled and healthy while capturing nothing at all — the
            // exact silent failure that made the last round of testing so expensive.
            if (IsAdminRoleValue(previous) && string.Equals(role, Staff, StringComparison.OrdinalIgnoreCase))
            {
                EnsureStaffServiceReinstated(log);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or IOException)
        {
            // Not elevated. The agent's fallback is "not admin", which is the safe direction: an
            // admin machine that misses the marker still never captures, because the staff service
            // is never handed a staff token on it.
            log.Debug($"Could not stamp the machine role ({role}): {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>True when a previously-stamped value means "this was an admin machine".</summary>
    public static bool IsAdminRoleValue(string? value)
        => string.Equals(value?.Trim(), Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the staff service back after an admin→staff flip, via the installer's
    /// <c>--ensure-staff-service</c> entry point (the app cannot register a service itself, and the
    /// installer already owns that logic).
    ///
    /// Best effort and non-fatal: this runs at sign-in, and a missing installer or a non-elevated
    /// session must never block the person from using the app. It is logged either way so a machine
    /// that stays untracked has a reason recorded rather than failing silently.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void EnsureStaffServiceReinstated(UiLog log)
    {
        var setup = Path.Combine(InstallRoot(), "bin", "Teamscop_setup.exe");
        if (!File.Exists(setup))
        {
            log.Warn($"Machine flipped admin -> staff but {setup} is missing; the staff service was not reinstated. Re-run the installer.");
            return;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "--ensure-staff-service",
                UseShellExecute = true,   // triggers elevation; registering a service needs it
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(60_000);
            log.Info($"Machine flipped admin -> staff; staff service reinstatement exited {p?.ExitCode.ToString() ?? "unknown"}.");
        }
        catch (Exception ex)
        {
            log.Warn($"Machine flipped admin -> staff but the staff service could not be reinstated: {ex.Message}");
        }
    }

    private static string InstallRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Teamscop");
}
