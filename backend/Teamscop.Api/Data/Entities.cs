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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The company clock (§8.4): one timezone, nothing more. IANA or fixed-offset id
    /// (e.g. Europe/Berlin, UTC+03:00). All data is displayed in this zone (§8.2).
    /// </summary>
    public string BusinessTimeZoneId { get; set; } = "UTC";

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

    // §14 — the four-state agent status the admin sees, and the evidence it is derived from.
    //
    // Two independent reporters feed this: the LocalSystem service (LastHeartbeatAt, plus the
    // capture verdict below) and the desktop app running as the signed-in user (LastAppReportAt and
    // its claim about the service). The split is what makes "this PC is off" distinguishable from
    // "this PC is on and someone stopped the monitoring service" — a single channel cannot tell
    // those apart, because both look like silence.

    /// <summary>When the desktop app last reported. Never updates LastHeartbeatAt — see §14.2.</summary>
    public DateTimeOffset? LastAppReportAt { get; set; }

    /// <summary>The app's claim about the Windows service. Only ever darkens the verdict.</summary>
    public string? AppServiceState { get; set; }

    /// <summary>
    /// When the app first asserted a defect that is still standing. Persisted rather than held in
    /// memory so the debounce survives an API restart, and so a machine waking from sleep is not
    /// accused during the moment before its service catches up.
    /// </summary>
    public DateTimeOffset? AppDefectSinceAt { get; set; }

    /// <summary>
    /// The agent's own verdict on whether capture is working: ok, broken, disabled, idle_no_user,
    /// unsupported_session, starting. Computed on the machine because only the machine knows
    /// whether anyone is logged on — the server would otherwise read a locked workstation as
    /// sabotage.
    /// </summary>
    public string? LastCaptureState { get; set; }

    /// <summary>Why capture is broken, when it is. Null otherwise.</summary>
    public string? LastCaptureReason { get; set; }

    /// <summary>
    /// Product binaries reported missing from the install directory, comma separated.
    ///
    /// Either reporter can set this: each checks the other's files, so deleting the helper is
    /// noticed by the service and deleting the service is noticed by the app. Existence only — a
    /// local administrator can restore a file, and content hashing would fire on every upgrade.
    /// </summary>
    public string? LastMissingComponents { get; set; }

    /// <summary>Set when an authorised uninstall commits; cleared by a heartbeat well after it.</summary>
    public DateTimeOffset? UninstalledAt { get; set; }

    /// <summary>The classified status: online, offline, broken, uninstalled.</summary>
    public string? AgentStatus { get; set; }

    public string? AgentStatusReason { get; set; }

    /// <summary>When the current status began, so the history shows durations rather than instants.</summary>
    public DateTimeOffset? AgentStatusSince { get; set; }

    // §6.1 — approval codes are derived from the device key on demand, so there is no stored secret
    // and no enrolment. What remains is the server-side brute-force / replay state for the anonymous
    // verify path (§9.6, §11.2): a lockout window and the last-used company-local step per purpose.
    public int AccessTotpFailedAttempts { get; set; }
    public DateTimeOffset? AccessTotpLockoutUntil { get; set; }
    public long AccessTotpLastUsedStepUsb { get; set; }
    public long AccessTotpLastUsedStepUninstall { get; set; }

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

    /// <summary>
    /// <c>timetrack</c> rows only: the closed window this row reports, denormalized at ingest.
    /// <see cref="OccurredAt"/> is its end. The leaderboard aggregate sums
    /// <see cref="WorkedSeconds"/> / <see cref="IdleSeconds"/> in SQL instead of parsing ~1 440
    /// payloads per staff member per day. Null on every other event type, and on rows ingested
    /// before the columns existed.
    /// </summary>
    public DateTimeOffset? SegmentStartedAt { get; set; }

    public int? WorkedSeconds { get; set; }
    public int? IdleSeconds { get; set; }

    public Company Company { get; set; } = null!;
    public UserAccount User { get; set; } = null!;
}

public sealed class StaffTrackingConfigEntity
{
    public Guid StaffUserId { get; set; }
    public Guid CompanyId { get; set; }
    public string ScreenshotQuality { get; set; } = "Medium";
    public int ScreenshotPeriodSeconds { get; set; } = 180;
    public bool TimeTrackEnabled { get; set; } = true;
    public bool BrowserHistoryEnabled { get; set; } = true;
    public bool ScreenshotEnabled { get; set; } = true;
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public UserAccount StaffUser { get; set; } = null!;
    public Company Company { get; set; } = null!;
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

/// <summary>
/// The single external-export credential (§ExportAPI). One key, one secret, one consumer.
///
/// Read-only by design: this credential can read a company's tracking data through /api/v2 and can
/// set its own IP allowlist, and nothing else. It never touches the JWT auth path, never mutates
/// agent data, and is bound to exactly one company — so the export API cannot affect or widen
/// anything the product already does. The secret is stored only as an Argon2 hash, exactly like a
/// password; the plaintext exists once, at generation.
/// </summary>
public sealed class ApiClient
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Human label, e.g. the name of the one service this credential is issued to.</summary>
    public required string Name { get; set; }

    /// <summary>Public identifier sent in <c>X-Api-Key</c>. Format <c>tsk-...</c>. Unique.</summary>
    public required string KeyId { get; set; }

    /// <summary>Argon2 hash of the <c>tss-...</c> secret. The plaintext is never stored.</summary>
    public required string SecretHash { get; set; }

    /// <summary>
    /// Comma-separated source IPs the DATA endpoints will serve. Null/empty means no data endpoint
    /// answers yet — the allowlist must be set first (§ExportAPI.2), which the management endpoint
    /// itself allows so the consumer can bootstrap and recover.
    /// </summary>
    public string? AllowedIps { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    public Company Company { get; set; } = null!;
}
