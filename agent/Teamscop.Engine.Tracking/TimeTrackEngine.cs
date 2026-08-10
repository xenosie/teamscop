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

/// <summary>
/// One emitted timetrack window. Worked and idle are ACCUMULATED across the window rather than the
/// window being labelled with whatever state happened to be current at the flush instant — a minute
/// containing both work and rest used to be counted 100% as one of them, and with a three-minute
/// hysteresis that could charge three minutes of genuine idle as work.
///
/// <see cref="StartedAt"/> is the first instant actually observed in this window, not the flush
/// anchor, so the coverage the server derives from (start, end) is exactly what was seen. A period
/// nobody observed produces no window at all and therefore surfaces as a gap (§12.4), which is the
/// truth: "we could not observe input", not "he was resting".
/// </summary>
public sealed record TimeTrackWindow
{
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required double WorkedSeconds { get; init; }
    public required double IdleSeconds { get; init; }

    /// <summary>The dominant state, for readers that want one label.</summary>
    public WorkState State => WorkedSeconds >= IdleSeconds && WorkedSeconds > 0
        ? WorkState.Working
        : WorkState.Rest;

    /// <summary>OS idle at the end of the window, for diagnostics only.</summary>
    public required uint IdleSecondsAtEnd { get; init; }

    public double ObservedSeconds => WorkedSeconds + IdleSeconds;
}

/// <summary>
/// Work/rest classifier over the OS last-input clock. No per-keystroke hooks — those cost CPU and
/// can destabilise input on the cheap hardware §15.2 targets.
///
/// Two properties matter and neither held before:
/// <list type="number">
/// <item><b>It refuses to guess.</b> <see cref="Poll"/> returns null when input cannot be observed
/// (see <see cref="IInputObserver"/>). It never invents an idle reading, because reporting a
/// working employee as idle for a whole shift is the worst outcome this product can produce.</item>
/// <item><b>It does not depend on the caller's cadence.</b> Activity is inferred from the last-input
/// stamp MOVING between polls, so 5 s and 30 s sampling yield the same worked/idle totals. The old
/// level test (<c>idle &lt;= samplePeriod</c>) silently under-reported at any cadence slower than
/// the sample period.</item>
/// </list>
///
/// §5.1 — any observed keyboard or mouse input means working. §5.2 — three minutes without it means
/// rest. Thresholds come from <see cref="TrackingDefaults"/>.
/// </summary>
public sealed class TimeTrackEngine
{
    private readonly TimeSpan _samplePeriod;
    private readonly TimeSpan _activeThreshold;
    private readonly TimeSpan _restThreshold;
    private readonly TimeSpan _maxAttribution;
    private readonly IInputObserver _input;
    private readonly Func<DateTimeOffset> _now;

    private WorkState _state = WorkState.Rest;
    private uint? _lastInputTick;
    private DateTimeOffset? _attributionAnchor;
    private DateTimeOffset? _coverageStartedAt;
    private DateTimeOffset? _lastObservedAt;
    private double _workedSeconds;
    private double _idleSeconds;
    private uint _lastIdleSeconds;

    public TimeTrackEngine(
        TimeSpan? samplePeriod = null,
        TimeSpan? activeThreshold = null,
        TimeSpan? restThreshold = null,
        IInputObserver? input = null,
        Func<DateTimeOffset>? now = null)
    {
        _samplePeriod = samplePeriod ?? TrackingDefaults.SamplePeriod;
        // §5.1: keyboard or mouse input is the only signal, so one sample with input means working.
        _activeThreshold = activeThreshold ?? _samplePeriod;
        _restThreshold = restThreshold ?? TrackingDefaults.IdleThreshold;
        if (_restThreshold < _activeThreshold)
        {
            throw new ArgumentException("restThreshold must be >= activeThreshold");
        }

        // A poll gap longer than one flush window means the process was not running (sleep,
        // suspend, starvation). Time nobody was there to measure is not attributed to either
        // bucket — it becomes an under-claim of coverage, which reads as a gap rather than as work.
        _maxAttribution = TrackingDefaults.FlushPeriod;
        _input = input ?? new WindowsInputObserver();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan SamplePeriod => _samplePeriod;

    public WorkState CurrentState => _state;

    /// <summary>False when this process cannot see user input; the caller must then emit nothing.</summary>
    public bool CanObserveInput => _input.CanObserve;

    public string InputUnavailableReason => _input.UnavailableReason;

    /// <summary>
    /// Samples input and advances the state machine. Null means "not observable" — the caller must
    /// treat that as silence, never as rest.
    /// </summary>
    public TimeTrackSample? Poll()
    {
        var reading = _input.Read();
        var now = _now();
        if (reading is not { } input)
        {
            // Break attribution so the unobservable stretch is never charged to a state.
            _attributionAnchor = null;
            _lastInputTick = null;
            return null;
        }

        if (_attributionAnchor is { } anchor && now > anchor)
        {
            var elapsed = now - anchor;
            if (elapsed > _maxAttribution)
            {
                elapsed = _maxAttribution;
            }

            if (_state == WorkState.Working)
            {
                _workedSeconds += elapsed.TotalSeconds;
            }
            else
            {
                _idleSeconds += elapsed.TotalSeconds;
            }
        }
        else
        {
            // First observation of this window (or the first after a blind stretch): coverage
            // begins here, so a window can never claim time it did not watch.
            _coverageStartedAt ??= now;
        }

        _attributionAnchor = now;
        _lastObservedAt = now;
        _coverageStartedAt ??= now;
        _lastIdleSeconds = (uint)Math.Min(uint.MaxValue, input.Idle.TotalSeconds);

        // §5.1 — the stamp moving is the signal. Only the very first poll has nothing to compare
        // against, and then the level is the best available answer.
        var inputSeen = _lastInputTick is { } previousTick
            ? input.LastInputTick != previousTick
            : input.Idle < _samplePeriod;
        _lastInputTick = input.LastInputTick;

        if (_state == WorkState.Rest && (inputSeen || input.Idle <= _activeThreshold))
        {
            _state = WorkState.Working;
        }
        else if (_state == WorkState.Working && !inputSeen && input.Idle >= _restThreshold)
        {
            _state = WorkState.Rest;
        }

        return new TimeTrackSample
        {
            At = now,
            State = _state,
            IdleSeconds = _lastIdleSeconds,
            InputSeenInBucket = inputSeen
        };
    }

    /// <summary>
    /// Closes the accumulation window. Null when nothing was observed since the last close — the
    /// caller emits nothing and the period becomes a reported gap instead of a fabricated rest.
    /// </summary>
    public TimeTrackWindow? CloseWindow()
    {
        if (_coverageStartedAt is not { } start
            || _lastObservedAt is not { } end
            || end <= start
            || _workedSeconds + _idleSeconds <= 0)
        {
            // Nothing watched: drop the anchor so the next window starts where observation resumes.
            _coverageStartedAt = null;
            _workedSeconds = 0;
            _idleSeconds = 0;
            return null;
        }

        var window = new TimeTrackWindow
        {
            StartedAt = start,
            EndedAt = end,
            WorkedSeconds = Math.Round(_workedSeconds, 3),
            IdleSeconds = Math.Round(_idleSeconds, 3),
            IdleSecondsAtEnd = _lastIdleSeconds
        };

        _workedSeconds = 0;
        _idleSeconds = 0;
        _coverageStartedAt = end;
        return window;
    }
}
