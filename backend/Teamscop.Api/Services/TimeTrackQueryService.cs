using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public sealed class TimeTrackTimelineDto
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public double TotalSeconds { get; set; }
    public List<TimeTrackSegmentDto> Segments { get; set; } = [];
}

public sealed class TimeTrackSegmentDto
{
    /// <summary>Not reporting while it should have been — PC off, asleep, or agent down. Drawn red.</summary>
    public const string GapKind = "gap";

    /// <summary>Outside the machine's lifetime: before it joined, or still in the future. Drawn as nothing.</summary>
    public const string UnknownKind = "unknown";

    /// <summary>working | rest | gap</summary>
    public string Kind { get; set; } = "gap";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public double DurationSeconds { get; set; }
}

public interface ITimeTrackQueryService
{
    Task<TimeTrackTimelineDto> GetTimelineAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}

public sealed class TimeTrackQueryService(
    AppDbContext db,
    IStaffDataGuard guard) : ITimeTrackQueryService
{
    public async Task<TimeTrackTimelineDto> GetTimelineAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        // §4.5's one exception: own rolling timeline feeds the staff sticker, no package needed.
        await guard.RequireViewableAsync(viewerId, staffUserId, AgentEventTypes.TimeTrack, allowSelf: true, ct);

        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (to <= from)
        {
            throw new ArgumentException("'to' must be after 'from'.");
        }

        // OccurredAt is typically the segment end; pull a small lead-in so
        // segments that started before `from` but overlap the window are included.
        var loadFrom = from.AddHours(-6);
        var rows = await db.AgentEvents.AsNoTracking()
            .ForStaff(staffUserId)
            .OfType(AgentEventTypes.TimeTrack)
            .InPeriod(loadFrom, to)
            .Oldest()
            .Select(e => e.PayloadJson)
            .ToListAsync(ct);

        var raw = new List<TimeTrackSegment>(rows.Count);
        foreach (var payload in rows)
        {
            if (TimeTrackSegmentReader.TryRead(payload, out var seg))
            {
                raw.Add(seg);
            }
        }

        // When this machine joined. Before that instant there was nothing to record, so the bar must
        // not claim the employee was resting — it has no opinion at all.
        var joinedAt = await db.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId)
            .Select(u => (DateTimeOffset?)u.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var clipped = ClipAndMerge(raw, from, to);
        var covering = FillGaps(clipped, from, to, joinedAt, DateTimeOffset.UtcNow);
        var total = (to - from).TotalSeconds;

        return new TimeTrackTimelineDto
        {
            From = from,
            To = to,
            TotalSeconds = total,
            Segments = covering
        };
    }

    private static List<TimeTrackSegmentDto> ClipAndMerge(
        List<TimeTrackSegment> raw,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var clipped = new List<TimeTrackSegment>();
        foreach (var seg in raw.OrderBy(s => s.Start))
        {
            var start = seg.Start < from ? from : seg.Start;
            var end = seg.End > to ? to : seg.End;
            if (end <= start)
            {
                continue;
            }

            clipped.Add(seg with { Start = start, End = end });
        }

        if (clipped.Count == 0)
        {
            return [];
        }

        var merged = new List<TimeTrackSegmentDto>();
        var cur = clipped[0];
        for (var i = 1; i < clipped.Count; i++)
        {
            var next = clipped[i];
            // Merge overlapping or touching same-kind segments.
            if (next.Working == cur.Working && next.Start <= cur.End.AddSeconds(1))
            {
                if (next.End > cur.End)
                {
                    cur = cur with { End = next.End };
                }

                continue;
            }

            merged.Add(ToDto(cur));
            cur = next;
        }

        merged.Add(ToDto(cur));
        return merged;
    }

    /// <summary>
    /// Fills everything the recorded segments do not cover, distinguishing two very different kinds
    /// of "no data".
    ///
    /// A gap means the agent should have been reporting and was not — the PC was off or asleep, or
    /// the agent was not running. That is real information and stays red.
    ///
    /// Time outside the machine's lifetime is not. Before <paramref name="knownFrom"/> the machine
    /// had not joined yet, and after <paramref name="knownTo"/> — now — it has not happened. Painting
    /// those red said the employee spent the rest of today idle, and on the day someone joined it
    /// claimed they had been resting since midnight. Both are unknown, and unknown is drawn as
    /// nothing at all.
    /// </summary>
    private static List<TimeTrackSegmentDto> FillGaps(
        List<TimeTrackSegmentDto> coverage,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset? knownFrom,
        DateTimeOffset knownTo)
    {
        var result = new List<TimeTrackSegmentDto>();
        var cursor = from;
        foreach (var seg in coverage.OrderBy(s => s.Start))
        {
            if (seg.Start > cursor)
            {
                AddUncovered(result, cursor, seg.Start, knownFrom, knownTo);
            }

            var start = seg.Start < cursor ? cursor : seg.Start;
            if (seg.End > start)
            {
                result.Add(new TimeTrackSegmentDto
                {
                    Kind = seg.Kind,
                    Start = start,
                    End = seg.End,
                    DurationSeconds = (seg.End - start).TotalSeconds
                });
                cursor = seg.End > cursor ? seg.End : cursor;
            }
        }

        if (cursor < to)
        {
            AddUncovered(result, cursor, to, knownFrom, knownTo);
        }

        if (result.Count == 0)
        {
            AddUncovered(result, from, to, knownFrom, knownTo);
        }

        return result;
    }

    /// <summary>
    /// Splits an uncovered span into up to three pieces: unknown before the machine joined, a real
    /// gap while it should have been reporting, and unknown for anything still in the future.
    /// </summary>
    private static void AddUncovered(
        List<TimeTrackSegmentDto> result,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? knownFrom,
        DateTimeOffset knownTo)
    {
        if (end <= start)
        {
            return;
        }

        // Before the machine existed.
        if (knownFrom is { } joined && start < joined)
        {
            var boundary = joined < end ? joined : end;
            Add(result, TimeTrackSegmentDto.UnknownKind, start, boundary);
            start = boundary;
            if (end <= start)
            {
                return;
            }
        }

        // Still in the future.
        if (end > knownTo)
        {
            var boundary = knownTo > start ? knownTo : start;
            Add(result, TimeTrackSegmentDto.GapKind, start, boundary);
            Add(result, TimeTrackSegmentDto.UnknownKind, boundary, end);
            return;
        }

        Add(result, TimeTrackSegmentDto.GapKind, start, end);
    }

    private static void Add(List<TimeTrackSegmentDto> result, string kind, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return;
        }

        result.Add(new TimeTrackSegmentDto
        {
            Kind = kind,
            Start = start,
            End = end,
            DurationSeconds = Math.Max(0, (end - start).TotalSeconds)
        });
    }

    private static TimeTrackSegmentDto ToDto(TimeTrackSegment seg)
        => new()
        {
            Kind = seg.Kind,
            Start = seg.Start,
            End = seg.End,
            DurationSeconds = seg.DurationSeconds
        };
}
