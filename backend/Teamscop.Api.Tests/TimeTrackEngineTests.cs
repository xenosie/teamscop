using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

/// <summary>
/// Defect 7 — "he is obviously working but the entire period is idle."
///
/// Two root causes are locked in here. First, activity has to be read from the last-input clock
/// MOVING, not from how large the idle reading is at each sampling instant, or a slow polling
/// cadence silently under-reports work. Second, when input cannot be observed at all — the
/// session-0 case that shipped — the engine must emit NOTHING, because reporting a working
/// employee as idle for a whole shift is the worst outcome this product can produce (§5.1, §5.4).
/// </summary>
public class TimeTrackEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SteadyTyping_ReadsAsWorking_AtEveryCadence_AndTheWorkFractionMatches()
    {
        // A user typing steadily: last-input advances every poll, so at a 30 s sampling cadence they
        // are always well past the previous poll instant. The OLD level test (idle <= samplePeriod)
        // called that idle forever; the delta test does not.
        var fast = RunSteadyTyping(pollEvery: TimeSpan.FromSeconds(5), windowSeconds: 300);
        var slow = RunSteadyTyping(pollEvery: TimeSpan.FromSeconds(30), windowSeconds: 300);

        Assert.NotNull(fast);
        Assert.NotNull(slow);

        // §5.1 — steady input is working, at any cadence.
        Assert.Equal(WorkState.Working, fast!.State);
        Assert.Equal(WorkState.Working, slow!.State);

        // The meaningful cadence invariant: the FRACTION of observed time counted as work is the
        // same regardless of how often the engine is polled. The slow cadence observes fewer
        // instants (so smaller absolute totals — honestly, it watched less), but it must not change
        // the verdict. Both are ~100% working here.
        var fastFraction = fast.WorkedSeconds / fast.ObservedSeconds;
        var slowFraction = slow.WorkedSeconds / slow.ObservedSeconds;
        Assert.True(fastFraction > 0.99, $"fast fraction {fastFraction}");
        Assert.True(slowFraction > 0.99, $"slow fraction {slowFraction}");
        Assert.True(Math.Abs(fastFraction - slowFraction) < 0.02);
    }

    [Fact]
    public void NoInput_ForThreeMinutes_TurnsToRest_AndAccumulatesIdle()
    {
        var clock = new ManualClock(T0);
        // Frozen last-input tick, and already well past the 3-minute idle threshold: the machine
        // has been untouched for a while, so every sample is Rest from the first one.
        var input = new FakeInputObserver(clock, () => 1000u, () => (clock.Now - T0) + TimeSpan.FromSeconds(240));
        var engine = new TimeTrackEngine(input: input, now: clock.Func);

        for (var i = 0; i < 60; i++)
        {
            engine.Poll();
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var window = engine.CloseWindow();
        Assert.NotNull(window);
        Assert.True(window!.IdleSeconds > window.WorkedSeconds);
        Assert.Equal(WorkState.Rest, window.State);
    }

    [Fact]
    public void UnobservableInput_EmitsNothing_NeverIdle()
    {
        var clock = new ManualClock(T0);
        // The session-0 case: the observer cannot see input at all.
        var input = new UnobservableInput();
        var engine = new TimeTrackEngine(input: input, now: clock.Func);

        Assert.False(engine.CanObserveInput);
        Assert.Equal(WindowsInputObserver.ReasonSessionZero, engine.InputUnavailableReason);

        for (var i = 0; i < 60; i++)
        {
            Assert.Null(engine.Poll());
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        // The honest answer to "we could not observe input" is silence, which renders as idle in
        // the timeline (§5.4) AND is flagged as a gap (§12.4) — not a fabricated Rest record.
        Assert.Null(engine.CloseWindow());
    }

    [Fact]
    public void ABlindStretchIsNotChargedToEitherBucket()
    {
        var clock = new ManualClock(T0);
        var observable = true;
        var tick = 1000u;
        var input = new FakeInputObserver(
            clock,
            () => tick,
            () => TimeSpan.Zero,
            () => observable);
        var engine = new TimeTrackEngine(input: input, now: clock.Func);

        // 20 s of observed, moving input → working.
        for (var i = 0; i < 4; i++)
        {
            tick += 5000;
            engine.Poll();
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        // The process is starved / the machine sleeps for two minutes: no polls happen at all.
        observable = false;
        engine.Poll(); // one unobservable poll breaks attribution
        clock.Advance(TimeSpan.FromMinutes(2));
        observable = true;

        // Resume for another 20 s.
        for (var i = 0; i < 4; i++)
        {
            tick += 5000;
            engine.Poll();
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var window = engine.CloseWindow();
        Assert.NotNull(window);
        // The two blind minutes are not attributed to work or rest: observed time is far less than
        // wall time, so the period reads as under-covered (a gap), not as two minutes of anything.
        Assert.True(window!.ObservedSeconds < 90, $"observed={window.ObservedSeconds}");
    }

    private static TimeTrackWindow? RunSteadyTyping(TimeSpan pollEvery, int windowSeconds)
    {
        var clock = new ManualClock(T0);
        // last-input tick tracks the clock: input happened "just now" continuously.
        var input = new FakeInputObserver(
            clock,
            () => unchecked((uint)(clock.Now - T0).TotalMilliseconds),
            () => TimeSpan.FromSeconds(1));
        var engine = new TimeTrackEngine(input: input, now: clock.Func);

        var elapsed = TimeSpan.Zero;
        while (elapsed < TimeSpan.FromSeconds(windowSeconds))
        {
            engine.Poll();
            clock.Advance(pollEvery);
            elapsed += pollEvery;
        }

        return engine.CloseWindow();
    }

    private sealed class ManualClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;
        public Func<DateTimeOffset> Func => () => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeInputObserver(
        ManualClock clock,
        Func<uint> tick,
        Func<TimeSpan> idle,
        Func<bool>? canObserve = null) : IInputObserver
    {
        public bool CanObserve => canObserve?.Invoke() ?? true;
        public string UnavailableReason => CanObserve ? string.Empty : "test_unobservable";
        public InputReading? Read()
        {
            _ = clock;
            return CanObserve ? new InputReading(tick(), idle()) : null;
        }
    }

    private sealed class UnobservableInput : IInputObserver
    {
        public bool CanObserve => false;
        public string UnavailableReason => WindowsInputObserver.ReasonSessionZero;
        public InputReading? Read() => null;
    }
}
