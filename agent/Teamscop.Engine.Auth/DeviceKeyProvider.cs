using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Teamscop.Engine.Auth;

/// <summary>
/// Hardware device identity for signup/login (survives OS reinstall).
///
/// DeviceKey = SHA256 of stable machine material:
///   SMBIOS product UUID + baseboard serial + BIOS serial
///   + all physical NIC MACs (sorted)
///   + internal disk serials (sorted)
///
/// SMBIOS alone is not enough — OEMs often reuse the same UUID across PCs.
/// MAC + disk serials are the real distinguishers and persist across reinstall.
/// MachineGuid / random install IDs are intentionally NOT used (wiped with the OS).
/// </summary>
public sealed class DeviceKeyProvider : IDeviceKeyProvider
{
    private const string HashPrefix = "teamscop-device-v3";

    private readonly object _gate = new();
    private string? _cached;

    public string GetDeviceKey()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        lock (_gate)
        {
            _cached ??= ComputeDeviceKey();
            return _cached;
        }
    }

    private static string ComputeDeviceKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ComputeWindowsDeviceKey();
        }

        return ComputeLinuxDeviceKey();
    }

    private static string ComputeWindowsDeviceKey()
    {
        var uuid = SanitizeHardware(QueryWindowsCim("Win32_ComputerSystemProduct", "UUID"));
        var board = SanitizeHardware(QueryWindowsCim("Win32_BaseBoard", "SerialNumber"));
        var bios = SanitizeHardware(QueryWindowsCim("Win32_BIOS", "SerialNumber"));
        if (IsPlaceholderUuid(uuid))
        {
            uuid = string.Empty;
        }

        var macs = string.Join(",", GetPhysicalMacs());
        var disks = string.Join(",", QueryWindowsDiskSerials());

        if (CountStrongParts(uuid, board, bios, macs, disks) == 0)
        {
            throw new InvalidOperationException(
                "Cannot derive a stable device identity from this PC (no SMBIOS/MAC/disk serial). " +
                "Teamscop needs hardware identity that survives OS reinstall.");
        }

        // Prefer MAC or disk when present — those break OEM SMBIOS collisions.
        return HashNormalized(HashPrefix, uuid, board, bios, macs, disks);
    }

    private static string ComputeLinuxDeviceKey()
    {
        var machineId = TryRead("/etc/machine-id") ?? string.Empty;
        var uuid = SanitizeHardware(TryRead("/sys/class/dmi/id/product_uuid") ?? string.Empty);
        var board = SanitizeHardware(TryRead("/sys/class/dmi/id/board_serial") ?? string.Empty);
        var bios = SanitizeHardware(TryRead("/sys/class/dmi/id/bios_serial") ?? string.Empty);
        if (IsPlaceholderUuid(uuid))
        {
            uuid = string.Empty;
        }

        var macs = string.Join(",", GetPhysicalMacs());
        // machine-id is OS-install scoped — only use when no other hardware exists (dev/test).
        var partsStrong = CountStrongParts(uuid, board, bios, macs, "");
        var installScoped = partsStrong == 0 ? machineId : string.Empty;
        return HashNormalized(HashPrefix, uuid, board, bios, macs, installScoped);
    }

    private static int CountStrongParts(params string[] parts)
        => parts.Count(p => !string.IsNullOrWhiteSpace(p));

    private static string QueryWindowsCim(string className, string property)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"(Get-CimInstance -ClassName {className}).{property}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(8000);
            return string.IsNullOrWhiteSpace(output) ? string.Empty : output.Split('\n')[0].Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> QueryWindowsDiskSerials()
    {
        try
        {
            // Fixed/internal drives only — USB sticks must not change device identity.
            const string script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                "Get-CimInstance Win32_DiskDrive | " +
                "Where-Object { " +
                "  $_.SerialNumber -and " +
                "  $_.InterfaceType -notmatch 'USB|1394|SD' -and " +
                "  $_.MediaType -notmatch 'Removable|External' " +
                "} | " +
                "ForEach-Object { $_.SerialNumber.Trim() } | " +
                "Sort-Object -Unique";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command " + script,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitizeHardware)
                .Where(s => s.Length > 0 && !IsWorthlessSerial(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Physical NIC MACs, including adapters that are currently down.
    /// Sorted for stability. Virtual/hypervisor NICs excluded.
    /// </summary>
    private static IReadOnlyList<string> GetPhysicalMacs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsPhysicalNic)
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b is { Length: >= 6 } && b.Any(x => x != 0))
                .Select(b => Convert.ToHexString(b).ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPhysicalNic(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        var name = (n.Name + " " + n.Description).ToUpperInvariant();
        if (name.Contains("VIRTUAL", StringComparison.Ordinal)
            || name.Contains("HYPER-V", StringComparison.Ordinal)
            || name.Contains("VMWARE", StringComparison.Ordinal)
            || name.Contains("VIRTUALBOX", StringComparison.Ordinal)
            || name.Contains("LOOPBACK", StringComparison.Ordinal)
            || name.Contains("BLUETOOTH", StringComparison.Ordinal)
            || name.Contains("VPN", StringComparison.Ordinal)
            || name.Contains("TAP-", StringComparison.Ordinal)
            || name.Contains("TUNNEL", StringComparison.Ordinal))
        {
            return false;
        }

        var mac = n.GetPhysicalAddress().GetAddressBytes();
        if (mac is not { Length: >= 6 } || !mac.Any(x => x != 0))
        {
            return false;
        }

        return n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Wman
            or NetworkInterfaceType.Wwanpp
            or NetworkInterfaceType.Wwanpp2
            // Some OEM NICs report Unknown but still expose a burned-in MAC.
            or NetworkInterfaceType.Unknown;
    }

    private static string? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeHardware(string? value)
    {
        var v = NormalizePart(value ?? string.Empty);
        if (v.Length == 0)
        {
            return string.Empty;
        }

        if (v is "NONE" or "NULL" or "N/A" or "NA" or "DEFAULT STRING"
            or "TO BE FILLED BY O.E.M." or "TO BE FILLED BY OEM"
            or "SYSTEM SERIAL NUMBER" or "BASE BOARD SERIAL NUMBER")
        {
            return string.Empty;
        }

        return v;
    }

    private static bool IsPlaceholderUuid(string uuid)
    {
        var u = NormalizePart(uuid).Replace("-", "", StringComparison.Ordinal);
        if (u.Length == 0)
        {
            return true;
        }

        if (u.Trim('0').Length == 0 || u.Trim('F').Length == 0)
        {
            return true;
        }

        // Common factory-default SMBIOS UUID used on many different boards.
        return NormalizePart(uuid) == "03000200-0400-0500-0006-000700080009";
    }

    private static bool IsWorthlessSerial(string serial)
    {
        var s = serial.Replace(".", "", StringComparison.Ordinal);
        return s.Trim('0').Length == 0 || s.Trim('F').Length == 0;
    }

    public static string HashNormalized(params string[] parts)
    {
        var normalized = string.Join('|', parts.Select(NormalizePart));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizePart(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return Regex.Replace(trimmed, @"\s+", " ");
    }
}
