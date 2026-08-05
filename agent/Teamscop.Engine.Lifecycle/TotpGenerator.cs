using System.Security.Cryptography;
using System.Text;

namespace Teamscop.Engine.Lifecycle;

/// <summary>RFC 6238 TOTP (SHA1, 30s, 6 digits) for staff USB approve + uninstall.</summary>
public static class TotpGenerator
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

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
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits || !code.All(char.IsDigit))
        {
            return false;
        }

        var key = Base32Decode(base32Secret);
        var nowStep = GetTimeStep(utcNow ?? DateTimeOffset.UtcNow);
        for (var offset = -window; offset <= window; offset++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeHotp(key, nowStep + offset)),
                    Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildOtpAuthUri(string base32Secret, string issuer, string accountName)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    private static long GetTimeStep(DateTimeOffset utcNow)
        => utcNow.ToUnixTimeSeconds() / PeriodSeconds;

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
