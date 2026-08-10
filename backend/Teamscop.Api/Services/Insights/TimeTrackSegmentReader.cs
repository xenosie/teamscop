using System.Globalization;
using System.Text.Json;

namespace Teamscop.Api.Services.Insights;

/// <summary>
/// One closed timetrack window: the interval the agent observed and whether it counted as work.
/// §5.1 — "working" is keyboard or mouse input, nothing else.
/// </summary>
public readonly record struct TimeTrackSegment(DateTimeOffset Start, DateTimeOffset End, bool Working)
{
    public const string WorkingKind = "working";
    public const string RestKind = "rest";

    public string Kind => Working ? WorkingKind : RestKind;

    public double DurationSeconds => Math.Max(0, (End - Start).TotalSeconds);
}

/// <summary>
/// Reads a stored <c>timetrack</c> payload into a <see cref="TimeTrackSegment"/>. Written once so
/// ingest (which denormalizes the segment onto the row) and the timeline query can never disagree
/// about what a payload means.
/// </summary>
public static class TimeTrackSegmentReader
{
    // The agent writes UTC wall clocks, so an offset-less timestamp means UTC.
    private const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    public static bool TryRead(string? payloadJson, out TimeTrackSegment segment)
    {
        segment = default;
        using var innerDoc = AgentEventPayload.TryOpen(payloadJson, "State", "state");
        if (innerDoc is null)
        {
            return false;
        }

        var root = innerDoc.RootElement;
        if (ReadWorking(root) is not { } working)
        {
            return false;
        }

        if (!TryReadTime(root, out var start, out var end) || end <= start)
        {
            return false;
        }

        segment = new TimeTrackSegment(start, end, working);
        return true;
    }

    private static bool? ReadWorking(JsonElement root)
    {
        if (!root.TryGetProperty("State", out var stateEl)
            && !root.TryGetProperty("state", out stateEl))
        {
            return null;
        }

        if (stateEl.ValueKind == JsonValueKind.Number && stateEl.TryGetInt32(out var n))
        {
            return n == 1;
        }

        if (stateEl.ValueKind == JsonValueKind.String)
        {
            var s = stateEl.GetString()?.Trim();
            if (string.Equals(s, "Working", StringComparison.OrdinalIgnoreCase) || s == "1")
            {
                return true;
            }

            if (string.Equals(s, "Rest", StringComparison.OrdinalIgnoreCase) || s == "0")
            {
                return false;
            }
        }

        return stateEl.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryReadTime(JsonElement root, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        var startValue = AgentEventPayload.Date(root, Utc, "startedAtUtc", "StartedAtUtc", "startedAt", "StartedAt");
        var endValue = AgentEventPayload.Date(root, Utc, "endedAtUtc", "EndedAtUtc", "endedAt", "EndedAt");
        end = endValue ?? default;

        if (startValue is not null && endValue is not null)
        {
            start = startValue.Value;
            return true;
        }

        // Fallback: duration + end only.
        if (endValue is not null
            && root.TryGetProperty("durationSeconds", out var dur)
            && dur.TryGetDouble(out var seconds)
            && seconds > 0)
        {
            start = end.AddSeconds(-seconds);
            return true;
        }

        return false;
    }
}
