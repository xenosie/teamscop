using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Services;

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
    Task<bool> CanViewStaffTrackingAsync(Guid viewerId, Guid targetStaffId, CancellationToken ct);
    Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(Guid viewerId, CancellationToken ct);
}

public sealed class TeamService(
    AppDbContext db,
    IHubContext<ConfigHub> hub,
    IAuthorityService authorities) : ITeamService
{
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

        var membership = await db.TeamMembers.AsNoTracking()
            .Include(m => m.Team).ThenInclude(t => t.Leader)
            .Include(m => m.Team).ThenInclude(t => t.Members).ThenInclude(x => x.StaffUser)
            .FirstOrDefaultAsync(m => m.StaffUserId == userId, ct);
        if (membership is not null)
        {
            return new MyOrgPlacementDto
            {
                StructureVersion = company.OrgStructureVersion,
                IsTeamLeader = false,
                TeamId = membership.TeamId,
                TeamName = membership.Team.Name,
                Placement = "member",
                Team = ToTeamDto(membership.Team)
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
            await ClearMembershipAsync(leader.Id, ct);
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
            await ClearMembershipAsync(leader.Id, ct);
            await ClearLeadershipExceptAsync(leader.Id, excludeTeamId: team.Id, ct);
            team.LeaderUserId = leader.Id;
        }

        team.UpdatedAt = DateTimeOffset.UtcNow;
        await BumpAndBroadcastAsync(admin.CompanyId, ct);
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

        // If they lead another/this team as leader elsewhere, clear that leadership first.
        await ClearLeadershipExceptAsync(staffUserId, excludeTeamId: null, ct);

        // Leave any other membership first.
        await db.TeamMembers.Where(m => m.StaffUserId == staffUserId && m.TeamId != teamId)
            .ExecuteDeleteAsync(ct);

        await EnsureCanBecomeMemberAsync(staffUserId, excludeTeamId: teamId, ct);

        if (!await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.StaffUserId == staffUserId, ct))
        {
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = teamId,
                StaffUserId = staffUserId,
                JoinedAt = DateTimeOffset.UtcNow
            });
            team.UpdatedAt = DateTimeOffset.UtcNow;
            await BumpAndBroadcastAsync(admin.CompanyId, ct);
        }

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
        }

        return await GetTeamDtoAsync(teamId, ct);
    }

    public Task<bool> CanViewStaffTrackingAsync(Guid viewerId, Guid targetStaffId, CancellationToken ct)
        => authorities.CanViewStaffAsync(viewerId, targetStaffId, ct);

    public async Task<IReadOnlyList<OrgStaffDto>> ListVisibleStaffAsync(Guid viewerId, CancellationToken ct)
    {
        var viewer = await RequireUserAsync(viewerId, ct);
        if (!await authorities.CanViewCompanyStaffAsync(viewerId, ct))
        {
            return [];
        }

        return await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == viewer.CompanyId && u.Role == UserRole.Staff)
            .OrderBy(u => u.Username)
            .Select(u => ToStaffDto(u))
            .ToListAsync(ct);
    }

    private async Task BumpAndBroadcastAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        company.OrgStructureVersion += 1;
        await db.SaveChangesAsync(ct);

        var dto = await BuildStructureAsync(companyId, ct);
        await hub.Clients.Group(ConfigHub.CompanyGroup(companyId))
            .SendAsync("OrgStructureUpdated", dto, ct);
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

    private async Task ClearMembershipAsync(Guid staffUserId, CancellationToken ct)
    {
        var memberships = await db.TeamMembers.Where(m => m.StaffUserId == staffUserId).ToListAsync(ct);
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

    private async Task EnsureCanBecomeLeaderAsync(Guid staffUserId, CancellationToken ct)
    {
        var leadsOther = await db.Teams.AnyAsync(t => t.LeaderUserId == staffUserId, ct);
        if (leadsOther)
        {
            throw new InvalidOperationException("Staff already leads another team.");
        }

        var isMember = await db.TeamMembers.AnyAsync(m => m.StaffUserId == staffUserId, ct);
        if (isMember)
        {
            throw new InvalidOperationException("Staff is a team member; remove them from their team before making them a leader.");
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
           ?? throw new UnauthorizedAccessException("User not found.");

    private async Task<UserAccount> RequireTeamManagerAsync(Guid userId, CancellationToken ct)
    {
        var user = await RequireUserAsync(userId, ct);
        if (!await authorities.CanManageTeamsAsync(userId, ct))
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
