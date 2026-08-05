using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Teamscop.Engine.Auth;

/// <summary>
/// Builds a hardware-bound device key from SMBIOS UUID + board/BIOS serials.
/// On non-Windows platforms, falls back to DMI / machine-id for local testing.
/// </summary>
public sealed class DeviceKeyProvider : IDeviceKeyProvider
{
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

        return ComputeLinuxFallbackKey();
    }

    private static string ComputeWindowsDeviceKey()
    {
        var uuid = QueryWindowsCim("Win32_ComputerSystemProduct", "UUID");
        var board = QueryWindowsCim("Win32_BaseBoard", "SerialNumber");
        var bios = QueryWindowsCim("Win32_BIOS", "SerialNumber");
        return HashNormalized(uuid, board, bios);
    }

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
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? string.Empty : output.Split('\n')[0].Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeLinuxFallbackKey()
    {
        var machineId = File.Exists("/etc/machine-id")
            ? File.ReadAllText("/etc/machine-id").Trim()
            : Environment.MachineName;
        var productUuidPath = "/sys/class/dmi/id/product_uuid";
        var boardSerialPath = "/sys/class/dmi/id/board_serial";
        var biosSerialPath = "/sys/class/dmi/id/bios_serial";

        var uuid = TryRead(productUuidPath) ?? machineId;
        var board = TryRead(boardSerialPath) ?? string.Empty;
        var bios = TryRead(biosSerialPath) ?? string.Empty;
        return HashNormalized(uuid, board, bios);
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
