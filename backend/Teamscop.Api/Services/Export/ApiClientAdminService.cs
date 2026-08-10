using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;

namespace Teamscop.Api.Services.Export;

public interface IApiClientAdminService
{
    /// <summary>Replaces the client's IP allowlist. Returns the normalised list that was stored.</summary>
    Task<IReadOnlyList<string>> SetAllowlistAsync(Guid clientId, IReadOnlyList<string> ips, CancellationToken ct);
}

public sealed class ApiClientAdminService(AppDbContext db) : IApiClientAdminService
{
    public async Task<IReadOnlyList<string>> SetAllowlistAsync(
        Guid clientId, IReadOnlyList<string> ips, CancellationToken ct)
    {
        var client = await db.ApiClients.FirstAsync(c => c.Id == clientId, ct);

        // Normalised on the way in — IPv4-mapped IPv6 and plain IPv4 are folded to one form, so a
        // stored entry matches regardless of how the socket presents the address at request time.
        var normalised = ips
            .Select(i => IPAddress.TryParse(i?.Trim(), out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Select(ip => (ip!.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        client.AllowedIps = string.Join(",", normalised);
        await db.SaveChangesAsync(ct);
        return normalised;
    }
}

/// <summary>
/// Generates the one export credential. Called from the CLI entry point below, never from a route:
/// there is deliberately no HTTP path that can mint a key.
/// </summary>
public static class ApiCredentialFactory
{
    public const string KeyPrefix = "tsk-";
    public const string SecretPrefix = "tss-";

    /// <summary>
    /// 32 random bytes each, base64url. The key id is public and only needs to be unguessable
    /// enough to avoid collisions; the secret is the actual authenticator and is stored hashed.
    /// </summary>
    public static (string KeyId, string Secret) Generate()
        => (KeyPrefix + Token(), SecretPrefix + Token());

    private static string Token()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "").Replace("/", "").Replace("=", "");
}
