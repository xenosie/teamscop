namespace Teamscop.Api.Data;

public enum UserRole
{
    Admin = 1,
    Staff = 2
}

public sealed class Company
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? AvatarUrl { get; set; }
    public Guid TokenJti { get; set; }
    public int TokenVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Deprecated company-wide TOTP (superseded by per-staff AccessTotp*).</summary>
    public string? UninstallTotpSecret { get; set; }
    public bool UninstallTotpEnabled { get; set; }
    public DateTimeOffset? UninstallTotpEnrolledAt { get; set; }

    /// <summary>IANA or fixed-offset id for company business time (e.g. Europe/Berlin, UTC+03:00).</summary>
    public string BusinessTimeZoneId { get; set; } = "UTC";
    public long BusinessClockVersion { get; set; }
    public bool BusinessClockSynchronized { get; set; }
    public DateTimeOffset? BusinessAnchorUtc { get; set; }
    public int? BusinessAnchorYear { get; set; }
    public int? BusinessAnchorMonth { get; set; }
    public int? BusinessAnchorDay { get; set; }
    public int? BusinessAnchorHour { get; set; }
    public int? BusinessAnchorMinute { get; set; }
    public int? BusinessAnchorSecond { get; set; }
    public DateTimeOffset? BusinessClockUpdatedAt { get; set; }

    /// <summary>Bumps whenever teams / membership / leaders change (SignalR org sync).</summary>
    public long OrgStructureVersion { get; set; }

    public List<UserAccount> Users { get; set; } = [];
    public List<Team> Teams { get; set; } = [];
}

public sealed class UninstallTicket
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? DeviceUserId { get; set; }
    public required string TicketHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Consumed { get; set; }

    public Company Company { get; set; } = null!;
}

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string DeviceKey { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public bool? LastOnline { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Per-staff Base32 TOTP secret for USB approve + uninstall.</summary>
    public string? AccessTotpSecret { get; set; }
    public bool AccessTotpEnabled { get; set; }
    public DateTimeOffset? AccessTotpEnrolledAt { get; set; }

    /// <summary>Staff designated as Policeman (may hold authority packages company-wide).</summary>
    public bool IsPoliceman { get; set; }
    public DateTimeOffset? PolicemanUpdatedAt { get; set; }
    public long AuthorityVersion { get; set; }

    public Company Company { get; set; } = null!;
    public List<PolicemanAuthorityGrant> AuthorityGrants { get; set; } = [];
}

/// <summary>One granted authority package for a policeman (or future grantee).</summary>
public sealed class PolicemanAuthorityGrant
{
    public Guid StaffUserId { get; set; }
    public required string PackageId { get; set; }
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? GrantedByUserId { get; set; }

    public UserAccount StaffUser { get; set; } = null!;
}

public sealed class UsbSessionTicket
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid DeviceUserId { get; set; }
    public required string TicketHash { get; set; }
    public string? DeviceInstanceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Consumed { get; set; }

    public Company Company { get; set; } = null!;
    public UserAccount DeviceUser { get; set; } = null!;
}

public sealed class AgentEvent
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid ClientEventId { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string PayloadJson { get; set; }
    public long? VaultSequence { get; set; }
    public string? ChainHash { get; set; }
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
    public long? BusinessClockVersion { get; set; }

    public Company Company { get; set; } = null!;
    public UserAccount User { get; set; } = null!;
}

public sealed class StaffTrackingConfigEntity
{
    public Guid StaffUserId { get; set; }
    public Guid CompanyId { get; set; }
    public string ScreenshotQuality { get; set; } = "Medium";
    public int ScreenshotPeriodSeconds { get; set; } = 300;
    public bool TimeTrackEnabled { get; set; } = true;
    public bool BrowserHistoryEnabled { get; set; } = true;
    public bool ScreenshotEnabled { get; set; } = true;
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public UserAccount StaffUser { get; set; } = null!;
    public Company Company { get; set; } = null!;
}

public sealed class AgentSequenceState
{
    public Guid UserId { get; set; }
    public long LastVaultSequence { get; set; }
    public string? LastChainHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long GapCount { get; set; }

    public UserAccount User { get; set; } = null!;
}

/// <summary>
/// One Team Leader + any number of members. Staff in at most one team;
/// a leader leads exactly one team and is not a member of any team.
/// </summary>
public sealed class Team
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public Guid? LeaderUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Company Company { get; set; } = null!;
    public UserAccount? Leader { get; set; }
    public List<TeamMember> Members { get; set; } = [];
}

public sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public Guid StaffUserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public Team Team { get; set; } = null!;
    public UserAccount StaffUser { get; set; } = null!;
}
