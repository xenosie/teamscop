using System.Text.Json;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// The single serializer for a closed timetrack window, so the two things that could ever emit one
/// (the SessionHelper today; anything added later) cannot drift into two payload shapes again.
///
/// The shape is deliberately BACKWARD COMPATIBLE with the server's existing
/// <c>TimeTrackSegmentReader</c>: it still carries <c>State</c>, <c>startedAtUtc</c>,
/// <c>endedAtUtc</c> and <c>durationSeconds</c>, so denormalization keeps working with no server
/// change. It ADDS <c>workedSeconds</c>/<c>idleSeconds</c> — the accumulated split — so a server
/// that later prefers them can charge a mixed minute proportionally instead of labelling the whole
/// window with one state. <c>durationSeconds</c> now equals the observed span (worked + idle),
/// closing the old inconsistency where it measured from the last state transition while the
/// timestamps measured the flush window.
/// </summary>
public static class TimeTrackPayload
{
    public static byte[] Serialize(TimeTrackWindow window, string source)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = window.State,
            IdleSeconds = window.IdleSecondsAtEnd,
            startedAtUtc = window.StartedAt,
            endedAtUtc = window.EndedAt,
            workedSeconds = window.WorkedSeconds,
            idleSeconds = window.IdleSeconds,
            durationSeconds = window.ObservedSeconds,
            algorithm = "last_input_delta_v2",
            source
        });
}
