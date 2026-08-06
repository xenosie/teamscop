using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
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
    IAuthorityService authorities) : ITimeTrackQueryService
{
    private readonly record struct RawSeg(DateTimeOffset Start, DateTimeOffset End, string Kind);

    public async Task<TimeTrackTimelineDto> GetTimelineAsync(
        Guid viewerId,
        Guid staffUserId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        // Self sticker: own rolling timeline without view_timetrack / CanViewStaff.
        var isSelf = viewerId == staffUserId;
        if (!isSelf)
        {
            if (!await authorities.CanViewStaffAsync(viewerId, staffUserId, ct))
            {
                throw new UnauthorizedAccessException("Not allowed to view this staff member's tracking data.");
            }

            if (!await authorities.CanViewEventTypeAsync(viewerId, AgentEventTypes.TimeTrack, ct))
            {
                throw new UnauthorizedAccessException("Missing authority package for timetrack.");
            }
        }

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
            .Where(e => e.UserId == staffUserId
                        && e.EventType == AgentEventTypes.TimeTrack
                        && e.OccurredAt >= loadFrom
                        && e.OccurredAt < to)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.PayloadJson)
            .ToListAsync(ct);

        var raw = new List<RawSeg>(rows.Count);
        foreach (var payload in rows)
        {
            if (TryParseSegment(payload, out var seg))
            {
                raw.Add(seg);
            }
        }

        var clipped = ClipAndMerge(raw, from, to);
        var covering = FillGaps(clipped, from, to);
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
        List<RawSeg> raw,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var clipped = new List<RawSeg>();
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
            if (next.Kind == cur.Kind && next.Start <= cur.End.AddSeconds(1))
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

    private static List<TimeTrackSegmentDto> FillGaps(
        List<TimeTrackSegmentDto> coverage,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var result = new List<TimeTrackSegmentDto>();
        var cursor = from;
        foreach (var seg in coverage.OrderBy(s => s.Start))
        {
            if (seg.Start > cursor)
            {
                result.Add(Gap(cursor, seg.Start));
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
            result.Add(Gap(cursor, to));
        }

        if (result.Count == 0)
        {
            result.Add(Gap(from, to));
        }

        return result;
    }

    private static TimeTrackSegmentDto Gap(DateTimeOffset start, DateTimeOffset end)
        => new()
        {
            Kind = "gap",
            Start = start,
            End = end,
            DurationSeconds = Math.Max(0, (end - start).TotalSeconds)
        };

    private static TimeTrackSegmentDto ToDto(RawSeg seg)
        => new()
        {
            Kind = seg.Kind,
            Start = seg.Start,
            End = seg.End,
            DurationSeconds = Math.Max(0, (seg.End - seg.Start).TotalSeconds)
        };

    private static bool TryParseSegment(string payloadJson, out RawSeg seg)
    {
        seg = default;
        try
        {
            using var innerDoc = OpenInnerDocument(payloadJson);
            if (innerDoc is null)
            {
                return false;
            }

            var root = innerDoc.RootElement;
            var kind = ReadState(root);
            if (kind is null)
            {
                return false;
            }

            if (!TryReadTime(root, out var start, out var end))
            {
                return false;
            }

            if (end <= start)
            {
                return false;
            }

            seg = new RawSeg(start, end, kind);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string? ReadState(JsonElement root)
    {
        if (!root.TryGetProperty("State", out var stateEl)
            && !root.TryGetProperty("state", out stateEl))
        {
            return null;
        }

        if (stateEl.ValueKind == JsonValueKind.Number && stateEl.TryGetInt32(out var n))
        {
            return n == 1 ? "working" : "rest";
        }

        if (stateEl.ValueKind == JsonValueKind.String)
        {
            var s = stateEl.GetString()?.Trim();
            if (string.Equals(s, "Working", StringComparison.OrdinalIgnoreCase)
                || s == "1")
            {
                return "working";
            }

            if (string.Equals(s, "Rest", StringComparison.OrdinalIgnoreCase)
                || s == "0")
            {
                return "rest";
            }
        }

        if (stateEl.ValueKind is JsonValueKind.True)
        {
            return "working";
        }

        if (stateEl.ValueKind is JsonValueKind.False)
        {
            return "rest";
        }

        return null;
    }

    private static bool TryReadTime(JsonElement root, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;
        var startOk = TryGetDate(root, out start, "startedAtUtc", "StartedAtUtc", "startedAt", "StartedAt");
        var endOk = TryGetDate(root, out end, "endedAtUtc", "EndedAtUtc", "endedAt", "EndedAt");
        if (startOk && endOk)
        {
            return true;
        }

        // Fallback: duration + end only.
        if (endOk && root.TryGetProperty("durationSeconds", out var dur)
            && dur.TryGetDouble(out var seconds)
            && seconds > 0)
        {
            start = end.AddSeconds(-seconds);
            return true;
        }

        return false;
    }

    private static bool TryGetDate(JsonElement root, out DateTimeOffset value, params string[] names)
    {
        value = default;
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            if (DateTimeOffset.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out value))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonDocument? OpenInnerDocument(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var outer = JsonDocument.Parse(payloadJson);
        var root = outer.RootElement;
        if (root.TryGetProperty("payloadBase64", out var pb)
            && pb.ValueKind == JsonValueKind.String)
        {
            var b64 = pb.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(Convert.FromBase64String(b64));
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }

        if (root.TryGetProperty("State", out _) || root.TryGetProperty("state", out _))
        {
            return JsonDocument.Parse(payloadJson);
        }

        return null;
    }
}
