namespace Teamscop.Engine.Tracking;

/// <summary>
/// The §5.1–5.2 tracking cadence, defined once.
/// Both capture paths construct <see cref="TimeTrackEngine"/> with no arguments
/// (TrackingCoordinator in-process, and the interactive SessionHelper), so these
/// defaults are the only place the idle threshold is stated and it cannot drift
/// between the two.
/// </summary>
public static class TrackingDefaults
{
    /// <summary>
    /// §5.2 — no keyboard or mouse input for this long counts as idle.
    /// </summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How often work/rest is sampled. §5.1 makes any input inside one sample
    /// "working", so this doubles as the return-to-working threshold.
    /// </summary>
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(15);

    /// <summary>How often a timetrack window is closed and emitted.</summary>
    public static readonly TimeSpan FlushPeriod = TimeSpan.FromSeconds(60);
}
