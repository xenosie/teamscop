using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Api.Services.Access;
using Teamscop.Engine.Lifecycle;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services;

/// <summary>
/// The <c>StaffRegistered</c> push, carried on the company-wide group. Deliberately holds nothing
/// but the company and the new structure version: every recipient re-reads its own roster through
/// an endpoint that scopes the answer, so this message can never widen anyone's reach.
/// </summary>
public sealed class StaffRosterChangedDto
{
    public Guid CompanyId { get; set; }
    public long StructureVersion { get; set; }
}

public sealed record CreateTeamRequest(string Name, Guid? LeaderUserId = null);
public sealed record UpdateTeamRequest(string? Name, Guid? LeaderUserId = null, bool ClearLeader = false);
public sealed record SetMembersRequest(IReadOnlyList<Guid> MemberUserIds);
public sealed record AddMemberRequest(Guid StaffUserId);

public interface ITeamService
{
    Task<OrgStructureDto> GetCompanyStructureAsync(Guid requesterId, CancellationToken ct);
    Task<MyOrgPlacementDto> GetMyPlacementAsync(Guid userId, CancellationToken ct);
    Task<TeamDto> CreateTeamAsync(Guid adminUserId, CreateTeamRequest request, CancellationToken ct);
    Task<TeamDto> UpdateTeamAsync(Guid adminUserId, Guid teamId, UpdateTeamRequest request, CancellationToken ct);
    Task DeleteTeamAsync(Guid adminUserId, Guid teamId, CancellationToken ct);
    Task<TeamDto> SetMembersAsync(Guid adminUserId, Guid teamId, SetMembersRequest request, CancellationToken ct);
    Task<TeamDto> AddMemberAsync(Guid adminUserId, Guid teamId, Guid staffUserId, CancellationToken ct);
    Task<TeamDto> RemoveMemberAsync(Guid adminUserId, Guid teamId, Guid staffUserId, CancellationToken ct);
    Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(Guid viewerId, CancellationToken ct);

    /// <summary>
    /// The roster changed without any team edit — a new machine enrolled (§2.3). Bumps the
    /// structure version and pushes it, so an admin who already has the app open discovers the new
    /// employee immediately instead of on the next cold start.
    ///
    /// Takes a company rather than a requester on purpose: enrolment has no approver to authorize
    /// against. Nothing privileged widens — the org chart still goes only to the management group,
    /// and everyone else gets a contentless "re-read your own scoped roster" signal.
    /// </summary>
    Task NotifyRosterChangedAsync(Guid companyId, CancellationToken ct);
}

