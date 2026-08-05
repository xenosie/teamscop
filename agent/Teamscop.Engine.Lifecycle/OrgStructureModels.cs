namespace Teamscop.Engine.Lifecycle;

public sealed class OrgStaffDto
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool? Online { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class TeamDto
{
    public Guid TeamId { get; set; }
    public string Name { get; set; } = "";
    public OrgStaffDto Leader { get; set; } = new();
    public List<OrgStaffDto> Members { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OrgStructureDto
{
    public Guid CompanyId { get; set; }
    public string CompanyName { get; set; } = "";
    public long StructureVersion { get; set; }
    public List<TeamDto> Teams { get; set; } = [];
    /// <summary>Staff not assigned as leader or member of any team.</summary>
    public List<OrgStaffDto> UnassignedStaff { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MyOrgPlacementDto
{
    public long StructureVersion { get; set; }
    public bool IsTeamLeader { get; set; }
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public string Placement { get; set; } = "unassigned"; // leader | member | unassigned
    public TeamDto? Team { get; set; }
}
