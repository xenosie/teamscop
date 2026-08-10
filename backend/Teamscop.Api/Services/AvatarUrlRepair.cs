using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;

namespace Teamscop.Api.Services;

/// <summary>
/// Re-homes stored <c>AvatarUrl</c> values onto the configured public base path.
///
/// B12 moved avatars from an unauthenticated static alias (<c>/media/avatars/…</c>) to an
/// authenticated API route (<c>/api/media/avatars/…</c>). Rows written before that change kept the
/// old prefix, and the old prefix does not 404 — behind nginx it falls through to the catch-all
/// banner and answers <c>200 text/plain</c>, so the desktop receives a successful non-image
/// response and shows a placeholder forever with no error anywhere. The image file itself is
/// intact on disk; only the prefix is stale.
///
/// Runs itself at start-up rather than as a hand-run SQL script: the owner should never have to
/// open psql to make faces appear. Matching is on the file name, exactly as
/// <see cref="AvatarAccess"/> resolves owners, so a repaired row and an original row are
/// indistinguishable afterwards.
///
/// Idempotent by construction — a row already on the configured prefix is excluded by the query,
/// so every run after the first repairs nothing. Re-pointing
/// <see cref="StorageOptions.PublicAvatarBasePath"/> later re-homes the rows again on the next
/// start, which is the same operation, not a special case.
/// </summary>
public interface IAvatarUrlRepair
{
    /// <summary>Repairs every stale row and returns how many were rewritten.</summary>
    Task<int> RunOnceAsync(CancellationToken ct);
}

public sealed class AvatarUrlRepair(
    AppDbContext db,
    IOptions<StorageOptions> options,
    ILogger<AvatarUrlRepair> logger) : IAvatarUrlRepair
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var basePath = options.Value.PublicAvatarBasePath?.TrimEnd('/') ?? "";
        if (basePath.Length == 0 || !basePath.StartsWith('/'))
        {
            // Nothing sane to re-home onto. Leave the data alone rather than guess.
            return 0;
        }

        var prefix = basePath + "/";
        var repaired = 0;

        var users = await db.Users
            .Where(u => u.AvatarUrl != null && !u.AvatarUrl.StartsWith(prefix))
            .ToListAsync(ct);
        foreach (var user in users)
        {
            if (Rehome(user.AvatarUrl!, prefix) is not { } fixedUrl)
            {
                continue;
            }

            logger.LogInformation("Repairing avatar path for user {UserId}: {Old} → {New}",
                user.Id, user.AvatarUrl, fixedUrl);
            user.AvatarUrl = fixedUrl;
            repaired++;
        }

        var companies = await db.Companies
            .Where(c => c.AvatarUrl != null && !c.AvatarUrl.StartsWith(prefix))
            .ToListAsync(ct);
        foreach (var company in companies)
        {
            if (Rehome(company.AvatarUrl!, prefix) is not { } fixedUrl)
            {
                continue;
            }

            logger.LogInformation("Repairing avatar path for company {CompanyId}: {Old} → {New}",
                company.Id, company.AvatarUrl, fixedUrl);
            company.AvatarUrl = fixedUrl;
            repaired++;
        }

        if (repaired > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return repaired;
    }

    /// <summary>
    /// The stored value rewritten onto <paramref name="prefix"/>, or null to leave it untouched.
    ///
    /// Three guards, each of which exists to make the sweep unable to corrupt a value it does not
    /// understand: the path must be site-relative (an absolute URL belongs to somewhere this
    /// service does not serve), its directory must actually be an <c>avatars</c> directory, and the
    /// file name must be a bare name — the same shape <see cref="AvatarStorage"/> writes. Anything
    /// else is left exactly as found.
    /// </summary>
    private static string? Rehome(string stored, string prefix)
    {
        if (!stored.StartsWith('/'))
        {
            return null;
        }

        var slash = stored.LastIndexOf('/');
        var directory = stored[..slash];
        var fileName = stored[(slash + 1)..];

        if (!directory.EndsWith("/avatars", StringComparison.Ordinal) || fileName.Length == 0)
        {
            return null;
        }

        foreach (var c in fileName)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return null;
            }
        }

        var rehomed = prefix + fileName;
        return string.Equals(rehomed, stored, StringComparison.Ordinal) ? null : rehomed;
    }
}

/// <summary>
/// Runs <see cref="IAvatarUrlRepair"/> once, at start-up, after the migration step in
/// <c>Program</c> has completed. Failure is logged and swallowed: a cosmetic data repair must
/// never be the reason the API refuses to come up.
/// </summary>
public sealed class AvatarUrlRepairHostedService(
    IServiceScopeFactory scopes,
    ILogger<AvatarUrlRepairHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repair = scope.ServiceProvider.GetRequiredService<IAvatarUrlRepair>();
            var repaired = await repair.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (repaired > 0)
            {
                logger.LogInformation("Avatar path repair rewrote {Count} row(s)", repaired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Avatar path repair failed; stale avatar rows remain");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
