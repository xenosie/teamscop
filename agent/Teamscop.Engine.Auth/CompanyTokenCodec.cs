using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Teamscop.Engine.Auth;

/// <summary>
/// Offline company token: TS1. + base64url(nonce || ciphertext || tag) using AES-256-GCM.
/// </summary>
public sealed class CompanyTokenCodec
{
    public const string Prefix = "TS1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly byte[] _key;

    public CompanyTokenCodec(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Company token key must be 32 bytes.", nameof(key));
        }

        _key = key;
    }

    public static CompanyTokenCodec FromBase64Key(string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        return new CompanyTokenCodec(key);
    }

    public string Encrypt(CompanyTokenPayload payload)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + ciphertext.Length, TagSize);

        return Prefix + Base64UrlEncode(packed);
    }

    public bool TryDecrypt(string token, out CompanyTokenPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var packed = Base64UrlDecode(token[Prefix.Length..]);
            if (packed.Length < NonceSize + TagSize + 1)
            {
                return false;
            }

            var nonce = packed.AsSpan(0, NonceSize);
            var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
            var ciphertext = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            payload = JsonSerializer.Deserialize<CompanyTokenPayload>(plaintext, JsonOptions);
            return payload is not null && payload.Version == 1 && payload.CompanyId != Guid.Empty && payload.Jti != Guid.Empty;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public CompanyTokenPayload Decrypt(string token)
    {
        if (!TryDecrypt(token, out var payload) || payload is null)
        {
            throw new InvalidOperationException("Invalid or corrupted company token.");
        }

        return payload;
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(padded);
    }
}
