using System.Runtime.InteropServices;
using System.Text.Json;

namespace Teamscop.Engine.Tracking;

public enum WorkState
{
    Rest = 0,
    Working = 1
}

public sealed class TimeTrackSample
{
    public required DateTimeOffset At { get; init; }
    public required WorkState State { get; init; }
    public required uint IdleSeconds { get; init; }
    public required bool InputSeenInBucket { get; init; }
}

public sealed class TimeTrackSegment
{
    public required WorkState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
}

/// <summary>
/// Lightweight work/rest classifier using OS last-input idle time.
/// Avoids per-keystroke hooks (those cost CPU and can destabilize input).
/// Algorithm: working if idle &lt; activeThreshold; rest if idle &gt; restThreshold (hysteresis).
/// </summary>
public sealed class TimeTrackEngine
{
    private readonly TimeSpan _samplePeriod;
    private readonly TimeSpan _activeThreshold;
    private readonly TimeSpan _restThreshold;
    private WorkState _state = WorkState.Rest;
    private DateTimeOffset _segmentStarted = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;

    public TimeTrackEngine(
        TimeSpan? samplePeriod = null,
        TimeSpan? activeThreshold = null,
        TimeSpan? restThreshold = null)
    {
        _samplePeriod = samplePeriod ?? TimeSpan.FromSeconds(15);
        _activeThreshold = activeThreshold ?? TimeSpan.FromSeconds(60);
        _restThreshold = restThreshold ?? TimeSpan.FromSeconds(180);
        if (_restThreshold < _activeThreshold)
        {
            throw new ArgumentException("restThreshold must be >= activeThreshold");
        }
    }

    public TimeSpan SamplePeriod => _samplePeriod;
    public WorkState CurrentState => _state;

    public TimeTrackSample Poll()
    {
        var idle = GetIdleTime();
        var now = DateTimeOffset.UtcNow;
        var inputSeen = idle < _samplePeriod;

        // Hysteresis state machine — prevents flapping around a single threshold.
        if (_state == WorkState.Rest && idle <= _activeThreshold)
        {
            _state = WorkState.Working;
            _segmentStarted = now;
        }
        else if (_state == WorkState.Working && idle >= _restThreshold)
        {
            _state = WorkState.Rest;
            _segmentStarted = now;
        }

        _lastTick = now;
        return new TimeTrackSample
        {
            At = now,
            State = _state,
            IdleSeconds = (uint)Math.Min(uint.MaxValue, idle.TotalSeconds),
            InputSeenInBucket = inputSeen
        };
    }

    public TimeTrackSegment CloseSegment(DateTimeOffset endedAt)
    {
        var seg = new TimeTrackSegment
        {
            State = _state,
            StartedAt = _segmentStarted,
            EndedAt = endedAt
        };
        _segmentStarted = endedAt;
        return seg;
    }

    public byte[] SerializeSample(TimeTrackSample sample)
        => JsonSerializer.SerializeToUtf8Bytes(sample);

    public static TimeSpan GetIdleTime()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetWindowsIdleTime();
        }

        // Non-Windows: synthetic idle based on process CPU is not meaningful; treat as restful for tests.
        return TimeSpan.FromSeconds(999);
    }

    private static TimeSpan GetWindowsIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.FromSeconds(999);
        }

        var idleMs = unchecked(Environment.TickCount - (int)info.LastInputTime);
        if (idleMs < 0)
        {
            idleMs = 0;
        }

        return TimeSpan.FromMilliseconds(idleMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint LastInputTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
