using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services;

public sealed class DeclareBusinessTimeRequest
{
    public string TimeZoneId { get; set; } = "UTC";
    public int Year { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int Second { get; set; }
}

public interface IBusinessTimeService
{
    Task<BusinessClockConfig> GetForCompanyAsync(Guid companyId, CancellationToken ct);
    Task<BusinessClockConfig> GetForUserAsync(Guid userId, CancellationToken ct);
    Task<BusinessClockConfig> DeclareSyncAsync(Guid adminUserId, DeclareBusinessTimeRequest request, CancellationToken ct);
}

public sealed class BusinessTimeService(
    AppDbContext db,
    IHubContext<ConfigHub> hub) : IBusinessTimeService
{
    public async Task<BusinessClockConfig> GetForCompanyAsync(Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new InvalidOperationException("Company not found.");
        return ToDto(company);
    }

    public async Task<BusinessClockConfig> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");
        return await GetForCompanyAsync(user.CompanyId, ct);
    }

    public async Task<BusinessClockConfig> DeclareSyncAsync(
        Guid adminUserId,
        DeclareBusinessTimeRequest request,
        CancellationToken ct)
    {
        var admin = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new UnauthorizedAccessException("Admin not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can declare business time.");
        }

        ValidateRequest(request);
        // Ensure timezone is resolvable (throws/falls back inside ResolveTimeZone).
        _ = BusinessClock.ResolveTimeZone(request.TimeZoneId);

        var company = admin.Company;
        var nowUtc = DateTimeOffset.UtcNow;
        company.BusinessTimeZoneId = request.TimeZoneId.Trim();
        company.BusinessClockSynchronized = true;
        company.BusinessAnchorUtc = nowUtc;
        company.BusinessAnchorYear = request.Year;
        company.BusinessAnchorMonth = request.Month;
        company.BusinessAnchorDay = request.Day;
        company.BusinessAnchorHour = request.Hour;
        company.BusinessAnchorMinute = request.Minute;
        company.BusinessAnchorSecond = request.Second;
        company.BusinessClockVersion += 1;
        company.BusinessClockUpdatedAt = nowUtc;
        await db.SaveChangesAsync(ct);

        var dto = ToDto(company);
        await hub.Clients.Group(ConfigHub.CompanyGroup(company.Id))
            .SendAsync("BusinessTimeUpdated", dto, ct);
        return dto;
    }

    private static void ValidateRequest(DeclareBusinessTimeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            throw new InvalidOperationException("TimeZoneId is required.");
        }

        try
        {
            _ = new DateTime(request.Year, request.Month, request.Day, request.Hour, request.Minute, request.Second);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("Invalid business date/time components.");
        }
    }

    private static BusinessClockConfig ToDto(Company c) => new()
    {
        CompanyId = c.Id,
        TimeZoneId = string.IsNullOrWhiteSpace(c.BusinessTimeZoneId) ? "UTC" : c.BusinessTimeZoneId,
        ClockVersion = c.BusinessClockVersion,
        IsSynchronized = c.BusinessClockSynchronized,
        AnchorUtc = c.BusinessAnchorUtc,
        AnchorYear = c.BusinessAnchorYear,
        AnchorMonth = c.BusinessAnchorMonth,
        AnchorDay = c.BusinessAnchorDay,
        AnchorHour = c.BusinessAnchorHour,
        AnchorMinute = c.BusinessAnchorMinute,
        AnchorSecond = c.BusinessAnchorSecond,
        UpdatedAt = c.BusinessClockUpdatedAt ?? c.CreatedAt
    };
}
