using System.Security.Cryptography;
using System.Text;

namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Windows DPAPI at <see cref="DataProtectionScope.LocalMachine"/> — the only scope a
/// LocalSystem service and an elevated uninstaller can both read.
///
/// Every method returns null off Windows rather than throwing: the agent only ever runs
/// unprotected on a developer box, and callers must treat null as "no protected storage here",
/// not as a failure. Machine scope means any process on the PC can unprotect; the file ACL is
/// what keeps the employee out, and §10.3 accepts that a local admin can defeat it.
/// </summary>
public static class Dpapi
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static byte[]? Protect(byte[] plain, byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ProtectedData.Protect(plain, entropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static byte[]? Unprotect(byte[] cipher, byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string? ProtectToBase64(string plaintext, byte[]? entropy = null)
    {
        var bytes = Protect(Encoding.UTF8.GetBytes(plaintext), entropy);
        return bytes is null ? null : Convert.ToBase64String(bytes);
    }

    public static string? UnprotectFromBase64(string protectedBase64, byte[]? entropy = null)
    {
        try
        {
            var bytes = Unprotect(Convert.FromBase64String(protectedBase64), entropy);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Binds protected data to this machine's device key, so a stolen file cannot be unprotected
    /// on another PC that happens to share the DPAPI machine key (imaged fleets).
    /// </summary>
    public static byte[] EntropyFromDeviceKey(string? deviceKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes("teamscop-approval:" + (deviceKey ?? string.Empty)));
}
