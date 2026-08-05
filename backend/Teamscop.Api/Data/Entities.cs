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

    /// <summary>Base32 TOTP secret used for staff uninstall authorization.</summary>
    public string? UninstallTotpSecret { get; set; }
    public bool UninstallTotpEnabled { get; set; }
    public DateTimeOffset? UninstallTotpEnrolledAt { get; set; }

    public List<UserAccount> Users { get; set; } = [];
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

    public Company Company { get; set; } = null!;
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
