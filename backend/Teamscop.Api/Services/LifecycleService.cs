using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Services;

public sealed record TotpEnrollDto(string Secret, string OtpAuthUri, bool Enabled);
public sealed record TotpStatusDto(bool Enabled, DateTimeOffset? EnrolledAt);
public sealed record UninstallTicketDto(string UninstallTicket, long ExpiresIn);

public interface ILifecycleService
{
    Task<TotpEnrollDto> EnrollTotpAsync(Guid adminUserId, CancellationToken ct);
    Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, CancellationToken ct);
    Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct);
    Task<bool> ConsumeUninstallTicketAsync(string ticket, CancellationToken ct);
    Task HeartbeatAsync(Guid userId, CancellationToken ct);
}

public sealed class LifecycleService(AppDbContext db) : ILifecycleService
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    public async Task<TotpEnrollDto> EnrollTotpAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        var company = admin.Company;
        var secret = TotpGenerator.GenerateSecret();
        company.UninstallTotpSecret = secret;
        company.UninstallTotpEnabled = true;
        company.UninstallTotpEnrolledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var uri = TotpGenerator.BuildOtpAuthUri(secret, "Teamscop", company.Name);
        return new TotpEnrollDto(secret, uri, true);
    }

    public async Task<TotpStatusDto> GetTotpStatusAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminUserId, ct);
        return new TotpStatusDto(admin.Company.UninstallTotpEnabled, admin.Company.UninstallTotpEnrolledAt);
    }

    public async Task<UninstallTicketDto> VerifyUninstallAsync(string deviceKey, string totpCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(totpCode))
        {
            throw new InvalidOperationException("deviceKey and totpCode are required.");
        }

        var normalized = deviceKey.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.DeviceKey == normalized, ct)
            ?? throw new UnauthorizedAccessException("Unknown device.");

        if (user.Role != UserRole.Staff)
        {
            throw new InvalidOperationException("TOTP uninstall gate applies to staff agents only.");
        }

        var company = user.Company;
        if (!company.UninstallTotpEnabled || string.IsNullOrWhiteSpace(company.UninstallTotpSecret))
        {
            throw new InvalidOperationException("Company uninstall TOTP is not enrolled. Ask admin to enroll first.");
        }

        if (!TotpGenerator.VerifyCode(company.UninstallTotpSecret, totpCode.Trim()))
        {
            throw new UnauthorizedAccessException("Invalid TOTP code.");
        }

        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entity = new UninstallTicket
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            DeviceUserId = user.Id,
            TicketHash = HashTicket(ticket),
            ExpiresAt = DateTimeOffset.UtcNow.Add(TicketLifetime),
            CreatedAt = DateTimeOffset.UtcNow,
            Consumed = false
        };
        db.UninstallTickets.Add(entity);
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

    private async Task<UserAccount> RequireAdminAsync(Guid adminUserId, CancellationToken ct)
    {
        var admin = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can manage uninstall TOTP.");
        }

        return admin;
    }

    private static string HashTicket(string ticket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ticket));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
