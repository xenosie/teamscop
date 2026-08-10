namespace Teamscop.Engine.Tracking;

/// <summary>
/// The agent's verdict on its own capture pipeline, sent to the server on every heartbeat.
///
/// This lives on the machine, not on the server, and that placement is the whole point. Whether a
/// missing screenshot is a fault depends on facts only the machine can see: whether anybody is
/// logged on, whether the session is one that can be captured at all, and whether the admin turned
/// capture off. A server told merely "no helper is running" cannot tell an empty office at 3am from
/// a sabotaged laptop, and guessing means accusing people whose only crime was going home.
///
/// So the machine answers the question, and the server believes it — because the only thing this
/// verdict can do is make a machine look worse, never better.
/// </summary>
public static class CaptureState
{
    public const string Ok = "ok";
    public const string Broken = "broken";
    public const string Disabled = "disabled";
    public const string IdleNoUser = "idle_no_user";
    public const string UnsupportedSession = "unsupported_session";
    public const string Starting = "starting";

    /// <summary>
    /// How long after someone signs in before a missing helper counts as a fault. The supervisor
    /// needs a moment to launch it into the new session, and a laptop resuming from sleep can take
    /// longer than feels reasonable.
    /// </summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    public readonly record struct Verdict(string State, string? Reason)
    {
        public bool IsBroken => string.Equals(State, Broken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Evaluates the pipeline. Ordered so that every innocent explanation is checked before the
    /// guilty one — the last branch is the only one that can accuse.
    /// </summary>
    /// <param name="anyCaptureEnabled">Whether the admin has any capture type switched on.</param>
    /// <param name="sessions">What the machine's interactive sessions look like right now.</param>
    /// <param name="consoleUserSince">
    /// When a console user was first seen in the current stretch, or null if there is none.
    /// </param>
    /// <param name="helperAlive">Whether the capture helper has checked in recently.</param>
    /// <param name="trackingHealthy">Whether captures are actually being produced.</param>
    /// <param name="unavailableReason">The engine's own words for why capture is unavailable.</param>
    /// <param name="now">Current time, injected so this stays pure and testable.</param>
    public static Verdict Evaluate(
        bool anyCaptureEnabled,
        SessionSnapshot sessions,
        DateTimeOffset? consoleUserSince,
        bool helperAlive,
        bool trackingHealthy,
        string? unavailableReason,
        DateTimeOffset now)
    {
        // Nothing is switched on, so nothing is expected. Not a fault.
        if (!anyCaptureEnabled)
        {
            return new Verdict(Disabled, null);
        }

        // Nobody is signed in. There is no screen to capture — the overnight and weekend case, and
        // the single most likely way to wrongly accuse an honest machine.
        if (!sessions.AnyUserLoggedOn)
        {
            return new Verdict(IdleNoUser, null);
        }

        // Signed in, but not at the physical console — an RDP-only session. The helper is launched
        // into the active console session, so capture genuinely cannot happen here. That is a
        // product limitation, and calling a limitation sabotage would be dishonest.
        if (!sessions.ActiveConsoleUser)
        {
            return new Verdict(UnsupportedSession, null);
        }

        // Signed in at the console, but only just. Give the supervisor time to start the helper.
        if (consoleUserSince is { } since && now - since < StartGrace)
        {
            return new Verdict(Starting, null);
        }

        if (helperAlive && trackingHealthy)
        {
            return new Verdict(Ok, null);
        }

        // Somebody is at the console, capture is enabled, the grace period has passed, and there is
        // still nothing. This is the only branch that accuses.
        return new Verdict(Broken, unavailableReason ?? "capture_unavailable");
    }
}
