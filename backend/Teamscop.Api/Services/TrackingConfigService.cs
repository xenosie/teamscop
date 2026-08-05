using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services;

public interface ITrackingConfigService
{
    Task<StaffTrackingConfig> GetForStaffAsync(Guid staffUserId, CancellationToken ct);
    Task<StaffTrackingConfig> UpsertByAdminAsync(Guid adminUserId, Guid staffUserId, StaffTrackingConfig update, CancellationToken ct);
}

public sealed class TrackingConfigService(
    AppDbContext db,
    IHubContext<ConfigHub> hub) : ITrackingConfigService
{
    public async Task<StaffTrackingConfig> GetForStaffAsync(Guid staffUserId, CancellationToken ct)
    {
        var entity = await db.StaffTrackingConfigs.FirstOrDefaultAsync(c => c.StaffUserId == staffUserId, ct);
        if (entity is null)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
                ?? throw new UnauthorizedAccessException("User not found.");
            entity = DefaultEntity(user);
            db.StaffTrackingConfigs.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        return ToDto(entity);
    }

    public async Task<StaffTrackingConfig> UpsertByAdminAsync(
        Guid adminUserId,
        Guid staffUserId,
        StaffTrackingConfig update,
        CancellationToken ct)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new UnauthorizedAccessException("Admin not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can configure staff tracking.");
        }

        var staff = await db.Users.FirstOrDefaultAsync(u => u.Id == staffUserId, ct)
            ?? throw new InvalidOperationException("Staff user not found.");
        if (staff.CompanyId != admin.CompanyId || staff.Role != UserRole.Staff)
        {
            throw new UnauthorizedAccessException("Staff not in your company.");
        }

        if (update.ScreenshotPeriodSeconds < 30 || update.ScreenshotPeriodSeconds > 86_400)
        {
            throw new InvalidOperationException("Screenshot period must be between 30s and 24h.");
        }

        var entity = await db.StaffTrackingConfigs.FirstOrDefaultAsync(c => c.StaffUserId == staffUserId, ct);
        if (entity is null)
        {
            entity = DefaultEntity(staff);
            db.StaffTrackingConfigs.Add(entity);
        }

        entity.ScreenshotQuality = update.ScreenshotQuality.ToString();
        entity.ScreenshotPeriodSeconds = update.ScreenshotPeriodSeconds;
        entity.TimeTrackEnabled = update.TimeTrackEnabled;
        entity.BrowserHistoryEnabled = update.BrowserHistoryEnabled;
        entity.ScreenshotEnabled = update.ScreenshotEnabled;
        entity.ConfigVersion += 1;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var dto = ToDto(entity);
        await hub.Clients.Group(ConfigHub.StaffGroup(staffUserId))
            .SendAsync("TrackingConfigUpdated", dto, ct);
        return dto;
    }

    private static StaffTrackingConfigEntity DefaultEntity(UserAccount staff) => new()
    {
        StaffUserId = staff.Id,
        CompanyId = staff.CompanyId,
        ScreenshotQuality = nameof(ScreenshotQuality.Medium),
        ScreenshotPeriodSeconds = 300,
        TimeTrackEnabled = true,
        BrowserHistoryEnabled = true,
        ScreenshotEnabled = true,
        ConfigVersion = 1,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static StaffTrackingConfig ToDto(StaffTrackingConfigEntity e) => new()
    {
        StaffUserId = e.StaffUserId,
        ScreenshotQuality = Enum.TryParse<ScreenshotQuality>(e.ScreenshotQuality, true, out var q)
            ? q
            : ScreenshotQuality.Medium,
        ScreenshotPeriodSeconds = e.ScreenshotPeriodSeconds,
        TimeTrackEnabled = e.TimeTrackEnabled,
        BrowserHistoryEnabled = e.BrowserHistoryEnabled,
        ScreenshotEnabled = e.ScreenshotEnabled,
        ConfigVersion = e.ConfigVersion,
        UpdatedAt = e.UpdatedAt
    };
}
