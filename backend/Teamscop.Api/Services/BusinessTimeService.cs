using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Audit;
using Teamscop.Api.Data;
using Teamscop.Api.Hubs;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services;

/// <summary>Admin picks a timezone from a dropdown — that is the entire clock (§8.4).</summary>
public sealed class SetCompanyTimeZoneRequest
{
    public string TimeZoneId { get; set; } = "UTC";
}

public sealed record CompanyTimeZoneDto(
    Guid CompanyId,
    string TimeZoneId,
    string DisplayName,
    TimeSpan CurrentOffset);

public sealed record TimeZoneOptionDto(string Id, string DisplayName, TimeSpan CurrentOffset);

public interface IBusinessTimeService
{
    Task<CompanyTimeZoneDto> GetForCompanyAsync(Guid companyId, CancellationToken ct);
    Task<CompanyTimeZoneDto> GetForUserAsync(Guid userId, CancellationToken ct);
    Task<CompanyTimeZoneDto> SetTimeZoneAsync(Guid adminUserId, SetCompanyTimeZoneRequest request, CancellationToken ct);
    IReadOnlyList<TimeZoneOptionDto> ListTimeZones();
}

public sealed class BusinessTimeService(
    AppDbContext db,
    IHubContext<ConfigHub> hub,
    IAuditLog audit,
    ILogger<BusinessTimeService> logger) : IBusinessTimeService
{
    public async Task<CompanyTimeZoneDto> GetForCompanyAsync(Guid companyId, CancellationToken ct)
    {
        var row = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Id, c.BusinessTimeZoneId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Company not found.");
        return ToDto(row.Id, row.BusinessTimeZoneId);
    }

    public async Task<CompanyTimeZoneDto> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.CompanyId, u.Company.BusinessTimeZoneId })
            .FirstOrDefaultAsync(ct)
            ?? throw new SessionInvalidException("User not found.");
        return ToDto(row.CompanyId, row.BusinessTimeZoneId);
    }

    public async Task<CompanyTimeZoneDto> SetTimeZoneAsync(
        Guid adminUserId,
        SetCompanyTimeZoneRequest request,
        CancellationToken ct)
    {
        var admin = await db.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == adminUserId, ct)
            ?? throw new SessionInvalidException("Admin not found.");
        if (admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Only admins can set the company timezone.");
        }

        if (string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            throw new InvalidOperationException("TimeZoneId is required.");
        }

        var id = request.TimeZoneId.Trim();
        if (!CompanyBusinessTime.TryResolve(id, out _))
        {
            throw new InvalidOperationException($"Unknown timezone id: {id}");
        }

        var company = admin.Company;
        var previous = company.BusinessTimeZoneId;
        company.BusinessTimeZoneId = id;
        await db.SaveChangesAsync(ct);

        audit.Record(AuditActions.CompanyTimeZoneChanged, admin.Id, company.Id,
            new { from = previous, to = id });
        // §8.2: every stored instant is now read through a different clock face.
        logger.LogInformation("Company {CompanyId} business timezone {From} -> {To}", company.Id, previous, id);

        var dto = ToDto(company.Id, company.BusinessTimeZoneId);
        await hub.Clients.Group(ConfigHub.CompanyGroup(company.Id))
            .SendAsync("BusinessTimeUpdated", dto, ct);
        return dto;
    }

    public IReadOnlyList<TimeZoneOptionDto> ListTimeZones()
    {
        var now = DateTime.UtcNow;
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new TimeZoneOptionDto(tz.Id, tz.DisplayName, tz.GetUtcOffset(now)))
            .OrderBy(o => o.CurrentOffset)
            .ThenBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CompanyTimeZoneDto ToDto(Guid companyId, string? timeZoneId)
    {
        var zone = CompanyBusinessTime.Resolve(timeZoneId);
        return new CompanyTimeZoneDto(
            companyId,
            string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId,
            zone.DisplayName,
            zone.GetUtcOffset(DateTime.UtcNow));
    }
}
