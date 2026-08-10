using System.Security.Cryptography;
using System.Text;

namespace Teamscop.Engine.Lifecycle;

/// <summary>RFC 6238 TOTP (SHA1, 30s, 6 digits) for staff USB approve + uninstall.</summary>
public static class TotpGenerator
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const string PurposeUsb = "usb";
    public const string PurposeUninstall = "uninstall";

    public static string GenerateSecret(int bytes = 20)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(raw);
    }

    public static string ComputeCode(string base32Secret, DateTimeOffset? utcNow = null)
    {
        var key = Base32Decode(base32Secret);
        var timestep = GetTimeStep(utcNow ?? DateTimeOffset.UtcNow);
        return ComputeHotp(key, timestep);
    }

    public static bool VerifyCode(string base32Secret, string code, int window = 1, DateTimeOffset? utcNow = null)
        => VerifyCode(base32Secret, code, window, utcNow, out _);

    public static bool VerifyCode(
        string base32Secret,
        string code,
        int window,
        DateTimeOffset? utcNow,
        out long matchedStep)
    {
        matchedStep = -1;
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits || !code.All(char.IsDigit))
        {
            return false;
        }

        var key = Base32Decode(base32Secret);
        var nowStep = GetTimeStep(utcNow ?? DateTimeOffset.UtcNow);
        for (var offset = -window; offset <= window; offset++)
        {
            var step = nowStep + offset;
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeHotp(key, step)),
                    Encoding.ASCII.GetBytes(code)))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §6 — a machine's approval secret for a purpose, derived deterministically from the device
    /// key. There is no random secret, no storage and no enrolment: the server (from
    /// <c>users.DeviceKey</c>) and the agent (from its own device key) both call THIS one function
    /// and get the same Base32 secret, so USB and uninstall codes verify offline (§6.1, §6.4).
    ///
    /// <paramref name="purpose"/> (<see cref="PurposeUsb"/> / <see cref="PurposeUninstall"/>) is the
    /// HKDF <c>info</c>, keeping the two an independent code stream — a USB code can never open an
    /// uninstall. The device key is normalized trim+lowercase INSIDE this function so both sides
    /// apply one identical rule; a byte difference here and codes silently stop matching.
    ///
    /// §6.5 — the derivation INPUT is assembled on ONE line (<c>ikm</c>). It is the device key alone
    /// today (§6.2, an accepted weakness); adding the company key to close it is a one-line edit here
    /// and nowhere else.
    /// </summary>
    public static string DeriveMachineSecret(string deviceKey, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        var ikm = Encoding.UTF8.GetBytes(deviceKey.Trim().ToLowerInvariant()); // §6.5 — the one input line
        var salt = Encoding.UTF8.GetBytes("teamscop-approval-v1");             // new salt: IKM is a device key, not a random root
        var info = Encoding.UTF8.GetBytes(purpose);
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 20, salt, info);
        return Base32Encode(derived);
    }

    public static long GetTimeStep(DateTimeOffset utcNow)
        => utcNow.ToUnixTimeSeconds() / PeriodSeconds;

    public static string BuildOtpAuthUri(string base32Secret, string issuer, string accountName)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    private static string ComputeHotp(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length + 4) / 5 * 8);
        var bitBuffer = 0;
        var bitCount = 0;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                output.Append(alphabet[(bitBuffer >> (bitCount - 5)) & 0x1F]);
                bitCount -= 5;
            }
        }

        if (bitCount > 0)
        {
            output.Append(alphabet[(bitBuffer << (5 - bitCount)) & 0x1F]);
        }

        return output.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Trim().Replace("=", "", StringComparison.Ordinal).ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        var bitBuffer = 0;
        var bitCount = 0;
        foreach (var c in clean)
        {
            var val = alphabet.IndexOf(c);
            if (val < 0)
            {
                throw new FormatException("Invalid base32 secret.");
            }

            bitBuffer = (bitBuffer << 5) | val;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bytes.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xFF));
                bitCount -= 8;
            }
        }

        return bytes.ToArray();
    }
}
