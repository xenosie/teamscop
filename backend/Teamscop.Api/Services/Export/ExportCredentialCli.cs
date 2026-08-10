using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;

namespace Teamscop.Api.Services.Export;

/// <summary>
/// Mints the single export credential from a shell on the server.
///
/// Not an HTTP route, and deliberately so: a route that can create API credentials is a route that
/// can be reached, and this product has exactly one external consumer. Issuing a key should require
/// access to the machine itself.
/// </summary>
public static class ExportCredentialCli
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var i = Array.IndexOf(args, "--issue-api-key");
        var companyName = i >= 0 && args.Length > i + 1 ? args[i + 1] : null;
        var label = i >= 0 && args.Length > i + 2 ? args[i + 2] : "export";

        if (string.IsNullOrWhiteSpace(companyName))
        {
            Console.Error.WriteLine("usage: --issue-api-key \"<company name>\" [label]");
            return 2;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var company = await db.Companies
            .FirstOrDefaultAsync(c => c.Name == companyName);
        if (company is null)
        {
            Console.Error.WriteLine($"No company named \"{companyName}\".");
            var names = await db.Companies.Select(c => c.Name).ToListAsync();
            Console.Error.WriteLine("Known companies: " + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
            return 3;
        }

        var (keyId, secret) = ApiCredentialFactory.Generate();

        // One credential per company: re-issuing replaces the old one rather than accumulating keys
        // nobody is tracking. The old key stops working the moment this returns.
        var existing = await db.ApiClients.Where(c => c.CompanyId == company.Id).ToListAsync();
        var allowlist = existing.FirstOrDefault()?.AllowedIps;
        if (existing.Count > 0)
        {
            db.ApiClients.RemoveRange(existing);
            foreach (var old in existing)
            {
                ApiClientAuthenticator.Forget(old.KeyId);
            }
        }

        db.ApiClients.Add(new ApiClient
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Name = label,
            KeyId = keyId,
            SecretHash = hasher.Hash(secret),
            // The allowlist survives a re-issue: rotating a secret should not also lock the
            // consumer out of the endpoint that would let them fix their access.
            AllowedIps = allowlist,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        Console.WriteLine();
        Console.WriteLine("Teamscop export credential issued");
        Console.WriteLine("=================================");
        Console.WriteLine($"company     : {company.Name}");
        Console.WriteLine($"label       : {label}");
        Console.WriteLine($"X-Api-Key   : {keyId}");
        Console.WriteLine($"X-Api-Secret: {secret}");
        Console.WriteLine();
        Console.WriteLine("The secret is stored only as a hash and cannot be shown again.");
        Console.WriteLine("Next: POST /api/v2/ip-allowlist with the consumer's source IP.");
        Console.WriteLine();
        return 0;
    }
}
