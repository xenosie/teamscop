using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// One staff member's live state for the roster badge (§5.1): <c>working</c> (green),
/// <c>rest</c> (red) or <c>offline</c> (grey).
/// </summary>
/// <param name="State">
/// The legacy working / rest / offline activity badge. Unchanged, because the leaderboard and the
/// staff sticker both read it.
/// </param>
/// <param name="Status">
/// §14 — the four-state engine status: online, offline, broken, uninstalled. A different question
/// from <paramref name="State"/>: that one asks whether the employee is at the keyboard, this asks
/// whether the machine is reporting at all and whether anything is wrong with it.
/// </param>
public sealed record StaffPresenceDto(Guid UserId, string State, string? Status, string? StatusReason);

public interface IStaffPresenceService
{
    /// <summary>
    /// Presence for every staff member the caller may see on the roster — the same scope as
    /// <c>GET /api/tracking/staff</c>, so a badge appears for exactly the rows the caller already
    /// sees. Deliberately NOT gated on <c>view_timetrack</c>: a team leader sees their own team's
    /// badges without a package (§4.2), and the badge is coarse presence, not the timeline.
    /// </summary>
    Task<IReadOnlyList<StaffPresenceDto>> GetPresenceAsync(Guid viewerId, CancellationToken ct);
}

public sealed class StaffPresenceService(AppDbContext db, IAccessPolicy access) : IStaffPresenceService
{
    public const string Working = "working";
    public const string Rest = "rest";
    public const string Offline = "offline";

    public async Task<IReadOnlyList<StaffPresenceDto>> GetPresenceAsync(Guid viewerId, CancellationToken ct)
    {
        var viewer = await access.GetAsync(viewerId, ct);

        // Same scoping as TeamService.ListVisibleStaffAsync: company-wide with a granted view
        // package (or admin), otherwise the members of the team the caller leads. Never self (§4.5).
        var query = db.Users.AsNoTracking()
            .Where(u => u.CompanyId == viewer.CompanyId && u.Role == UserRole.Staff && u.Id != viewerId);
        if (!viewer.CanViewCompanyData)
        {
            var ledIds = viewer.LedMemberIds.ToList();
            if (ledIds.Count == 0)
            {
                return [];
            }

            query = query.Where(u => ledIds.Contains(u.Id));
        }

        var staff = await query
            .Select(u => new { u.Id, u.LastHeartbeatAt, u.AgentStatus, u.AgentStatusReason })
            .ToListAsync(ct);
        if (staff.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;

        // §5.2 — "working" is keyboard/mouse input inside the 3-minute idle window. A timetrack
        // window that closed within that window carrying worked time is exactly that signal.
        var activeSince = now - TrackingDefaults.IdleThreshold;
        var ids = staff.Select(s => s.Id).ToList();
        var workingIds = (await db.AgentEvents.AsNoTracking()
            .Where(e => e.EventType == AgentEventTypes.TimeTrack
                        && ids.Contains(e.UserId)
                        && e.OccurredAt >= activeSince
                        && (e.WorkedSeconds ?? 0) > 0)
            .Select(e => e.UserId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        return staff
            .Select(s => new StaffPresenceDto(
                s.Id,
                StateFor(s.LastHeartbeatAt, workingIds.Contains(s.Id), now),
                // Read from the column the status hosted service writes, never recomputed here, so
                // the badge and the app-history row behind it can never disagree.
                s.AgentStatus,
                s.AgentStatusReason))
            .ToList();
    }

    private static string StateFor(DateTimeOffset? lastHeartbeatAt, bool recentlyWorked, DateTimeOffset now)
    {
        // No heartbeat / PC off / agent not running → grey (§5.1). Same 2-minute threshold the
        // sticker and health use, so "offline" never means two different things (TrackingHealth).
        if (lastHeartbeatAt is null || now - lastHeartbeatAt.Value > TrackingHealth.OfflineAfter)
        {
            return Offline;
        }

        return recentlyWorked ? Working : Rest;
    }
}
