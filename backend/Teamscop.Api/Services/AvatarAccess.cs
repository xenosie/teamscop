using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Api.Services.Access;

namespace Teamscop.Api.Services;

/// <summary>An avatar the caller is allowed to read, resolved to a file on disk.</summary>
public sealed record AvatarFile(string Path, string ContentType);

public interface IAvatarAccess
{
    /// <summary>
    /// The avatar behind <paramref name="fileName"/> if <paramref name="viewerId"/> may see it,
    /// otherwise null — indistinguishable, deliberately, from a file that does not exist.
    /// </summary>
    Task<AvatarFile?> ResolveAsync(Guid viewerId, string fileName, CancellationToken ct);

    /// <summary>
    /// Drops the cached reach of every viewer in a company. Called wherever leadership or an
    /// authority grant changes, so a demotion narrows what its holder can fetch immediately
    /// rather than at the end of the cache window.
    /// </summary>
    void InvalidateCompany(Guid companyId);
}

/// <summary>
/// B12 — avatars used to be static files on a public path, so a leaked URL was readable by anyone
/// with no token at all. They are staff data and are now scoped like staff data: a viewer may see
/// the avatar of anyone they may view (§4.2–4.4), plus their own and their company's.
///
/// The check has to be cheap, because the staff directory fetches one image per row: caching only
/// the *decision inputs* — the file's owner and the viewer's reach — keeps a 50-row screen at two
/// database round trips per new avatar and none at all thereafter, instead of one authorization
/// load per image. The rule itself is never cached and never re-implemented; it is
/// <see cref="ViewerContext.CanViewStaff"/>, the same function the tracking endpoints use.
/// </summary>
public sealed class AvatarAccess(
    IServiceScopeFactory scopes,
    IOptions<StorageOptions> options,
    IWebHostEnvironment env) : IAvatarAccess
{
    /// <summary>
    /// A stored avatar is never rewritten (upload happens once, at signup), so ownership is
    /// effectively immutable and can be held for a long time.
    /// </summary>
    private static readonly TimeSpan OwnerTtl = TimeSpan.FromMinutes(30);

    /// <summary>An unknown name is cheap to re-ask for, and must not pin memory.</summary>
    private static readonly TimeSpan UnknownTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Backstop only. Every path that changes a viewer's reach calls
    /// <see cref="InvalidateCompany"/>, so this bounds nothing but a missed call.
    /// </summary>
    private static readonly TimeSpan ViewerTtl = TimeSpan.FromSeconds(60);

    private const int MaxEntries = 5_000;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private readonly ConcurrentDictionary<string, Cached<AvatarOwner?>> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Cached<ViewerContext>> _viewers = new();
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    /// <summary>
    /// Absolute and without a trailing separator — the containment check below compares against
    /// <c>_root + separator</c>, which a configured "…/avatars/" would otherwise never match.
    /// </summary>
    private readonly string _root = Path.TrimEndingDirectorySeparator(
        Path.IsPathRooted(options.Value.AvatarRoot)
            ? Path.GetFullPath(options.Value.AvatarRoot)
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.AvatarRoot)));

    public async Task<AvatarFile?> ResolveAsync(Guid viewerId, string fileName, CancellationToken ct)
    {
        if (!TryResolveFile(fileName, out var path, out var contentType))
        {
            return null;
        }

        var owner = await OwnerAsync(fileName, ct).ConfigureAwait(false);
        if (owner is null)
        {
            return null;
        }

        var viewer = await ViewerAsync(viewerId, ct).ConfigureAwait(false);
        if (viewer is null || !Allows(viewer, owner))
        {
            return null;
        }

        return File.Exists(path) ? new AvatarFile(path, contentType) : null;
    }

    public void InvalidateCompany(Guid companyId)
        => _generations.AddOrUpdate(companyId, 1, (_, version) => version + 1);

    private static bool Allows(ViewerContext viewer, AvatarOwner owner)
    {
        // The company badge: every employee's own workspace and sticker show it.
        if (owner.CompanyId is Guid companyId && companyId == viewer.CompanyId)
        {
            return true;
        }

        if (owner.UserId is not Guid ownerId)
        {
            return false;
        }

        // Your own face. CanViewStaff refuses self — that is the self-monitoring ban (§4.5),
        // which is about tracking data, not about your own portrait in your own title bar.
        return ownerId == viewer.UserId
               || viewer.CanViewStaff(ownerId, owner.CompanyIdOfUser, owner.UserRole);
    }

    /// <summary>
    /// Validates the name and maps it onto a file inside the avatar root. Rejects anything that
    /// is not a plain file name with a known image extension, then confirms the composed path did
    /// not escape the root anyway.
    /// </summary>
    private bool TryResolveFile(string fileName, out string path, out string contentType)
    {
        path = string.Empty;
        contentType = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 128
            || fileName.Contains("..", StringComparison.Ordinal)
            || Path.GetFileName(fileName) != fileName)
        {
            return false;
        }

        if (!ContentTypes.TryGetValue(Path.GetExtension(fileName), out var resolved))
        {
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(_root, fileName));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        path = full;
        contentType = resolved;
        return true;
    }

    /// <summary>
    /// Matched on the file name rather than the whole stored URL, so the public base path stays a
    /// configuration detail and rows written under an older one still resolve.
    /// </summary>
    private async Task<AvatarOwner?> OwnerAsync(string fileName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_owners.TryGetValue(fileName, out var cached) && cached.Expires > now)
        {
            return cached.Value;
        }

        var suffix = "/" + fileName;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.AvatarUrl != null && u.AvatarUrl.EndsWith(suffix))
            .Select(u => new { u.Id, u.CompanyId, u.Role })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var companyId = await db.Companies.AsNoTracking()
            .Where(c => c.AvatarUrl != null && c.AvatarUrl.EndsWith(suffix))
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var owner = user is null && companyId is null
            ? null
            : new AvatarOwner(
                user?.Id,
                user?.CompanyId ?? Guid.Empty,
                user?.Role ?? UserRole.Staff,
                companyId);

        Store(_owners, fileName, new Cached<AvatarOwner?>(
            owner, now + (owner is null ? UnknownTtl : OwnerTtl), Generation: 0));
        return owner;
    }

    private async Task<ViewerContext?> ViewerAsync(Guid viewerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_viewers.TryGetValue(viewerId, out var cached)
            && cached.Expires > now
            && cached.Generation == Generation(cached.Value.CompanyId))
        {
            return cached.Value;
        }

        using var scope = scopes.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessPolicy>();
        var viewer = await access.FindAsync(viewerId, ct).ConfigureAwait(false);
        if (viewer is null)
        {
            _viewers.TryRemove(viewerId, out _);
            return null;
        }

        Store(_viewers, viewerId, new Cached<ViewerContext>(
            viewer, now + ViewerTtl, Generation(viewer.CompanyId)));
        return viewer;
    }

    private long Generation(Guid companyId) => _generations.TryGetValue(companyId, out var g) ? g : 0;

    /// <summary>
    /// Adds an entry, emptying the map first if it has grown past its ceiling. Crude on purpose:
    /// the working set is one entry per employee and one per avatar (§15.1), so the ceiling is a
    /// guard against a pathological caller, not a cache-eviction policy.
    /// </summary>
    private static void Store<TKey, TValue>(ConcurrentDictionary<TKey, TValue> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (map.Count >= MaxEntries)
        {
            map.Clear();
        }

        map[key] = value;
    }

    private sealed record Cached<T>(T Value, DateTimeOffset Expires, long Generation);

    /// <summary>
    /// Which principal a stored avatar belongs to. Both halves can be set at once: registering a
    /// company writes the same file to the company row and to its admin.
    /// </summary>
    private sealed record AvatarOwner(Guid? UserId, Guid CompanyIdOfUser, UserRole UserRole, Guid? CompanyId);
}
