using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services;

/// <param name="Enabled">
/// Always <c>true</c> in the derived-code model (§6.1): codes are computed on demand from the
/// device key, so there is no "not enrolled yet" state. Kept for the desktop's list shape.
/// </param>
/// <param name="Role">Always <c>staff</c> — the Codes screen is staff-only (§1.5).</param>
/// <param name="IsSelfMachine">Always <c>false</c> — the admin's own row is not listed (§1.5).</param>
public sealed record TotpStatusDto(
    Guid StaffUserId,
    string StaffUsername,
    bool Enabled,
    DateTimeOffset? EnrolledAt,
    string Role,
    bool IsSelfMachine);

public sealed record TotpCodeDto(
    Guid StaffUserId,
    string StaffUsername,
    string Code,
    int PeriodSeconds,
    int RemainingSeconds);

public sealed record UninstallTicketDto(string UninstallTicket, long ExpiresIn);

public sealed record UsbApproveDto(
    string UsbSessionTicket,
    long ExpiresIn,
    string? DeviceInstanceId);

public interface ILifecycleService
{
    Task<IReadOnlyList<TotpStatusDto>> ListStaffTotpAsync(Guid adminUserId, CancellationToken ct);
    Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
    Task<TotpCodeDto> GetTotpCodeAsync(Guid adminUserId, Guid staffUserId, string? purpose, CancellationToken ct);
    Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct);
    Task<bool> ConsumeUninstallTicketAsync(string ticket, CancellationToken ct);
    Task<UsbApproveDto> VerifyUsbAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct);
    Task<bool> ConsumeUsbTicketAsync(string ticket, CancellationToken ct);
    Task HeartbeatAsync(Guid userId, CancellationToken ct);
    Task HeartbeatAsync(Guid userId, AgentHeartbeatReport? report, CancellationToken ct);
    Task AppReportAsync(Guid userId, AppStatusReport report, CancellationToken ct);
}

/// <summary>
/// The agent's own verdict, carried on the heartbeat. Optional — an older agent sends no body and
/// the server simply stores nothing new.
/// </summary>
public sealed record AgentHeartbeatReport(
    string? CaptureState,
    string? CaptureReason,
    string? MissingComponents);

/// <summary>The desktop app's claim about the service and the files on disk.</summary>
public sealed record AppStatusReport(string? ServiceState, string? MissingComponents);

