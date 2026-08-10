using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Services.Insights;

public sealed record LeaderboardRowDto(
    int Rank,
    Guid UserId,
    string Username,
    string? AvatarUrl,
    long WorkedSeconds,
    long IdleSeconds);

public sealed record LeaderboardPageDto(
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZoneId,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<LeaderboardRowDto> Rows);

public interface IWorkSummaryService
{
    Task<LeaderboardPageDto> GetLeaderboardAsync(
        Guid viewerId,
        DateTimeOffset from,
        DateTimeOffset to,
        int page,
        int pageSize,
        CancellationToken ct);
}

/// <summary>
/// The §14.2 leaderboard aggregate: worked / idle seconds per visible staff member over a period,
/// as one <c>SUM ... GROUP BY UserId</c>. Cost is fixed no matter how many staff there are — the
/// desktop app's rejected fallback was one request per member per minute (§15.2).
/// </summary>
public sealed class WorkSummaryService(AppDbContext db, IAccessPolicy access) : IWorkSummaryService
{
    private const int MaxPageSize = 100;

    public async Task<LeaderboardPageDto> GetLeaderboardAsync(
        Guid viewerId,
        DateTimeOffset from,
        DateTimeOffset to,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (to <= from)
        {
            throw new ArgumentException("'to' must be after 'from'.");
        }

        if (to - from > AgentEventQueries.MaxPeriod)
        {
            throw new ArgumentException(
                $"Period must not exceed {AgentEventQueries.MaxPeriod.TotalDays:0} days.");
        }

        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, MaxPageSize);

        var scope = await LoadScopeAsync(viewerId, ct);
        var totals = await AggregateAsync(scope.Staff, from, to, ct);

        // Ranking must consider every visible member, so the page is cut after ordering.
        // The set is 20-50 people (§15.1) — ordering it in memory is cheaper than a second query.
        var ranked = scope.Staff
            .Select(s =>
            {
                var t = Totals(totals, s.UserId);
                return (s.UserId, s.Username, s.AvatarUrl, Worked: (long)t.Worked, Idle: (long)t.Idle);
            })
            .OrderByDescending(r => r.Worked)
            .ThenBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
            .Select((r, i) => new LeaderboardRowDto(i + 1, r.UserId, r.Username, r.AvatarUrl, r.Worked, r.Idle))
            .ToList();

        return new LeaderboardPageDto(
            from,
            to,
            scope.TimeZoneId,
            page,
            pageSize,
            ranked.Count,
            ranked.Skip(page * pageSize).Take(pageSize).ToList());
    }

    private async Task<Scope> LoadScopeAsync(Guid viewerId, CancellationToken ct)
    {
        // Hours worked is timetrack data, so the timetrack package gates both views.
        // A team leader holds it inherently for their own team (§4.2).
        await access.EnsureHasAsync(viewerId, AuthorityPackageIds.ViewTimeTrack, ct);
        var viewer = await access.GetAsync(viewerId, ct);

        var timeZoneId = await db.Companies.AsNoTracking()
            .Where(c => c.Id == viewer.CompanyId)
            .Select(c => c.BusinessTimeZoneId)
            .FirstAsync(ct);

        // §4.5 — never yourself.
        var query = db.Users.AsNoTracking()
            .Where(u => u.CompanyId == viewer.CompanyId && u.Role == UserRole.Staff && u.Id != viewerId);
        // Tracking aggregates: company-wide only with a granted VIEW package. A team leader who
        // also holds usb_approval stays scoped to their own team.
        if (!viewer.CanViewCompanyData)
        {
            var ledIds = viewer.LedMemberIds.ToList();
            query = query.Where(u => ledIds.Contains(u.Id));
        }

        var staff = await query
            .OrderBy(u => u.Username)
            .Select(u => new StaffRow(u.Id, u.Username, u.AvatarUrl))
            .ToListAsync(ct);

        return new Scope(timeZoneId, CompanyBusinessTime.Resolve(timeZoneId), staff);
    }

    private async Task<Dictionary<Guid, WorkTotals>> AggregateAsync(
        List<StaffRow> staff,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        if (staff.Count == 0)
        {
            return [];
        }

        var ids = staff.Select(s => s.UserId).ToList();
        var rows = await db.AgentEvents.AsNoTracking()
            .SummarizeWork(ids, from, to)
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.UserId);
    }

    /// <summary>A staff member with no timetrack row in the period simply has zero of everything.</summary>
    private static WorkTotals Totals(Dictionary<Guid, WorkTotals> totals, Guid userId)
        => totals.TryGetValue(userId, out var hit) ? hit : new WorkTotals { UserId = userId };

    private sealed record StaffRow(Guid UserId, string Username, string? AvatarUrl);

    private sealed record Scope(string TimeZoneId, TimeZoneInfo Zone, List<StaffRow> Staff);
}

/// <summary>Per-staff worked/idle totals over one period, as the database returns them.</summary>
public sealed class WorkTotals
{
    public Guid UserId { get; set; }
    public int Worked { get; set; }
    public int Idle { get; set; }
}

public static class WorkSummaryQueries
{
    /// <summary>
    /// The whole leaderboard aggregate, in SQL: one <c>SUM ... GROUP BY UserId</c> over the
    /// denormalized timetrack columns. Kept as a composable extension so a translation test can
    /// assert it survives the PostgreSQL provider — the in-memory provider evaluates anything.
    /// </summary>
    public static IQueryable<WorkTotals> SummarizeWork(
        this IQueryable<AgentEvent> events,
        IReadOnlyCollection<Guid> staffUserIds,
        DateTimeOffset from,
        DateTimeOffset to)
        => events
            .Where(e => e.EventType == AgentEventTypes.TimeTrack
                        && staffUserIds.Contains(e.UserId)
                        && e.OccurredAt >= from
                        && e.OccurredAt < to)
            .GroupBy(e => e.UserId)
            .Select(g => new WorkTotals
            {
                UserId = g.Key,
                Worked = g.Sum(e => e.WorkedSeconds) ?? 0,
                Idle = g.Sum(e => e.IdleSeconds) ?? 0
            });
}
