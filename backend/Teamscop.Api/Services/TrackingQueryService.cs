using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services;

public sealed class TrackingEventDto
{
    public Guid Id { get; set; }
    public Guid StaffUserId { get; set; }
    public string StaffUsername { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// Company-local wall clock (§8.2), computed from <see cref="OccurredAt"/> and the
    /// company timezone at projection time. Kind is Unspecified — it is a clock face,
    /// not an instant.
    /// </summary>
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
}

public interface ITrackingQueryService
{
    Task<IReadOnlyList<TrackingEventDto>> QueryEventsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? eventType,
        int take,
        CancellationToken ct);

    Task<StaffTrackingConfig> GetConfigIfAllowedAsync(Guid viewerId, Guid staffUserId, CancellationToken ct);
}

public sealed class TrackingQueryService(
    AppDbContext db,
    IAccessPolicy access,
    ITrackingConfigService configs) : ITrackingQueryService
{
    public async Task<IReadOnlyList<TrackingEventDto>> QueryEventsAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? eventType,
        int take,
        CancellationToken ct)
    {
        await access.EnsureCanViewStaffAsync(viewerId, staffUserId, ct);

        var viewer = await access.GetAsync(viewerId, ct);
        // Scoped to THIS staff member: a leader-and-policeman may hold company-wide screenshots
        // (granted) yet team-only timetrack (inherent), and the target-blind set leaked the latter.
        var allowedTypes = viewer.ViewableEventTypesFor(staffUserId);

        var requestedType = string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim();
        if (requestedType is not null && !allowedTypes.Contains(requestedType))
        {
            throw new UnauthorizedAccessException($"Missing authority package for event type: {requestedType}");
        }

        take = Math.Clamp(take <= 0 ? 100 : take, 1, 500);
        var q = db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .InPeriod(from, to);
        q = requestedType is not null ? q.OfType(requestedType) : q.OfTypes(allowedTypes);

        var staff = await db.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId)
            .Select(u => new { u.Username, TimeZoneId = u.Company.BusinessTimeZoneId })
            .FirstAsync(ct);
        var zone = CompanyBusinessTime.Resolve(staff.TimeZoneId);

        var rows = await q.Newest(take)
            .Select(e => new
            {
                e.Id,
                e.UserId,
                e.EventType,
                e.OccurredAt,
                e.ReceivedAt,
                e.PayloadJson
            })
            .ToListAsync(ct);

        return rows.Select(e => new TrackingEventDto
        {
            Id = e.Id,
            StaffUserId = e.UserId,
            StaffUsername = staff.Username,
            EventType = e.EventType,
            OccurredAt = e.OccurredAt,
            ReceivedAt = e.ReceivedAt,
            PayloadJson = e.PayloadJson,
            BusinessOccurredAt = CompanyBusinessTime.ToBusinessLocal(e.OccurredAt, zone),
            BusinessTimeZoneId = staff.TimeZoneId
        }).ToList();
    }

    public async Task<StaffTrackingConfig> GetConfigIfAllowedAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await access.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking config.");
        }

        return await configs.GetForStaffAsync(staffUserId, ct);
    }
}
