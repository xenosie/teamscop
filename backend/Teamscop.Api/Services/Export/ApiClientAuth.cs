using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;

namespace Teamscop.Api.Services.Export;

/// <summary>The authenticated export caller, resolved once per request.</summary>
public sealed record ApiCaller(Guid ClientId, Guid CompanyId, string KeyId, string? AllowedIps);

/// <summary>Why an export request was refused. Mapped to a status code by the endpoint filter.</summary>
public enum ApiAuthFailure
{
    None,
    MissingCredentials,
    InvalidCredentials,
    Disabled,
    IpNotAllowed,
    NoAllowlistConfigured
}

public sealed record ApiAuthResult(ApiCaller? Caller, ApiAuthFailure Failure)
{
    public bool Ok => Caller is not null && Failure == ApiAuthFailure.None;
}

public interface IApiClientAuthenticator
{
    /// <summary>Validates key + secret only. Used by the allowlist-management endpoint.</summary>
    Task<ApiAuthResult> AuthenticateAsync(string? keyId, string? secret, CancellationToken ct);

    /// <summary>Validates key + secret AND that the source IP is on the client's allowlist.</summary>
    Task<ApiAuthResult> AuthenticateForDataAsync(
        string? keyId, string? secret, IPAddress? sourceIp, CancellationToken ct);
}

/// <summary>
/// Credential check for the export API.
///
/// Three properties matter here and are deliberate:
///
/// The secret is verified with the SAME Argon2 hasher the product uses for passwords, so a database
/// disclosure does not hand anyone a working credential. Because Argon2 is intentionally slow, a
/// successful verification is memoised for a short window — otherwise a consumer polling every few
/// seconds would spend most of the server's CPU on its own authentication.
///
/// A failed key lookup still performs a verification against a dummy hash. Returning early would
/// make "unknown key" measurably faster than "wrong secret", which tells an attacker when they have
/// guessed a real key id.
///
/// The IP allowlist is enforced on DATA endpoints only. The management endpoint that SETS the
/// allowlist is reachable with credentials alone — otherwise a consumer whose IP changes would be
/// permanently locked out of the only endpoint that could fix it, with no recovery path.
/// </summary>
public sealed class ApiClientAuthenticator(
    AppDbContext db,
    IPasswordHasher hasher,
    ILogger<ApiClientAuthenticator> logger) : IApiClientAuthenticator
{
    /// <summary>Long enough to matter under polling, short enough that a revoked key dies quickly.</summary>
    private static readonly TimeSpan VerifyCacheTtl = TimeSpan.FromSeconds(60);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Secret, DateTimeOffset Until)>
        VerifiedCache = new(StringComparer.Ordinal);

    /// <summary>A real Argon2 hash of a value nobody holds, for constant-ish work on unknown keys.</summary>
    private static readonly Lazy<string> DummyHash =
        new(() => new Argon2PasswordHasher().Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

    public async Task<ApiAuthResult> AuthenticateAsync(string? keyId, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secret))
        {
            return new ApiAuthResult(null, ApiAuthFailure.MissingCredentials);
        }

        var client = await db.ApiClients
            .FirstOrDefaultAsync(c => c.KeyId == keyId, ct);

        if (client is null)
        {
            // Same work as a real verification: an unknown key must not answer faster.
            hasher.Verify(secret, DummyHash.Value);
            return new ApiAuthResult(null, ApiAuthFailure.InvalidCredentials);
        }

        if (!client.Enabled)
        {
            return new ApiAuthResult(null, ApiAuthFailure.Disabled);
        }

        if (!VerifyWithCache(client.KeyId, secret, client.SecretHash))
        {
            return new ApiAuthResult(null, ApiAuthFailure.InvalidCredentials);
        }

        // Coarse: only written when the previous stamp is over a minute old, so a polling consumer
        // does not turn every read into a database write.
        var now = DateTimeOffset.UtcNow;
        if (client.LastUsedAt is null || now - client.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            client.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new ApiAuthResult(
            new ApiCaller(client.Id, client.CompanyId, client.KeyId, client.AllowedIps),
            ApiAuthFailure.None);
    }

    public async Task<ApiAuthResult> AuthenticateForDataAsync(
        string? keyId, string? secret, IPAddress? sourceIp, CancellationToken ct)
    {
        var result = await AuthenticateAsync(keyId, secret, ct);
        if (!result.Ok)
        {
            return result;
        }

        var allowed = ParseAllowlist(result.Caller!.AllowedIps);
        if (allowed.Count == 0)
        {
            // Fail CLOSED. An empty allowlist means "not configured yet", never "allow everyone" —
            // the whole point of the allowlist is that data leaves only to one known host.
            logger.LogWarning("Export client {KeyId} has no IP allowlist; data refused.", result.Caller.KeyId);
            return new ApiAuthResult(null, ApiAuthFailure.NoAllowlistConfigured);
        }

        if (sourceIp is null || !allowed.Any(ip => ip.Equals(Normalize(sourceIp))))
        {
            logger.LogWarning(
                "Export client {KeyId} refused from {Ip} (not on allowlist).",
                result.Caller.KeyId, sourceIp?.ToString() ?? "unknown");
            return new ApiAuthResult(null, ApiAuthFailure.IpNotAllowed);
        }

        return result;
    }

    /// <summary>
    /// Argon2 is deliberately expensive, so a verified (key, secret) pair is trusted briefly. The
    /// cache key is the key id and the entry stores the secret it was proved against, so presenting
    /// a DIFFERENT secret for the same key never hits the cache.
    /// </summary>
    private bool VerifyWithCache(string keyId, string secret, string hash)
    {
        var now = DateTimeOffset.UtcNow;
        if (VerifiedCache.TryGetValue(keyId, out var entry)
            && entry.Until > now
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(entry.Secret),
                System.Text.Encoding.UTF8.GetBytes(secret)))
        {
            return true;
        }

        if (!hasher.Verify(secret, hash))
        {
            return false;
        }

        VerifiedCache[keyId] = (secret, now + VerifyCacheTtl);
        return true;
    }

    /// <summary>Drops a cached verification immediately — used when a credential changes.</summary>
    public static void Forget(string keyId) => VerifiedCache.TryRemove(keyId, out _);

    public static List<IPAddress> ParseAllowlist(string? raw)
    {
        var list = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(part, out var ip))
            {
                list.Add(Normalize(ip));
            }
        }

        return list;
    }

    /// <summary>
    /// IPv4-mapped IPv6 (<c>::ffff:203.0.113.7</c>) and plain IPv4 are the same host. Kestrel behind
    /// nginx can present either, so both are folded to the v4 form before comparison — otherwise an
    /// allowlist entry would work or not depending on socket details the consumer cannot see.
    /// </summary>
    private static IPAddress Normalize(IPAddress ip)
        => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