public sealed class LifecycleService(
    AppDbContext db,
    IAccessPolicy access,
    IAuditLog audit,
    ILogger<LifecycleService> logger) : ILifecycleService
{
    private const int MaxTotpFailedAttempts = 8;
    private static readonly TimeSpan TotpLockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UsbTicketLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every staff member the caller may issue a code for (§1.5 — staff only; the admin's own PC is
    /// not listed and is not monitored). Codes are always available in the derived model (§6.1), so
    /// every row reports <c>Enabled = true</c>; there is no enrolment to reflect.
    /// </summary>
    public async Task<IReadOnlyList<TotpStatusDto>> ListStaffTotpAsync(Guid adminUserId, CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == actor.CompanyId && u.Role == UserRole.Staff)
            .OrderBy(u => u.Username)
            .Select(u => new TotpStatusDto(u.Id, u.Username, true, null, "staff", false))
            .ToListAsync(ct);
    }

    public async Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        var staff = await RequireApprovalTargetAsync(actor, staffUserId, ct);
        return new TotpStatusDto(staff.Id, staff.Username, true, null, "staff", false);
    }

    /// <summary>
    /// The current 6-digit code for a staff machine, for the admin to relay out of band (§10.2).
    ///
    /// §6.1 — derived deterministically from the machine's device key, so there is no stored secret
    /// and no enrolment; a code is always available. §6.3 — the RFC-6238 step counter runs on
    /// COMPANY-LOCAL time, so the agent, verifying offline against the same device key and the same
    /// company timezone, computes the same code (§6.4). The audit trail of who asked is kept (§10.1).
    /// </summary>
    public async Task<TotpCodeDto> GetTotpCodeAsync(
        Guid adminUserId,
        Guid staffUserId,
        string? purpose,
        CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        var staff = await RequireApprovalTargetAsync(actor, staffUserId, ct);

        var chosenPurpose = await ResolveTotpPurposeAsync(actor.Id, purpose, ct);
        var companyNow = CompanyNow(staff.Company);
        var secret = TotpGenerator.DeriveMachineSecret(staff.DeviceKey, chosenPurpose);
        var code = TotpGenerator.ComputeCode(secret, companyNow);
        var remaining = TotpGenerator.PeriodSeconds - (int)(companyNow.ToUnixTimeSeconds() % TotpGenerator.PeriodSeconds);

        // §10.1/§10.2: the code leaves the system out of band, so who asked for it is the trail.
        audit.Record(AuditActions.TotpCodeIssued, actor.Id, actor.CompanyId,
            new { staffUserId, purpose = chosenPurpose });

        return new TotpCodeDto(staff.Id, staff.Username, code, TotpGenerator.PeriodSeconds, remaining);
    }

    public async Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct)
    {
        var user = await RequireApprovalDeviceAsync(deviceKey, ct);
        await EnsureStaffTotpAsync(user, totpCode, TotpGenerator.PurposeUninstall, ct);

        var ticket = NewTicket();
        db.UninstallTickets.Add(new UninstallTicket
        {
            Id = Guid.NewGuid(),
            CompanyId = user.CompanyId,
            DeviceUserId = user.Id,
            TicketHash = HashTicket(ticket),
            ExpiresAt = DateTimeOffset.UtcNow.Add(TicketLifetime),
            CreatedAt = DateTimeOffset.UtcNow,
            Consumed = false
        });
        await db.SaveChangesAsync(ct);
        audit.Record(AuditActions.UninstallApprovalGranted, user.Id, user.CompanyId, new { deviceUserId = user.Id });
        return new UninstallTicketDto(ticket, (long)TicketLifetime.TotalSeconds);
    }

    public async Task<bool> ConsumeUninstallTicketAsync(string ticket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return false;
        }

        var hash = HashTicket(ticket);
        var entity = await db.UninstallTickets.FirstOrDefaultAsync(t => t.TicketHash == hash, ct);
        if (entity is null || entity.Consumed || entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogWarning("Uninstall ticket rejected (unknown, consumed or expired)");
            return false;
        }

        entity.Consumed = true;
        // §4.5 / §7.4 — app history is a MONITORED machine's lifecycle. An admin machine can now
        // hold an uninstall credential, and its removal must not become the one agent_event row an
        // admin account ever owns. The audit log below still records it.
        if (entity.DeviceUserId is Guid staffUserId
            && await db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == staffUserId && u.Role == UserRole.Staff, ct))
        {
            var now = DateTimeOffset.UtcNow;
            var uninstall = new AgentEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = entity.CompanyId,
                UserId = staffUserId,
                ClientEventId = Guid.NewGuid(),
                EventType = AgentEventTypes.Uninstall,
                OccurredAt = now,
                ReceivedAt = now,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    kind = AgentEventTypes.Uninstall,
                    ticketId = entity.Id,
                    consumedAt = now
                })
            };
            db.AgentEvents.Add(uninstall);
        }

        await db.SaveChangesAsync(ct);
        audit.Record(AuditActions.UninstallApprovalConsumed,
            entity.DeviceUserId ?? Guid.Empty, entity.CompanyId, new { ticketId = entity.Id });
        return true;
    }

    public async Task<UsbApproveDto> VerifyUsbAsync(
        string deviceKey,
        string totpCode,
        string? deviceInstanceId,
        CancellationToken ct)
    {
        var user = await RequireApprovalDeviceAsync(deviceKey, ct);
        await EnsureStaffTotpAsync(user, totpCode, TotpGenerator.PurposeUsb, ct);

        var ticket = NewTicket();
        db.UsbSessionTickets.Add(new UsbSessionTicket
        {
            Id = Guid.NewGuid(),
            CompanyId = user.CompanyId,
            DeviceUserId = user.Id,
            TicketHash = HashTicket(ticket),
            DeviceInstanceId = string.IsNullOrWhiteSpace(deviceInstanceId) ? null : deviceInstanceId.Trim(),
            ExpiresAt = DateTimeOffset.UtcNow.Add(UsbTicketLifetime),
            CreatedAt = DateTimeOffset.UtcNow,
            Consumed = false
        });
        await db.SaveChangesAsync(ct);
        audit.Record(AuditActions.UsbApprovalGranted, user.Id, user.CompanyId,
            new { deviceUserId = user.Id, deviceInstanceId });
        return new UsbApproveDto(ticket, (long)UsbTicketLifetime.TotalSeconds, deviceInstanceId);
    }

    public async Task<bool> ConsumeUsbTicketAsync(string ticket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return false;
        }

        var hash = HashTicket(ticket);
        var entity = await db.UsbSessionTickets.FirstOrDefaultAsync(t => t.TicketHash == hash, ct);
        if (entity is null || entity.Consumed || entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogWarning("USB session ticket rejected (unknown, consumed or expired)");
            return false;
        }

        entity.Consumed = true;
        await db.SaveChangesAsync(ct);
        audit.Record(AuditActions.UsbApprovalConsumed, entity.DeviceUserId, entity.CompanyId,
            new { ticketId = entity.Id, entity.DeviceInstanceId });
        return true;
    }

    /// <summary>
    /// Presence for a monitored machine. A non-staff caller is accepted and recorded as nothing:
    /// §4.5 says an admin is not monitored, and liveness IS monitoring — <c>LastHeartbeatAt</c> is
    /// what §14.1's "who is working now" reads. Deliberately not a 403, unlike ingest: an admin
    /// PC running yesterday's agent would otherwise sit in a permanent error loop for a call whose
    /// correct outcome is simply that nothing is stored.
    /// </summary>
    public async Task HeartbeatAsync(Guid userId, CancellationToken ct)
        => await HeartbeatAsync(userId, null, ct);

    /// <summary>
    /// The service's liveness ping, optionally carrying the agent's own verdict on capture.
    ///
    /// The verdict rides on the heartbeat rather than through the outbox on purpose: the outbox is a
    /// FIFO queue that can be hundreds of items deep after an outage, so health news would arrive
    /// last, exactly when it matters most. The body is optional so an older agent keeps working.
    /// </summary>
    public async Task HeartbeatAsync(Guid userId, AgentHeartbeatReport? report, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new SessionInvalidException("User not found.");
        if (user.Role != UserRole.Staff)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        user.LastHeartbeatAt = now;

        if (report is not null)
        {
            user.LastCaptureState = Trim(report.CaptureState, 40);
            user.LastCaptureReason = Trim(report.CaptureReason, 200);
            user.LastMissingComponents = Trim(report.MissingComponents, 400);
        }

        // A heartbeat well after an uninstall means the product was put back. Cleared here rather
        // than on the registration event alone, because a repair reinstall keeps the same account.
        if (user.UninstalledAt is not null && now - user.UninstalledAt.Value > StaffAgentStatus.UninstallClear)
        {
            user.UninstalledAt = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The desktop app's independent report. Deliberately does NOT touch LastHeartbeatAt or
    /// LastSeenAt: this channel runs as the monitored user, and letting it write liveness would let
    /// anyone keep a machine looking alive while nothing was actually running.
    /// </summary>
    public async Task AppReportAsync(Guid userId, AppStatusReport report, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new SessionInvalidException("User not found.");
        if (user.Role != UserRole.Staff)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var state = Trim(report.ServiceState, 40) ?? AppServiceStates.Unknown;
        user.LastAppReportAt = now;
        user.AppServiceState = state;

        // AppDefectSinceAt is when the CURRENT defect started, so the classifier can require it to
        // have stood for a while before believing it. Stored rather than held in memory so an API
        // restart does not reset every machine's debounce to zero.
        if (AppServiceStates.Defects.Contains(state))
        {
            user.AppDefectSinceAt ??= now;
        }
        else
        {
            user.AppDefectSinceAt = null;
        }

        if (!string.IsNullOrWhiteSpace(report.MissingComponents))
        {
            user.LastMissingComponents = Trim(report.MissingComponents, 400);
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>
    /// When the actor holds both USB and uninstall packages, an optional <paramref name="purpose"/>
    /// query (<c>usb</c> or <c>uninstall</c>) selects which code to return; otherwise defaults to USB.
    /// </summary>
    private async Task<string> ResolveTotpPurposeAsync(Guid actorUserId, string? purpose, CancellationToken ct)
    {
        var actor = await access.GetAsync(actorUserId, ct);
        var hasUsb = actor.Has(AuthorityPackageIds.UsbApproval);
        var hasUninstall = actor.Has(AuthorityPackageIds.UninstallApproval);
        if (!hasUsb && !hasUninstall)
        {
            throw new UnauthorizedAccessException("Missing authority package: usb_approval or uninstall_approval");
        }

        // An explicit ?purpose= is honoured when held and REFUSED when not — never substituted.
        // The old rule silently swapped in the other purpose for single-package policemen, so a
        // caller asking for an uninstall code received a USB code labelled "uninstall", which the
        // guard then refused on every attempt with no hint why.
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            var wanted = NormalizePurpose(purpose);
            var held = wanted == TotpGenerator.PurposeUsb ? hasUsb : hasUninstall;
            if (!held)
            {
                throw new UnauthorizedAccessException($"Missing authority package for {wanted} codes.");
            }

            return wanted;
        }

        return hasUsb ? TotpGenerator.PurposeUsb : TotpGenerator.PurposeUninstall;
    }

    /// <summary>
    /// Company-local "now" as a zero-offset instant, so <c>ToUnixTimeSeconds()/30</c> yields the
    /// §6.3 company-local step. The conversion UTC → company-local is unambiguous (unlike the wall →
    /// instant direction), so a DST fold is not a factor here; both server code generation and agent
    /// offline verification start from the same real instant and the same company timezone.
    /// </summary>
    private static DateTimeOffset CompanyNow(Company company)
    {
        var zone = CompanyBusinessTime.Resolve(company.BusinessTimeZoneId);
        var businessLocal = CompanyBusinessTime.ToBusinessLocal(DateTimeOffset.UtcNow, zone);
        return new DateTimeOffset(businessLocal, TimeSpan.Zero);
    }

    private static string NormalizePurpose(string purpose)
    {
        if (purpose.Equals(TotpGenerator.PurposeUsb, StringComparison.OrdinalIgnoreCase))
        {
            return TotpGenerator.PurposeUsb;
        }

        if (purpose.Equals(TotpGenerator.PurposeUninstall, StringComparison.OrdinalIgnoreCase))
        {
            return TotpGenerator.PurposeUninstall;
        }

        throw new InvalidOperationException("purpose must be 'usb' or 'uninstall'.");
    }

    private async Task EnsureStaffTotpAsync(UserAccount user, string totpCode, string purpose, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (user.AccessTotpLockoutUntil is DateTimeOffset lockoutUntil)
        {
            if (lockoutUntil > now)
            {
                RecordDenial(user, purpose, "locked_out");
                throw new UnauthorizedAccessException("TOTP verification locked due to too many failed attempts.");
            }

            // Lockout expired: clear the counter. Leaving attempts ≥ Max would re-lock
            // on the next single failure (remote permanent DoS via anonymous verify).
            user.AccessTotpFailedAttempts = 0;
            user.AccessTotpLockoutUntil = null;
        }

        // §6.1/§6.4 — derive the machine's secret on demand from its device key (no stored secret),
        // and verify against COMPANY-LOCAL time (§6.3), the same frame the agent verifies against
        // offline. `matchedStep` is therefore a company-local step, consistent with the replay
        // columns below. Lockout timestamps stay real-time UTC.
        var companyNow = CompanyNow(user.Company);
        var purposeSecret = TotpGenerator.DeriveMachineSecret(user.DeviceKey, purpose);
        if (!TotpGenerator.VerifyCode(purposeSecret, totpCode.Trim(), window: 1, companyNow, out var matchedStep))
        {
            user.AccessTotpFailedAttempts += 1;
            if (user.AccessTotpFailedAttempts >= MaxTotpFailedAttempts)
            {
                user.AccessTotpLockoutUntil = now.Add(TotpLockoutDuration);
            }

            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "TOTP verification failed for user {UserId} purpose {Purpose} (attempt {Attempt})",
                user.Id,
                purpose,
                user.AccessTotpFailedAttempts);
            RecordDenial(user, purpose, "invalid_code");
            throw new UnauthorizedAccessException("Invalid TOTP code.");
        }

        var lastUsedStep = purpose == TotpGenerator.PurposeUsb
            ? user.AccessTotpLastUsedStepUsb
            : user.AccessTotpLastUsedStepUninstall;
        if (matchedStep <= lastUsedStep)
        {
            logger.LogWarning(
                "TOTP replay rejected for user {UserId} purpose {Purpose} step {Step}",
                user.Id,
                purpose,
                matchedStep);
            RecordDenial(user, purpose, "replayed_code");
            throw new UnauthorizedAccessException("TOTP code already used.");
        }

        user.AccessTotpFailedAttempts = 0;
        user.AccessTotpLockoutUntil = null;
        if (purpose == TotpGenerator.PurposeUsb)
        {
            user.AccessTotpLastUsedStepUsb = matchedStep;
        }
        else
        {
            user.AccessTotpLastUsedStepUninstall = matchedStep;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "TOTP verified for user {UserId} purpose {Purpose} step {Step}",
            user.Id,
            purpose,
            matchedStep);
    }

    private void RecordDenial(UserAccount user, string purpose, string reason)
        => audit.Record(
            purpose == TotpGenerator.PurposeUsb
                ? AuditActions.UsbApprovalDenied
                : AuditActions.UninstallApprovalDenied,
            user.Id,
            user.CompanyId,
            new { deviceUserId = user.Id, reason });

    /// <summary>
    /// The staff machine presenting a code, with its company clock loaded for the §6.3 company-local
    /// step. Staff only: an admin machine is not monitored and removes its own service (§8.2), so it
    /// has no USB gate and needs no uninstall code.
    /// </summary>
    private async Task<UserAccount> RequireApprovalDeviceAsync(string deviceKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            throw new InvalidOperationException("deviceKey is required.");
        }

        var normalized = deviceKey.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.DeviceKey == normalized, ct)
            ?? throw new UnauthorizedAccessException("Unknown device.");

        if (user.Role != UserRole.Staff)
        {
            throw new InvalidOperationException("Access gate applies to staff agents only.");
        }

        return user;
    }

    private async Task<UserAccount> RequireTotpGeneratorAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new SessionInvalidException("User not found.");
        if (!(await access.GetAsync(userId, ct)).CanGenerateTotp)
        {
            throw new UnauthorizedAccessException("Missing authority package: usb_approval or uninstall_approval");
        }

        return user;
    }

    /// <summary>
    /// The staff account an approval code may be issued for (§1.5 — staff only). Company-scoped and
    /// never the admin's own machine, which is not monitored and self-removes (§8.2). The company is
    /// loaded because the code's step runs on the company clock (§6.3).
    /// </summary>
    private async Task<UserAccount> RequireApprovalTargetAsync(
        UserAccount actor, Guid targetUserId, CancellationToken ct)
    {
        var target = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");

        if (target.CompanyId != actor.CompanyId || target.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("Staff is not in your company.");
        }

        return target;
    }

    private static string NewTicket()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashTicket(string ticket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ticket));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
