using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public sealed record TotpEnrollDto(
    Guid StaffUserId,
    string StaffUsername,
    string Secret,
    string OtpAuthUri,
    bool Enabled);

public sealed record TotpStatusDto(
    Guid StaffUserId,
    string StaffUsername,
    bool Enabled,
    DateTimeOffset? EnrolledAt);

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
    Task<TotpEnrollDto> EnrollTotpAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
    Task<IReadOnlyList<TotpStatusDto>> ListStaffTotpAsync(Guid adminUserId, CancellationToken ct);
    Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
    Task<TotpCodeDto> GetTotpCodeAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct);
    Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct);
    Task<bool> ConsumeUninstallTicketAsync(string ticket, CancellationToken ct);
    Task<UsbApproveDto> VerifyUsbAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct);
    Task<bool> ConsumeUsbTicketAsync(string ticket, CancellationToken ct);
    Task HeartbeatAsync(Guid userId, CancellationToken ct);
}

public sealed class LifecycleService(AppDbContext db, IAuthorityService authorities) : ILifecycleService
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UsbTicketLifetime = TimeSpan.FromMinutes(5);

    public async Task<TotpEnrollDto> EnrollTotpAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        var staff = await RequireCompanyStaffAsync(admin.CompanyId, staffUserId, ct);

        var secret = TotpGenerator.GenerateSecret();
        staff.AccessTotpSecret = secret;
        staff.AccessTotpEnabled = true;
        staff.AccessTotpEnrolledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var account = $"{admin.Company.Name}:{staff.Username}";
        var uri = TotpGenerator.BuildOtpAuthUri(secret, "Teamscop", account);
        return new TotpEnrollDto(staff.Id, staff.Username, secret, uri, true);
    }

    public async Task<IReadOnlyList<TotpStatusDto>> ListStaffTotpAsync(Guid adminUserId, CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == actor.CompanyId && u.Role == UserRole.Staff)
            .OrderBy(u => u.Username)
            .Select(u => new TotpStatusDto(u.Id, u.Username, u.AccessTotpEnabled, u.AccessTotpEnrolledAt))
            .ToListAsync(ct);
    }

    public async Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        var staff = await RequireCompanyStaffAsync(actor.CompanyId, staffUserId, ct);
        return new TotpStatusDto(staff.Id, staff.Username, staff.AccessTotpEnabled, staff.AccessTotpEnrolledAt);
    }

    public async Task<TotpCodeDto> GetTotpCodeAsync(Guid adminUserId, Guid staffUserId, CancellationToken ct)
    {
        var actor = await RequireTotpGeneratorAsync(adminUserId, ct);
        var staff = await RequireCompanyStaffAsync(actor.CompanyId, staffUserId, ct);
        if (!staff.AccessTotpEnabled || string.IsNullOrWhiteSpace(staff.AccessTotpSecret))
        {
            throw new InvalidOperationException("Staff TOTP is not enrolled. Enroll first.");
        }

        var now = DateTimeOffset.UtcNow;
        var code = TotpGenerator.ComputeCode(staff.AccessTotpSecret, now);
        var remaining = TotpGenerator.PeriodSeconds - (int)(now.ToUnixTimeSeconds() % TotpGenerator.PeriodSeconds);
        return new TotpCodeDto(staff.Id, staff.Username, code, TotpGenerator.PeriodSeconds, remaining);
    }

    public async Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct)
    {
        var user = await RequireStaffDeviceAsync(deviceKey, ct);
        EnsureStaffTotp(user, totpCode);

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
            return false;
        }

        entity.Consumed = true;
        if (entity.DeviceUserId is Guid staffUserId)
        {
            var now = DateTimeOffset.UtcNow;
            db.AgentEvents.Add(new AgentEvent
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
            });
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UsbApproveDto> VerifyUsbAsync(
        string deviceKey,
        string totpCode,
        string? deviceInstanceId,
        CancellationToken ct)
    {
        var user = await RequireStaffDeviceAsync(deviceKey, ct);
        EnsureStaffTotp(user, totpCode);

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
            return false;
        }

        entity.Consumed = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task HeartbeatAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        user.LastHeartbeatAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static void EnsureStaffTotp(UserAccount user, string totpCode)
    {
        if (!user.AccessTotpEnabled || string.IsNullOrWhiteSpace(user.AccessTotpSecret))
        {
            throw new InvalidOperationException("Staff access TOTP is not enrolled. Ask admin to enroll for this staff.");
        }

        if (!TotpGenerator.VerifyCode(user.AccessTotpSecret, totpCode.Trim()))
        {
            throw new UnauthorizedAccessException("Invalid TOTP code.");
        }
    }

    private async Task<UserAccount> RequireStaffDeviceAsync(string deviceKey, CancellationToken ct)
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

    private async Task<UserAccount> RequireAdminAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can enroll staff TOTP.");
        }

        return admin;
    }

    private async Task<UserAccount> RequireTotpGeneratorAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (!await authorities.CanGenerateTotpAsync(userId, ct))
        {
            throw new UnauthorizedAccessException("Missing authority package: usb_approval or uninstall_approval");
        }

        return user;
    }

    private async Task<UserAccount> RequireCompanyStaffAsync(Guid companyId, Guid staffUserId, CancellationToken ct)
    {
        var staff = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");
        if (staff.CompanyId != companyId || staff.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("Staff is not in your company.");
        }

        return staff;
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
