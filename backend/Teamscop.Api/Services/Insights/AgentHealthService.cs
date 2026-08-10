using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// The thresholds the overview, the gap query and the sticker all judge an agent by.
/// Stated once so "offline" cannot mean two different things in two views.
/// </summary>
public static class TrackingHealth
{
    /// <summary>No heartbeat for this long and the agent is not reporting.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Shorter holes than this are flush jitter, not data loss: the agent closes a timetrack
    /// window every 60 s, so a single missed flush must not become a gap marker (§12.4).
    /// </summary>
    public static readonly TimeSpan MinReportableGap = TimeSpan.FromMinutes(2);

    /// <summary>Queued events above this and the agent is behind rather than broken.</summary>
    public const int CatchingUpOutbox = 50;

    public const string StatusUnknown = "unknown";
    public const string StatusNotReporting = "not_reporting";
    public const string StatusCatchingUp = "catching_up";
    public const string StatusProtected = "protected";
}

/// <summary>
/// What the staff sticker needs to prove the engine is alive (§14.4). Carries no captured data,
/// only liveness — which is why a staff member may read their own (§4.5's sticker exception).
/// </summary>
public sealed record AgentSelfHealthDto(
    Guid UserId,
    string Status,
    string? StatusDetail,
    bool AgentOffline,
    bool? Online,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastTimeTrackAt,
    bool? HelperAlive,
    bool? TrackingOk,
    int? PendingOutbox);

public interface IAgentHealthService
{
    Task<AgentSelfHealthDto> GetSelfAsync(Guid userId, CancellationToken ct);
}

public sealed class AgentHealthService(AppDbContext db) : IAgentHealthService
{
    public async Task<AgentSelfHealthDto> GetSelfAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LastHeartbeatAt, u.LastSeenAt, u.LastOnline })
            .FirstOrDefaultAsync(ct)
            ?? throw new SessionInvalidException("User not found.");

        var payload = await db.AgentEvents.AsNoTracking()
            .ForStaff(userId)
            .OfType(AgentEventTypes.Heartbeat)
            .Newest()
            .Select(e => e.PayloadJson)
            .FirstOrDefaultAsync(ct);

        var lastTimeTrackAt = await db.AgentEvents.AsNoTracking()
            .ForStaff(userId)
            .OfType(AgentEventTypes.TimeTrack)
            .Newest()
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(ct);

        bool? helperAlive = null;
        bool? trackingOk = null;
        int? pending = null;
        using (var doc = AgentEventPayload.TryOpen(payload))
        {
            if (doc is not null)
            {
                var root = doc.RootElement;
                helperAlive = AgentEventPayload.Bool(root, "helperAlive", "HelperAlive");
                trackingOk = AgentEventPayload.Bool(root, "trackingOk", "TrackingOk");
                if (AgentEventPayload.Long(root, "pending", "Pending", "pendingOutbox", "PendingOutbox") is { } queued)
                {
                    pending = (int)Math.Clamp(queued, 0, int.MaxValue);
                }
            }
        }

        var offline = user.LastHeartbeatAt is null
                      || DateTimeOffset.UtcNow - user.LastHeartbeatAt.Value > TrackingHealth.OfflineAfter;
        var (status, detail) = Classify(user.LastHeartbeatAt, offline, helperAlive, trackingOk, pending);

        return new AgentSelfHealthDto(
            userId,
            status,
            detail,
            offline,
            user.LastOnline,
            user.LastHeartbeatAt,
            user.LastSeenAt,
            lastTimeTrackAt,
            helperAlive,
            trackingOk,
            pending);
    }

    private static (string Status, string? Detail) Classify(
        DateTimeOffset? lastHeartbeatAt,
        bool offline,
        bool? helperAlive,
        bool? trackingOk,
        int? pending)
    {
        if (lastHeartbeatAt is null)
        {
            return (TrackingHealth.StatusUnknown, "No heartbeat received yet.");
        }

        if (offline)
        {
            return (TrackingHealth.StatusNotReporting, "No recent heartbeat.");
        }

        if (helperAlive == false)
        {
            return (TrackingHealth.StatusNotReporting, "Tracking helper not running.");
        }

        if (trackingOk == false)
        {
            return (TrackingHealth.StatusNotReporting, "Tracking tasks not healthy.");
        }

        if (pending > TrackingHealth.CatchingUpOutbox)
        {
            return (TrackingHealth.StatusCatchingUp, $"{pending} events still queued.");
        }

        return (TrackingHealth.StatusProtected, null);
    }
}
