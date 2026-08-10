using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Services.Access;

public interface IPolicemanAdminService
{
    Task<IReadOnlyList<AuthorityPackageInfo>> ListPackageCatalogAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<PolicemanDto>> ListPolicemenAsync(Guid adminUserId, CancellationToken ct);
    Task<PolicemanDto> UpsertPolicemanAsync(Guid adminUserId, Guid staffUserId, IReadOnlyList<string> packages, CancellationToken ct);
    Task RevokePolicemanAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
}

/// <summary>The admin-only write side of authority packages. Reads live in <see cref="AccessPolicy"/>.</summary>
public sealed class PolicemanAdminService(
    AppDbContext db,
    IAccessPolicy access,
    IAvatarAccess avatars,
    IHubContext<ConfigHub> hub,
    IAuditLog audit,
    ILogger<PolicemanAdminService> logger) : IPolicemanAdminService
{
    public async Task<IReadOnlyList<AuthorityPackageInfo>> ListPackageCatalogAsync(Guid userId, CancellationToken ct)
    {
        var viewer = await access.GetAsync(userId, ct);
        if (!viewer.IsAdmin && !viewer.CanManageTeams && !viewer.CanGenerateTotp)
        {
            throw new UnauthorizedAccessException("Not allowed to list authority packages.");
        }

        return AuthorityPackageIds.All
            .Select(id => new AuthorityPackageInfo { Id = id, Label = AuthorityPackageIds.Labels[id] })
            .ToList();
    }

    public async Task<IReadOnlyList<PolicemanDto>> ListPolicemenAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        var cops = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == admin.CompanyId && u.Role == UserRole.Staff && u.IsPoliceman)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
        var ids = cops.Select(c => c.Id).ToList();
        var grants = await db.PolicemanAuthorityGrants.AsNoTracking()
            .Where(g => ids.Contains(g.StaffUserId))
            .ToListAsync(ct);
        var byUser = grants.GroupBy(g => g.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PackageId).OrderBy(p => p).ToList());

        return cops.Select(c => new PolicemanDto
        {
            StaffUserId = c.Id,
            Username = c.Username,
            IsPoliceman = true,
            Packages = byUser.GetValueOrDefault(c.Id) ?? [],
            UpdatedAt = c.PolicemanUpdatedAt
        }).ToList();
    }

    public async Task<PolicemanDto> UpsertPolicemanAsync(
        Guid adminUserId,
        Guid staffUserId,
        IReadOnlyList<string> packages,
        CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        var staff = await RequireCompanyStaffAsync(admin.CompanyId, staffUserId, ct);

        var normalized = (packages ?? [])
            .Select(AuthorityPackageIds.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        staff.IsPoliceman = true;
        staff.PolicemanUpdatedAt = DateTimeOffset.UtcNow;
        staff.AuthorityVersion += 1;

        var existing = await db.PolicemanAuthorityGrants.Where(g => g.StaffUserId == staffUserId).ToListAsync(ct);
        db.PolicemanAuthorityGrants.RemoveRange(existing);
        foreach (var packageId in normalized)
        {
            db.PolicemanAuthorityGrants.Add(new PolicemanAuthorityGrant
            {
                StaffUserId = staffUserId,
                PackageId = packageId,
                GrantedAt = DateTimeOffset.UtcNow,
                GrantedByUserId = admin.UserId
            });
        }

        await db.SaveChangesAsync(ct);
        access.Invalidate();

        audit.Record(AuditActions.PoliceGranted, admin.UserId, admin.CompanyId,
            new { staffUserId, packages = normalized });
        logger.LogInformation("Policeman packages set for {StaffUserId} by {AdminUserId}: {Packages}",
            staffUserId, admin.UserId, string.Join(",", normalized));

        await BroadcastAsync(admin, staffUserId, ct);

        // Return granted packages only — never merge inherent team-leader views into the police grant list.
        return new PolicemanDto
        {
            StaffUserId = staff.Id,
            Username = staff.Username,
            IsPoliceman = true,
            Packages = normalized,
            UpdatedAt = staff.PolicemanUpdatedAt
        };
    }

    public async Task RevokePolicemanAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        var staff = await RequireCompanyStaffAsync(admin.CompanyId, staffUserId, ct);

        var grants = await db.PolicemanAuthorityGrants.Where(g => g.StaffUserId == staffUserId).ToListAsync(ct);
        db.PolicemanAuthorityGrants.RemoveRange(grants);
        staff.IsPoliceman = false;
        staff.PolicemanUpdatedAt = DateTimeOffset.UtcNow;
        staff.AuthorityVersion += 1;
        await db.SaveChangesAsync(ct);
        access.Invalidate();

        audit.Record(AuditActions.PoliceRevoked, admin.UserId, admin.CompanyId, new { staffUserId });
        logger.LogInformation("Policeman revoked for {StaffUserId} by {AdminUserId}", staffUserId, admin.UserId);

        await BroadcastAsync(admin, staffUserId, ct);
    }

    private async Task BroadcastAsync(ViewerContext admin, Guid staffUserId, CancellationToken ct)
    {
        // A granted or revoked view package changes who this staff member may look at, avatars
        // included — drop the cached reach before anyone acts on the push (B12).
        avatars.InvalidateCompany(admin.CompanyId);

        var dto = await access.GetEffectiveAsync(staffUserId, ct);
        await hub.Clients.Group(ConfigHub.StaffGroup(staffUserId))
            .SendAsync("AuthoritiesUpdated", dto, ct);
        // Policeman roster is admin-only over REST — keep the push equally restricted.
        await hub.Clients.Group(ConfigHub.CompanyManagementGroup(admin.CompanyId))
            .SendAsync("PolicemenUpdated", await ListPolicemenAsync(admin.UserId, ct), ct);
    }

    private async Task<ViewerContext> RequireAdminAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await access.GetAsync(adminUserId, ct);
        if (!admin.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only admins can manage policemen.");
        }

        return admin;
    }

    private async Task<UserAccount> RequireCompanyStaffAsync(Guid companyId, Guid staffUserId, CancellationToken ct)
    {
        var staff = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");
        if (staff.CompanyId != companyId || staff.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("User is not staff in your company.");
        }

        return staff;
    }
}
