using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public interface IAuthorityService
{
    IReadOnlyList<AuthorityPackageInfo> ListPackageCatalog();
    Task<EffectiveAuthoritiesDto> GetEffectiveAsync(Guid userId, CancellationToken ct);
    Task<bool> HasAsync(Guid userId, string packageId, CancellationToken ct);
    Task EnsureHasAsync(Guid userId, string packageId, CancellationToken ct);
    Task<bool> CanViewCompanyStaffAsync(Guid viewerId, CancellationToken ct);
    Task<bool> CanViewStaffAsync(Guid viewerId, Guid targetStaffId, CancellationToken ct);
    Task<bool> CanViewEventTypeAsync(Guid viewerId, string eventType, CancellationToken ct);
    Task<bool> CanGenerateTotpAsync(Guid viewerId, CancellationToken ct);
    Task<bool> CanManageTeamsAsync(Guid viewerId, CancellationToken ct);
    Task<bool> IsTeamLeaderAsync(Guid userId, CancellationToken ct);
    Task<bool> LeadsTeamMemberAsync(Guid leaderUserId, Guid memberStaffId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListLedMemberIdsAsync(Guid leaderUserId, CancellationToken ct);
    Task<IReadOnlyList<PolicemanDto>> ListPolicemenAsync(Guid adminUserId, CancellationToken ct);
    Task<PolicemanDto> UpsertPolicemanAsync(Guid adminUserId, Guid staffUserId, IReadOnlyList<string> packages, CancellationToken ct);
    Task RevokePolicemanAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
}

public sealed class AuthorityService(AppDbContext db, IHubContext<ConfigHub> hub) : IAuthorityService
{
    public IReadOnlyList<AuthorityPackageInfo> ListPackageCatalog()
        => AuthorityPackageIds.All
            .Select(id => new AuthorityPackageInfo { Id = id, Label = AuthorityPackageIds.Labels[id] })
            .ToList();

    public async Task<EffectiveAuthoritiesDto> GetEffectiveAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        if (user.Role == UserRole.Admin)
        {
            return new EffectiveAuthoritiesDto
            {
                UserId = user.Id,
                IsAdmin = true,
                IsPoliceman = false,
                AuthorityVersion = long.MaxValue,
                Packages = AuthorityPackageIds.All.ToList()
            };
        }

        var packages = user.IsPoliceman
            ? await db.PolicemanAuthorityGrants.AsNoTracking()
                .Where(g => g.StaffUserId == userId)
                .Select(g => g.PackageId)
                .ToListAsync(ct)
            : [];

        // Team leaders get inherent tracking view packages (team-scoped via CanViewStaffAsync).
        // Never auto-grant USB / uninstall / team_management.
        if (await IsTeamLeaderAsync(userId, ct))
        {
            foreach (var inherent in new[]
                     {
                         AuthorityPackageIds.ViewScreenshot,
                         AuthorityPackageIds.ViewTimeTrack,
                         AuthorityPackageIds.ViewBrowserHistory
                     })
            {
                if (!packages.Contains(inherent, StringComparer.Ordinal))
                {
                    packages.Add(inherent);
                }
            }
        }

        return new EffectiveAuthoritiesDto
        {
            UserId = user.Id,
            IsAdmin = false,
            IsPoliceman = user.IsPoliceman,
            AuthorityVersion = user.AuthorityVersion,
            Packages = packages.OrderBy(p => p).ToList()
        };
    }

    public async Task<bool> HasAsync(Guid userId, string packageId, CancellationToken ct)
    {
        var normalized = AuthorityPackageIds.Normalize(packageId);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        if (user.Role == UserRole.Admin)
        {
            return true;
        }

        // Inherent team-leader tracking views (scoped elsewhere to team members).
        if (normalized is AuthorityPackageIds.ViewScreenshot
            or AuthorityPackageIds.ViewTimeTrack
            or AuthorityPackageIds.ViewBrowserHistory
            && await IsTeamLeaderAsync(userId, ct))
        {
            return true;
        }

        if (!user.IsPoliceman)
        {
            return false;
        }

        return await db.PolicemanAuthorityGrants.AsNoTracking()
            .AnyAsync(g => g.StaffUserId == userId && g.PackageId == normalized, ct);
    }

    public async Task EnsureHasAsync(Guid userId, string packageId, CancellationToken ct)
    {
        if (!await HasAsync(userId, packageId, ct))
        {
            throw new UnauthorizedAccessException($"Missing authority package: {packageId}");
        }
    }

    public async Task<bool> CanViewCompanyStaffAsync(Guid viewerId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == viewerId, ct);
        if (user is null)
        {
            return false;
        }

        if (user.Role == UserRole.Admin)
        {
            return true;
        }

        // Policeman with any monitoring/approval package → company-wide list.
        if (user.IsPoliceman)
        {
            return await db.PolicemanAuthorityGrants.AsNoTracking()
                .AnyAsync(g => g.StaffUserId == viewerId && (
                    g.PackageId == AuthorityPackageIds.ViewScreenshot
                    || g.PackageId == AuthorityPackageIds.ViewTimeTrack
                    || g.PackageId == AuthorityPackageIds.ViewBrowserHistory
                    || g.PackageId == AuthorityPackageIds.UsbApproval
                    || g.PackageId == AuthorityPackageIds.UninstallApproval), ct);
        }

        return false;
    }

    public async Task<bool> CanViewStaffAsync(Guid viewerId, Guid targetStaffId, CancellationToken ct)
    {
        // Leaders/police/staff never monitor themselves (sticker uses a dedicated self-timetrack path).
        if (viewerId == targetStaffId)
        {
            return false;
        }

        var viewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == viewerId, ct);
        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetStaffId, ct);
        if (viewer is null || target is null || viewer.CompanyId != target.CompanyId)
        {
            return false;
        }

        if (target.Role != UserRole.Staff)
        {
            return false;
        }

        if (viewer.Role == UserRole.Admin || await CanViewCompanyStaffAsync(viewerId, ct))
        {
            return true;
        }

        // Team leader → only members of their team.
        return await LeadsTeamMemberAsync(viewerId, targetStaffId, ct);
    }

    public async Task<bool> CanViewEventTypeAsync(Guid viewerId, string eventType, CancellationToken ct)
    {
        var eff = await GetEffectiveAsync(viewerId, ct);
        if (eff.IsAdmin)
        {
            return true;
        }

        return eventType switch
        {
            AgentEventTypes.ScreenshotMeta => eff.Packages.Contains(AuthorityPackageIds.ViewScreenshot),
            AgentEventTypes.TimeTrack => eff.Packages.Contains(AuthorityPackageIds.ViewTimeTrack),
            AgentEventTypes.BrowserHistory => eff.Packages.Contains(AuthorityPackageIds.ViewBrowserHistory),
            AgentEventTypes.UsbEvent => eff.Packages.Contains(AuthorityPackageIds.UsbApproval),
            // Phase 9 lifecycle history — never inherent for team leaders.
            AgentEventTypes.Registration or AgentEventTypes.PowerOff
                or AgentEventTypes.Uninstall or AgentEventTypes.AppBroken
                => eff.Packages.Contains(AuthorityPackageIds.UninstallApproval),
            AgentEventTypes.Heartbeat or AgentEventTypes.Connectivity or AgentEventTypes.VaultAlert
                => eff.Packages.Any(p =>
                    p is AuthorityPackageIds.ViewScreenshot
                        or AuthorityPackageIds.ViewTimeTrack
                        or AuthorityPackageIds.ViewBrowserHistory),
            _ => false
        };
    }

    public Task<bool> CanGenerateTotpAsync(Guid viewerId, CancellationToken ct)
        => HasAnyGrantedAsync(viewerId,
            [AuthorityPackageIds.UsbApproval, AuthorityPackageIds.UninstallApproval], ct);

    public Task<bool> CanManageTeamsAsync(Guid viewerId, CancellationToken ct)
        => HasGrantedAsync(viewerId, AuthorityPackageIds.TeamManagement, ct);

    public Task<bool> IsTeamLeaderAsync(Guid userId, CancellationToken ct)
        => db.Teams.AsNoTracking().AnyAsync(t => t.LeaderUserId == userId, ct);

    public Task<bool> LeadsTeamMemberAsync(Guid leaderUserId, Guid memberStaffId, CancellationToken ct)
        => db.Teams.AsNoTracking()
            .AnyAsync(t => t.LeaderUserId == leaderUserId
                           && t.Members.Any(m => m.StaffUserId == memberStaffId), ct);

    public async Task<IReadOnlyList<Guid>> ListLedMemberIdsAsync(Guid leaderUserId, CancellationToken ct)
    {
        return await db.Teams.AsNoTracking()
            .Where(t => t.LeaderUserId == leaderUserId)
            .SelectMany(t => t.Members.Select(m => m.StaffUserId))
            .Distinct()
            .ToListAsync(ct);
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
        var staff = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");
        if (staff.CompanyId != admin.CompanyId || staff.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("User is not staff in your company.");
        }

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
                GrantedByUserId = admin.Id
            });
        }

        await db.SaveChangesAsync(ct);

        var dto = await GetEffectiveAsync(staffUserId, ct);
        await hub.Clients.Group(ConfigHub.StaffGroup(staffUserId))
            .SendAsync("AuthoritiesUpdated", dto, ct);
        await hub.Clients.Group(ConfigHub.CompanyGroup(admin.CompanyId))
            .SendAsync("PolicemenUpdated", await ListPolicemenAsync(adminUserId, ct), ct);

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
        var staff = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");
        if (staff.CompanyId != admin.CompanyId || staff.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("User is not staff in your company.");
        }

        var grants = await db.PolicemanAuthorityGrants.Where(g => g.StaffUserId == staffUserId).ToListAsync(ct);
        db.PolicemanAuthorityGrants.RemoveRange(grants);
        staff.IsPoliceman = false;
        staff.PolicemanUpdatedAt = DateTimeOffset.UtcNow;
        staff.AuthorityVersion += 1;
        await db.SaveChangesAsync(ct);

        var dto = await GetEffectiveAsync(staffUserId, ct);
        await hub.Clients.Group(ConfigHub.StaffGroup(staffUserId))
            .SendAsync("AuthoritiesUpdated", dto, ct);
        await hub.Clients.Group(ConfigHub.CompanyGroup(admin.CompanyId))
            .SendAsync("PolicemenUpdated", await ListPolicemenAsync(adminUserId, ct), ct);
    }

    /// <summary>Package granted via policeman table or admin — not inherent leader views.</summary>
    private async Task<bool> HasGrantedAsync(Guid userId, string packageId, CancellationToken ct)
    {
        var normalized = AuthorityPackageIds.Normalize(packageId);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        if (user.Role == UserRole.Admin)
        {
            return true;
        }

        if (!user.IsPoliceman)
        {
            return false;
        }

        return await db.PolicemanAuthorityGrants.AsNoTracking()
            .AnyAsync(g => g.StaffUserId == userId && g.PackageId == normalized, ct);
    }

    private async Task<bool> HasAnyGrantedAsync(Guid userId, IReadOnlyList<string> packages, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        if (user.Role == UserRole.Admin)
        {
            return true;
        }

        if (!user.IsPoliceman)
        {
            return false;
        }

        return await db.PolicemanAuthorityGrants.AsNoTracking()
            .AnyAsync(g => g.StaffUserId == userId && packages.Contains(g.PackageId), ct);
    }

    private async Task<UserAccount> RequireAdminAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can manage policemen.");
        }

        return admin;
    }
}
