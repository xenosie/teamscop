using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services.Access;

public interface IAccessPolicy
{
    /// <summary>Loads the principal, throwing if the token names a user that no longer exists.</summary>
    Task<ViewerContext> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>Loads the principal, or null when there is no such user.</summary>
    Task<ViewerContext?> FindAsync(Guid userId, CancellationToken ct);

    Task<EffectiveAuthoritiesDto> GetEffectiveAsync(Guid userId, CancellationToken ct);
    Task<bool> HasAsync(Guid userId, string packageId, CancellationToken ct);
    Task EnsureHasAsync(Guid userId, string packageId, CancellationToken ct);
    Task<bool> CanViewStaffAsync(Guid viewerId, Guid staffUserId, CancellationToken ct);
    Task EnsureCanViewStaffAsync(Guid viewerId, Guid staffUserId, CancellationToken ct);

    /// <summary>Drops the per-request memo after a grant or org-structure change.</summary>
    void Invalidate();
}

/// <summary>
/// Loads a <see cref="ViewerContext"/> once per user per request and answers every authorization
/// question from it. Scoped, never singleton: a grant change must take effect on the next request
/// (§4.3), not on the next process restart.
/// </summary>
public sealed class AccessPolicy(AppDbContext db) : IAccessPolicy
{
    private readonly Dictionary<Guid, ViewerContext?> _memo = [];

    public async Task<ViewerContext> GetAsync(Guid userId, CancellationToken ct)
        => await FindAsync(userId, ct) ?? throw new SessionInvalidException("User not found.");

    public async Task<ViewerContext?> FindAsync(Guid userId, CancellationToken ct)
    {
        if (_memo.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var loaded = await LoadAsync(userId, ct);
        _memo[userId] = loaded;
        return loaded;
    }

    public void Invalidate() => _memo.Clear();

    public async Task<EffectiveAuthoritiesDto> GetEffectiveAsync(Guid userId, CancellationToken ct)
        => (await GetAsync(userId, ct)).ToEffectiveAuthorities();

    public async Task<bool> HasAsync(Guid userId, string packageId, CancellationToken ct)
        => (await FindAsync(userId, ct))?.Has(packageId) ?? false;

    public async Task EnsureHasAsync(Guid userId, string packageId, CancellationToken ct)
    {
        if (!await HasAsync(userId, packageId, ct))
        {
            throw new UnauthorizedAccessException($"Missing authority package: {packageId}");
        }
    }

    public async Task<bool> CanViewStaffAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (viewerId == staffUserId)
        {
            return false;
        }

        var viewer = await FindAsync(viewerId, ct);
        var target = await FindAsync(staffUserId, ct);
        return viewer is not null && target is not null && viewer.CanView(target);
    }

    public async Task EnsureCanViewStaffAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
        }
    }

    private async Task<ViewerContext?> LoadAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.CompanyId,
                u.Role,
                u.IsPoliceman,
                u.AuthorityVersion,
                Packages = u.AuthorityGrants.Select(g => g.PackageId).ToList()
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        Guid? ledTeamId = null;
        var ledMembers = new HashSet<Guid>();

        // Only staff lead teams (RequireCompanyStaffAsync enforces it), and an admin already
        // holds everything — so the admin path never pays for this query.
        if (row.Role != UserRole.Admin)
        {
            var led = await db.Teams.AsNoTracking()
                .Where(t => t.LeaderUserId == userId)
                .Select(t => new { t.Id, Members = t.Members.Select(m => m.StaffUserId).ToList() })
                .ToListAsync(ct);
            foreach (var team in led)
            {
                ledTeamId ??= team.Id;
                foreach (var member in team.Members)
                {
                    ledMembers.Add(member);
                }
            }
        }

        return new ViewerContext(
            row.Id,
            row.CompanyId,
            row.Role,
            row.IsPoliceman,
            row.AuthorityVersion,
            new HashSet<string>(row.Packages, StringComparer.Ordinal),
            ledTeamId,
            ledMembers);
    }
}
