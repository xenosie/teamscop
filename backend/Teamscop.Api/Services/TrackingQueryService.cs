using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
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
    public long? VaultSequence { get; set; }
    public string? ChainHash { get; set; }
    public DateTime? BusinessOccurredAt { get; set; }
    public string? BusinessTimeZoneId { get; set; }
    public long? BusinessClockVersion { get; set; }
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
    IAuthorityService authorities,
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
        if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var t = eventType.Trim();
            if (!await authorities.CanViewEventTypeAsync(viewerId, t, ct))
            {
                throw new UnauthorizedAccessException($"Missing authority package for event type: {t}");
            }
        }

        take = Math.Clamp(take <= 0 ? 100 : take, 1, 500);
        var q = db.AgentEvents.AsNoTracking().Where(e => e.UserId == staffUserId);
        if (from is not null)
        {
            q = q.Where(e => e.OccurredAt >= from);
        }

        // `to` is exclusive (end of period = next local midnight / 24:00 of last day).
        if (to is not null)
        {
            q = q.Where(e => e.OccurredAt < to);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var t = eventType.Trim();
            q = q.Where(e => e.EventType == t);
        }

        var staffName = await db.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId)
            .Select(u => u.Username)
            .FirstAsync(ct);

        // Fetch a window then filter by package (keeps InMemory + PG simple).
        var rows = await q.OrderByDescending(e => e.OccurredAt)
            .Take(take * 3)
            .Select(e => new TrackingEventDto
            {
                Id = e.Id,
                StaffUserId = e.UserId,
                StaffUsername = staffName,
                EventType = e.EventType,
                OccurredAt = e.OccurredAt,
                ReceivedAt = e.ReceivedAt,
                PayloadJson = e.PayloadJson,
                VaultSequence = e.VaultSequence,
                ChainHash = e.ChainHash,
                BusinessOccurredAt = e.BusinessOccurredAt,
                BusinessTimeZoneId = e.BusinessTimeZoneId,
                BusinessClockVersion = e.BusinessClockVersion
            })
            .ToListAsync(ct);

        var allowed = new List<TrackingEventDto>(take);
        foreach (var row in rows)
        {
            if (await authorities.CanViewEventTypeAsync(viewerId, row.EventType, ct))
            {
                allowed.Add(row);
                if (allowed.Count >= take)
                {
                    break;
                }
            }
        }

        return allowed;
    }

    public async Task<StaffTrackingConfig> GetConfigIfAllowedAsync(Guid viewerId, Guid staffUserId, CancellationToken ct)
    {
        if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
        {
            throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking config.");
        }

        return await configs.GetForStaffAsync(staffUserId, ct);
    }
}
