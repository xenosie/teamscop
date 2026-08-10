using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Teamscop.StaffService;

/// <summary>
/// Reads the machine's declared role from <c>HKLM\SOFTWARE\Teamscop\InstalledRole</c>.
///
/// The installer writes "unassigned"; the desktop app stamps "admin" or "staff" at registration.
/// It is an admin-writable HKLM value, which is why the capture gate keys off it rather than
/// agent-state.json (a file the monitored user owns and can delete). Defect 8: an admin machine
/// must never capture, and the surest signal it is one is this marker reading "admin".
/// </summary>
public static class InstallRoleMarker
{
    public const string Admin = "admin";
    public const string Staff = "staff";
    public const string Unassigned = "unassigned";

    /// <summary>The stored role, lower-cased, or null when the marker is absent / off Windows.</summary>
    public static string? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadWindows();
    }

    /// <summary>True only when the marker positively says "admin". Absence is not admin.</summary>
    public static bool IsAdminMachine() => string.Equals(Read(), Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mirrors the machine's device key to <c>HKLM\SOFTWARE\Teamscop\DeviceKey</c>.
    ///
    /// The uninstall gate needs to know whether a code could exist for this machine. It used to ask
    /// agent-state.json alone, which lives in a directory granted BUILTIN\Users:Modify so the
    /// monitored employee can write their own session — meaning deleting one file they owned opened
    /// the gate with no code required at all. HKLM needs administrator rights to change, and this
    /// service already runs as LocalSystem, so it is the right place to keep the answer.
    ///
    /// This is not a secret. The device key is derived from hardware and readable on the box; a
    /// local administrator can still compute their own code. It closes a bypass, nothing more.
    /// </summary>
    public static void MirrorDeviceKey(string? deviceKey)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }

        MirrorDeviceKeyWindows(deviceKey);
    }

    [SupportedOSPlatform("windows")]
    private static void MirrorDeviceKeyWindows(string deviceKey)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Teamscop", writable: true);
            if (key is null || string.Equals(key.GetValue("DeviceKey") as string, deviceKey, StringComparison.Ordinal))
            {
                return;
            }

            key.SetValue("DeviceKey", deviceKey, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not fatal: the gate falls back to the session file, which is the previous behaviour.
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Teamscop");
            return (key?.GetValue("InstalledRole") as string)?.Trim().ToLowerInvariant();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