public sealed class TeamService(
    AppDbContext db,
    IHubContext<ConfigHub> hub,
    IAccessPolicy access,
    IAvatarAccess avatars,
    IAuditLog audit,
    ILogger<TeamService> logger) : ITeamService
{
    private static readonly IReadOnlySet<string> NoPackages =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<Guid> NoMembers = new HashSet<Guid>();

    public async Task<OrgStructureDto> GetCompanyStructureAsync(Guid requesterId, CancellationToken ct)
    {
        var user = await RequireTeamManagerAsync(requesterId, ct);
        return await BuildStructureAsync(user.CompanyId, ct);
    }

    public async Task<MyOrgPlacementDto> GetMyPlacementAsync(Guid userId, CancellationToken ct)
    {
        var user = await RequireUserAsync(userId, ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);

        if (user.Role == UserRole.Admin)
        {
            return new MyOrgPlacementDto
            {
                StructureVersion = company.OrgStructureVersion,
                IsTeamLeader = false,
                Placement = "admin"
            };
        }

        var led = await db.Teams.AsNoTracking()
            .Include(t => t.Leader)
            .Include(t => t.Members).ThenInclude(m => m.StaffUser)
            .FirstOrDefaultAsync(t => t.LeaderUserId == userId, ct);
        if (led is not null)
        {
            return new MyOrgPlacementDto
            {
                StructureVersion = company.OrgStructureVersion,
                IsTeamLeader = true,
                TeamId = led.Id,
                TeamName = led.Name,
                Placement = "leader",
                Team = ToTeamDto(led)
            };
        }

        // Two queries, not one Include chain. TeamMember → Team → Members walks back to
        // TeamMember, and EF rejects a cycle in a no-tracking query at COMPILATION time — before
        // the predicate runs — so GET /api/org/me answered 400 for every staff member and every
        // leader, in or out of a team. Admins never saw it because they return above. The desktop
        // feeds this into IAuthorityState.Apply, so a team leader could never be told they lead a
        // team (§4.2, §4.6). Same acyclic shape as GetTeamDtoAsync.
        var memberTeamId = await db.TeamMembers.AsNoTracking()
            .Where(m => m.StaffUserId == userId)
            .Select(m => (Guid?)m.TeamId)
            .FirstOrDefaultAsync(ct);
        if (memberTeamId is Guid teamId)
        {
            var team = await db.Teams.AsNoTracking()
                .Include(t => t.Leader)
                .Include(t => t.Members).ThenInclude(x => x.StaffUser)
                .FirstAsync(t => t.Id == teamId, ct);
            return new MyOrgPlacementDto
            {
                StructureVersion = company.OrgStructureVersion,
                IsTeamLeader = false,
                TeamId = team.Id,
                TeamName = team.Name,
                Placement = "member",
                Team = ToTeamDto(team)
            };
        }

        return new MyOrgPlacementDto
        {
            StructureVersion = company.OrgStructureVersion,
            IsTeamLeader = false,
            Placement = "unassigned"
        };
    }

    public async Task<TeamDto> CreateTeamAsync(Guid adminUserId, CreateTeamRequest request, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var name = NormalizeName(request.Name);
        await EnsureUniqueTeamNameAsync(admin.CompanyId, name, excludeTeamId: null, ct);

        Guid? leaderId = null;
        if (request.LeaderUserId is Guid requestedLeader)
        {
            var leader = await RequireCompanyStaffAsync(admin.CompanyId, requestedLeader, ct);
            await ClearMembershipAsync(leader.Id, excludeTeamId: null, ct);
            await ClearLeadershipExceptAsync(leader.Id, excludeTeamId: null, ct);
            leaderId = leader.Id;
        }

        var team = new Team
        {
            Id = Guid.NewGuid(),
            CompanyId = admin.CompanyId,
            Name = name,
            LeaderUserId = leaderId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Teams.Add(team);
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
        audit.Record(AuditActions.TeamCreated, admin.UserId, admin.CompanyId,
            new { teamId = team.Id, name, leaderUserId = leaderId });
        return await GetTeamDtoAsync(team.Id, ct);
    }

    public async Task<TeamDto> UpdateTeamAsync(Guid adminUserId, Guid teamId, UpdateTeamRequest request, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.CompanyId == admin.CompanyId, ct)
            ?? throw new InvalidOperationException("Team not found.");

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = NormalizeName(request.Name);
            await EnsureUniqueTeamNameAsync(admin.CompanyId, name, team.Id, ct);
            team.Name = name;
        }

        if (request.ClearLeader)
        {
            team.LeaderUserId = null;
        }
        else if (request.LeaderUserId is Guid newLeaderId && newLeaderId != team.LeaderUserId)
        {
            var leader = await RequireCompanyStaffAsync(admin.CompanyId, newLeaderId, ct);
            // Promote / steal: leave any membership; clear leadership on other teams.
            await ClearMembershipAsync(leader.Id, excludeTeamId: null, ct);
            await ClearLeadershipExceptAsync(leader.Id, excludeTeamId: team.Id, ct);
            team.LeaderUserId = leader.Id;
        }

        team.UpdatedAt = DateTimeOffset.UtcNow;
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
        audit.Record(AuditActions.TeamUpdated, admin.UserId, admin.CompanyId,
            new { teamId = team.Id, team.Name, leaderUserId = team.LeaderUserId });
        return await GetTeamDtoAsync(team.Id, ct);
    }

    public async Task DeleteTeamAsync(Guid adminUserId, Guid teamId, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var team = await db.Teams.Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == teamId && t.CompanyId == admin.CompanyId, ct)
            ?? throw new InvalidOperationException("Team not found.");
        db.Teams.Remove(team);
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
        audit.Record(AuditActions.TeamDeleted, admin.UserId, admin.CompanyId,
            new { teamId, team.Name, leaderUserId = team.LeaderUserId });
    }

    public async Task<TeamDto> SetMembersAsync(Guid adminUserId, Guid teamId, SetMembersRequest request, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var team = await db.Teams.Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == teamId && t.CompanyId == admin.CompanyId, ct)
            ?? throw new InvalidOperationException("Team not found.");

        var desired = (request.MemberUserIds ?? []).Distinct().ToList();
        if (team.LeaderUserId is Guid leaderId && desired.Contains(leaderId))
        {
            throw new InvalidOperationException("Team leader cannot also be a team member.");
        }

        foreach (var staffId in desired)
        {
            await RequireCompanyStaffAsync(admin.CompanyId, staffId, ct);
            await EnsureCanBecomeMemberAsync(staffId, excludeTeamId: team.Id, ct);
        }

        db.TeamMembers.RemoveRange(team.Members);
        foreach (var staffId in desired)
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id,
                StaffUserId = staffId,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }

        team.UpdatedAt = DateTimeOffset.UtcNow;
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
        audit.Record(AuditActions.TeamMembersReplaced, admin.UserId, admin.CompanyId,
            new { teamId = team.Id, memberUserIds = desired });
        return await GetTeamDtoAsync(team.Id, ct);
    }

    public async Task<TeamDto> AddMemberAsync(Guid adminUserId, Guid teamId, Guid staffUserId, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.CompanyId == admin.CompanyId, ct)
            ?? throw new InvalidOperationException("Team not found.");
        if (team.LeaderUserId == staffUserId)
        {
            throw new InvalidOperationException("Team leader cannot also be a team member.");
        }

        await RequireCompanyStaffAsync(admin.CompanyId, staffUserId, ct);

        // Adding someone to a team supersedes what they were doing before — leading another team,
        // or belonging to one. Both are cleared here, which is precisely why neither may then be
        // re-checked: EnsureCanBecomeMemberAsync queries the database, which has not seen these
        // stages yet, so it read back the state we just cleared and refused every promotion.
        await ClearLeadershipExceptAsync(staffUserId, excludeTeamId: null, ct);
        await ClearMembershipAsync(staffUserId, excludeTeamId: teamId, ct);

        if (!await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.StaffUserId == staffUserId, ct))
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = teamId,
                StaffUserId = staffUserId,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }

        // Always persist leadership clears / membership changes (even on re-add).
        team.UpdatedAt = DateTimeOffset.UtcNow;
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
        audit.Record(AuditActions.TeamMemberAdded, admin.UserId, admin.CompanyId, new { teamId, staffUserId });

        return await GetTeamDtoAsync(teamId, ct);
    }

    public async Task<TeamDto> RemoveMemberAsync(Guid adminUserId, Guid teamId, Guid staffUserId, CancellationToken ct)
    {
        var admin = await RequireTeamManagerAsync(adminUserId, ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.CompanyId == admin.CompanyId, ct)
            ?? throw new InvalidOperationException("Team not found.");
        var row = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.StaffUserId == staffUserId, ct);
        if (row is not null)
        {
            db.TeamMembers.Remove(row);
            team.UpdatedAt = DateTimeOffset.UtcNow;
            await BumpAndBroadcastAsync(admin.CompanyId, ct);
            audit.Record(AuditActions.TeamMemberRemoved, admin.UserId, admin.CompanyId, new { teamId, staffUserId });
        }

        return await GetTeamDtoAsync(teamId, ct);
    }

    public async Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(Guid viewerId, CancellationToken ct)
    {
        var viewer = await access.GetAsync(viewerId, ct);

        // This list drives the tracking views, so it is scoped by CanViewCompanyData, not by the
        // roster flag: an approval-only policeman picks staff on the Codes screen, not here.
        // Admin / policeman with a granted VIEW package → whole company (never include self).
        if (viewer.CanViewCompanyData)
        {
            return await db.Users.AsNoTracking()
                .Where(u => u.CompanyId == viewer.CompanyId
                            && u.Role == UserRole.Staff
                            && u.Id != viewerId)
                .OrderBy(u => u.Username)
                .Select(u => ToStaffDto(u))
                .ToListAsync(ct);
        }

        // Team leader → only members of the team they lead (immediate with leader assignment).
        var memberIds = viewer.LedMemberIds;
        if (memberIds.Count == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking()
            .Where(u => memberIds.Contains(u.Id) && u.Role == UserRole.Staff && u.Id != viewerId)
            .OrderBy(u => u.Username)
            .Select(u => ToStaffDto(u))
            .ToListAsync(ct);
    }

    public async Task NotifyRosterChangedAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        company.OrgStructureVersion += 1;
        await db.SaveChangesAsync(ct);

        // A face nobody has ever asked for still needs the reach cache to admit it exists: the
        // admin's first fetch of the new avatar happens seconds after this (B12).
        avatars.InvalidateCompany(companyId);

        // Privileged payload, management group only — same rule the team edits follow. The desktop
        // already force-reloads its staff directory on this message, so no client change is needed
        // for an admin to see the new employee at once (§4.1, §14.1).
        var dto = await BuildStructureAsync(companyId, ct);
        await hub.Clients.Group(ConfigHub.CompanyManagementGroup(companyId))
            .SendAsync("OrgStructureUpdated", dto, ct);

        // …and a contentless nudge to everyone else. A team leader or an approval-only policeman
        // is deliberately NOT in the management group and must never receive the org chart, but
        // they do have a roster of their own that just changed. This carries no names and no ids —
        // only "re-read the list you are already allowed to read", which the server re-scopes.
        await hub.Clients.Group(ConfigHub.CompanyGroup(companyId))
            .SendAsync("StaffRegistered", new StaffRosterChangedDto
            {
                CompanyId = companyId,
                StructureVersion = company.OrgStructureVersion
            }, ct);

        logger.LogInformation("Roster change v{Version} broadcast for company {CompanyId}",
            company.OrgStructureVersion, companyId);
    }

    private async Task BumpAndBroadcastAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        company.OrgStructureVersion += 1;
        await db.SaveChangesAsync(ct);

        // Leadership just moved, so every memoized ViewerContext in this request is stale.
        access.Invalidate();
        // …as is every viewer's cached avatar reach: a leader who lost their team must stop being
        // able to fetch its faces at once, not when the cache window closes (B12).
        avatars.InvalidateCompany(companyId);

        var dto = await BuildStructureAsync(companyId, ct);
        // Org chart is management-only — GET /api/org/structure requires team_management.
        await hub.Clients.Group(ConfigHub.CompanyManagementGroup(companyId))
            .SendAsync("OrgStructureUpdated", dto, ct);

        // Leaders gain/lose inherent view packages immediately with assignment changes, so every
        // staff member is pushed their new effective set. Three batch queries build the whole
        // company; asking IAccessPolicy per staff member cost two each, so one "add member" click
        // in a 50-person company ran ~100 queries inside the request (B5).
        var principals = await LoadCompanyStaffPrincipalsAsync(companyId, ct);
        foreach (var principal in principals)
        {
            await hub.Clients.Group(ConfigHub.StaffGroup(principal.UserId))
                .SendAsync("AuthoritiesUpdated", principal.ToEffectiveAuthorities(), ct);
        }

        logger.LogInformation("Org structure v{Version} broadcast for company {CompanyId} ({StaffCount} staff)",
            company.OrgStructureVersion, companyId, principals.Count);
    }

    /// <summary>
    /// Every staff principal in one company, in three queries regardless of headcount.
    ///
    /// Deliberately reconstructs <see cref="ViewerContext"/> — the same record
    /// <see cref="IAccessPolicy"/> loads one user at a time — so a batched principal and a
    /// singly-loaded one answer identically. Only the loading strategy differs, never the rules.
    /// </summary>
    private async Task<List<ViewerContext>> LoadCompanyStaffPrincipalsAsync(Guid companyId, CancellationToken ct)
    {
        var staff = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.Role == UserRole.Staff)
            .Select(u => new { u.Id, u.Role, u.IsPoliceman, u.AuthorityVersion })
            .ToListAsync(ct);
        if (staff.Count == 0)
        {
            return [];
        }

        // Loaded for every staff member, policeman or not, exactly as the per-user load does:
        // GrantedPackages is raw rows and only IsPoliceman decides whether they count.
        var grants = await db.PolicemanAuthorityGrants.AsNoTracking()
            .Where(g => g.StaffUser.CompanyId == companyId)
            .Select(g => new { g.StaffUserId, g.PackageId })
            .ToListAsync(ct);
        var grantsByUser = grants
            .GroupBy(g => g.StaffUserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)new HashSet<string>(g.Select(x => x.PackageId), StringComparer.Ordinal));

        var ledTeams = await db.Teams.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.LeaderUserId != null)
            .Select(t => new
            {
                TeamId = t.Id,
                LeaderUserId = t.LeaderUserId!.Value,
                MemberIds = t.Members.Select(m => m.StaffUserId).ToList()
            })
            .ToListAsync(ct);
        var ledByLeader = ledTeams
            .GroupBy(t => t.LeaderUserId)
            .ToDictionary(
                g => g.Key,
                g => (TeamId: g.First().TeamId,
                      MemberIds: (IReadOnlySet<Guid>)g.SelectMany(t => t.MemberIds).ToHashSet()));

        return staff.Select(u =>
        {
            var leads = ledByLeader.TryGetValue(u.Id, out var led);
            return new ViewerContext(
                u.Id,
                companyId,
                u.Role,
                u.IsPoliceman,
                u.AuthorityVersion,
                grantsByUser.GetValueOrDefault(u.Id) ?? NoPackages,
                leads ? led.TeamId : null,
                leads ? led.MemberIds : NoMembers);
        }).ToList();
    }

    private async Task<OrgStructureDto> BuildStructureAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId, ct);
        var teams = await db.Teams.AsNoTracking()
            .Include(t => t.Leader)
            .Include(t => t.Members).ThenInclude(m => m.StaffUser)
            .Where(t => t.CompanyId == companyId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var assigned = teams
            .Where(t => t.LeaderUserId.HasValue)
            .Select(t => t.LeaderUserId!.Value)
            .Concat(teams.SelectMany(t => t.Members.Select(m => m.StaffUserId)))
            .ToHashSet();

        var unassigned = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.Role == UserRole.Staff && !assigned.Contains(u.Id))
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        return new OrgStructureDto
        {
            CompanyId = company.Id,
            CompanyName = company.Name,
            StructureVersion = company.OrgStructureVersion,
            Teams = teams.Select(ToTeamDto).ToList(),
            UnassignedStaff = unassigned.Select(ToStaffDto).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<TeamDto> GetTeamDtoAsync(Guid teamId, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking()
            .Include(t => t.Leader)
            .Include(t => t.Members).ThenInclude(m => m.StaffUser)
            .FirstAsync(t => t.Id == teamId, ct);
        return ToTeamDto(team);
    }

    private static TeamDto ToTeamDto(Team team) => new()
    {
        TeamId = team.Id,
        Name = team.Name,
        Leader = team.Leader is null ? null : ToStaffDto(team.Leader),
        Members = team.Members.Select(m => ToStaffDto(m.StaffUser)).OrderBy(m => m.Username).ToList(),
        UpdatedAt = team.UpdatedAt
    };

    private static OrgStaffDto ToStaffDto(UserAccount u) => new()
    {
        UserId = u.Id,
        Username = u.Username,
        AvatarUrl = u.AvatarUrl,
        Online = u.LastOnline,
        LastSeenAt = u.LastSeenAt
    };

    /// <summary>
    /// Stages the removal of every membership except <paramref name="excludeTeamId"/>. Tracked
    /// rather than an <c>ExecuteDeleteAsync</c>, so it lands in the same SaveChanges — and the same
    /// transaction — as the change that made it necessary.
    /// </summary>
    private async Task ClearMembershipAsync(Guid staffUserId, Guid? excludeTeamId, CancellationToken ct)
    {
        var memberships = await db.TeamMembers
            .Where(m => m.StaffUserId == staffUserId && (excludeTeamId == null || m.TeamId != excludeTeamId))
            .ToListAsync(ct);
        if (memberships.Count > 0)
        {
            db.TeamMembers.RemoveRange(memberships);
        }
    }

    private async Task ClearLeadershipExceptAsync(Guid staffUserId, Guid? excludeTeamId, CancellationToken ct)
    {
        var led = await db.Teams
            .Where(t => t.LeaderUserId == staffUserId && (excludeTeamId == null || t.Id != excludeTeamId))
            .ToListAsync(ct);
        foreach (var t in led)
        {
            t.LeaderUserId = null;
            t.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task EnsureCanBecomeMemberAsync(Guid staffUserId, Guid? excludeTeamId, CancellationToken ct)
    {
        var isLeader = await db.Teams.AnyAsync(t => t.LeaderUserId == staffUserId, ct);
        if (isLeader)
        {
            throw new InvalidOperationException("Team leaders cannot be members of a team.");
        }

        var other = await db.TeamMembers.AnyAsync(
            m => m.StaffUserId == staffUserId && (excludeTeamId == null || m.TeamId != excludeTeamId), ct);
        if (other)
        {
            throw new InvalidOperationException("Staff already belongs to another team.");
        }
    }

    private async Task EnsureUniqueTeamNameAsync(Guid companyId, string name, Guid? excludeTeamId, CancellationToken ct)
    {
        var exists = await db.Teams.AnyAsync(
            t => t.CompanyId == companyId && t.Name == name && (excludeTeamId == null || t.Id != excludeTeamId), ct);
        if (exists)
        {
            throw new InvalidOperationException("A team with that name already exists.");
        }
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 200)
        {
            throw new InvalidOperationException("Team name must be 1–200 characters.");
        }

        return trimmed;
    }

    private async Task<UserAccount> RequireUserAsync(Guid userId, CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
           ?? throw new SessionInvalidException("User not found.");

    private async Task<ViewerContext> RequireTeamManagerAsync(Guid userId, CancellationToken ct)
    {
        var user = await access.GetAsync(userId, ct);
        if (!user.CanManageTeams)
        {
            throw new UnauthorizedAccessException("Missing authority package: team_management");
        }

        return user;
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
