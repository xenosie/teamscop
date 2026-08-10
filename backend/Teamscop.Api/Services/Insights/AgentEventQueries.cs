using Teamscop.Api.Data;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// The filter clauses every read-side service composes. Written once so the inclusive/exclusive
/// period semantics cannot drift between the screenshot, browsing, timetrack and event views.
/// </summary>
public static class AgentEventQueries
{
    /// <summary>
    /// The widest period any read may span. Data expires after 30 days (§13.2), so a wider
    /// request can only aggregate emptiness — while still costing a full scan to find it.
    /// </summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(31);

    public static IQueryable<AgentEvent> ForStaff(this IQueryable<AgentEvent> q, Guid staffUserId)
        => q.Where(e => e.UserId == staffUserId);

    public static IQueryable<AgentEvent> OfType(this IQueryable<AgentEvent> q, string eventType)
        => q.Where(e => e.EventType == eventType);

    public static IQueryable<AgentEvent> OfTypes(this IQueryable<AgentEvent> q, IReadOnlyCollection<string> eventTypes)
        => q.Where(e => eventTypes.Contains(e.EventType));

    /// <summary>
    /// <paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive — the end of a
    /// period is the next local midnight, which belongs to the following day.
    /// </summary>
    public static IQueryable<AgentEvent> InPeriod(
        this IQueryable<AgentEvent> q,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        if (from is not null)
        {
            q = q.Where(e => e.OccurredAt >= from);
        }

        if (to is not null)
        {
            q = q.Where(e => e.OccurredAt < to);
        }

        return q;
    }

    public static IQueryable<AgentEvent> Newest(this IQueryable<AgentEvent> q)
        => q.OrderByDescending(e => e.OccurredAt);

    public static IQueryable<AgentEvent> Newest(this IQueryable<AgentEvent> q, int take)
        => q.Newest().Take(take);

    public static IQueryable<AgentEvent> Oldest(this IQueryable<AgentEvent> q)
        => q.OrderBy(e => e.OccurredAt);
}
