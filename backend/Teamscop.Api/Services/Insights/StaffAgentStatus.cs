namespace Teamscop.Api.Services.Insights;

/// <summary>The four states an admin sees against a staff machine.</summary>
public static class AgentStatuses
{
    /// <summary>Green. A live service is reporting and capture is working.</summary>
    public const string Online = "online";

    /// <summary>Grey. Nothing is reporting. The machine is off, asleep, or cut off — unknowable.</summary>
    public const string Offline = "offline";

    /// <summary>Black. A live reporter on the machine positively asserts the other half is broken.</summary>
    public const string Broken = "broken";

    /// <summary>Yellow. Removed with authorisation, or never installed.</summary>
    public const string Uninstalled = "uninstalled";
}

/// <summary>What the agent says about its own capture pipeline. Computed on the machine.</summary>
public static class CaptureStates
{
    public const string Ok = "ok";
    public const string Broken = "broken";
    public const string Disabled = "disabled";
    public const string IdleNoUser = "idle_no_user";
    public const string UnsupportedSession = "unsupported_session";
    public const string Starting = "starting";
}

/// <summary>The desktop app's claim about the Windows service.</summary>
public static class AppServiceStates
{
    public const string Unknown = "unknown";
    public const string Running = "running";
    public const string StartPending = "start_pending";
    public const string Stopped = "stopped";
    public const string Disabled = "disabled";
    public const string NotInstalled = "not_installed";
    public const string ExeMissing = "exe_missing";

    /// <summary>Claims that assert a defect. Everything else is treated as "no claim".</summary>
    public static readonly IReadOnlySet<string> Defects = new HashSet<string>(StringComparer.Ordinal)
    {
        Stopped, Disabled, NotInstalled, ExeMissing
    };
}

/// <summary>Everything the classifier is allowed to look at, as of one instant.</summary>
public readonly record struct StaffStatusSignals(
    DateTimeOffset? ServiceHeartbeatAt,
    DateTimeOffset? AppReportAt,
    string? AppServiceState,
    DateTimeOffset? AppDefectSinceAt,
    string? CaptureState,
    string? CaptureReason,
    bool ComponentsMissing,
    string? MissingComponents,
    DateTimeOffset? UninstalledAt);

public readonly record struct StaffStatusVerdict(string Status, string Reason);

/// <summary>
/// Turns the evidence two independent reporters produce into one of four states.
///
/// Three rules hold the whole design together, and every change here must preserve them.
///
/// First, <b>black is never produced by silence</b>. It requires a fresh message from a live process
/// that positively asserts a defect. Silence is always grey, because a powered-off PC and a
/// thoroughly sabotaged one are genuinely indistinguishable from the server, and inventing a
/// distinction would mean accusing people whose laptop is simply shut.
///
/// Second, <b>the machine decides whether capture is broken, not the server</b>. Whether a helper
/// ought to be running depends on facts only the machine has: whether anyone is logged on, whether
/// the session is a console or an RDP one, and whether the admin disabled capture entirely. The
/// server receiving "no helper" cannot tell an empty office from sabotage.
///
/// Third, <b>the app channel can only ever darken the verdict</b>. It appears exclusively in the
/// black-producing rules. Its absence proves nothing, and no forged app report can produce green —
/// which matters because that channel runs as the monitored user.
/// </summary>
public static class StaffAgentStatus
{
    /// <summary>Six missed 30s heartbeats. Long enough to ride out a flaky network.</summary>
    public static readonly TimeSpan ServiceStale = TimeSpan.FromMinutes(3);

    /// <summary>Three missed 60s app reports.</summary>
    public static readonly TimeSpan AppStale = TimeSpan.FromMinutes(3);

    /// <summary>How long an app-asserted defect must stand before it counts. Absorbs resume-from-sleep.</summary>
    public static readonly TimeSpan AppDefectConfirm = TimeSpan.FromMinutes(2);

    /// <summary>The same, when the service has never reported at all — covers a slow first install.</summary>
    public static readonly TimeSpan ColdDefectConfirm = TimeSpan.FromMinutes(10);

    /// <summary>App says the service is running, yet nothing arrives from it. Something is wedged.</summary>
    public static readonly TimeSpan ServiceSilentBroken = TimeSpan.FromMinutes(10);

    /// <summary>A heartbeat this long after an uninstall means the product was put back.</summary>
    public static readonly TimeSpan UninstallClear = TimeSpan.FromMinutes(10);

    public static StaffStatusVerdict Classify(StaffStatusSignals s, DateTimeOffset now)
    {
        var serviceFresh = Fresh(s.ServiceHeartbeatAt, now, ServiceStale);
        var appFresh = Fresh(s.AppReportAt, now, AppStale);

        // R1 — an authorised uninstall stops the service, and a still-resident app would otherwise
        // report the service as missing. Yellow has to win, or every legitimate removal reads as
        // sabotage. Requires the service to have actually gone: UninstalledAt is written at the
        // point of commitment, and teardown takes a moment.
        if (s.UninstalledAt is not null && !serviceFresh)
        {
            return new StaffStatusVerdict(AgentStatuses.Uninstalled, "authorized_uninstall");
        }

        // R2 — the machine itself says capture is broken while it is demonstrably alive.
        if (serviceFresh && string.Equals(s.CaptureState, CaptureStates.Broken, StringComparison.Ordinal))
        {
            return new StaffStatusVerdict(AgentStatuses.Broken, s.CaptureReason ?? "capture_broken");
        }

        // R3 — files are gone from the install directory. Whichever half still runs reports it, so
        // a gutted installation cannot keep reporting itself healthy out of memory.
        if ((serviceFresh || appFresh) && s.ComponentsMissing)
        {
            return new StaffStatusVerdict(
                AgentStatuses.Broken,
                string.IsNullOrWhiteSpace(s.MissingComponents) ? "components_missing" : $"components_missing:{s.MissingComponents}");
        }

        // R4 — the service is silent and the app, which is alive and running as the user, says why.
        // Debounced, because a machine resuming from sleep briefly looks exactly like this.
        if (!serviceFresh && AppAssertsDefect(s, now))
        {
            return new StaffStatusVerdict(AgentStatuses.Broken, ReasonFor(s.AppServiceState));
        }

        // R5 — the app says the service is running, yet the server has heard nothing from it for ten
        // minutes. The service process exists but is not doing its job. Requires the service to have
        // heartbeated at least ONCE: a freshly installed machine runs its service tokenless until
        // the employee joins, so "running but never heard from" is the normal pre-enrolment state,
        // and accusing it painted every install-day machine black.
        if (!serviceFresh
            && appFresh
            && string.Equals(s.AppServiceState, AppServiceStates.Running, StringComparison.Ordinal)
            && s.ServiceHeartbeatAt is not null
            && now - s.ServiceHeartbeatAt.Value > ServiceSilentBroken)
        {
            return new StaffStatusVerdict(AgentStatuses.Broken, "service_silent");
        }

        // R6 — the service is talking. That beats any claim the app makes about it, because the
        // service's own traffic is direct evidence and the app's claim is hearsay.
        if (serviceFresh)
        {
            return new StaffStatusVerdict(AgentStatuses.Online, "healthy");
        }

        // R7 — nothing has ever been heard from this machine on either channel.
        if (s.ServiceHeartbeatAt is null && s.AppReportAt is null && s.UninstalledAt is null)
        {
            return new StaffStatusVerdict(AgentStatuses.Uninstalled, "not_installed");
        }

        // R8 — the app is alive and says the service is not there, but has not been saying it long
        // enough to call it sabotage (R4 above owns that, once the cold window has passed). During a
        // first install this is simply the truth, and yellow says more than grey would.
        if (!serviceFresh
            && appFresh
            && string.Equals(s.AppServiceState, AppServiceStates.NotInstalled, StringComparison.Ordinal))
        {
            return new StaffStatusVerdict(AgentStatuses.Uninstalled, "not_installed");
        }

        // R9 — silence. Honest grey.
        return new StaffStatusVerdict(AgentStatuses.Offline, "no_channel");
    }

    private static bool Fresh(DateTimeOffset? at, DateTimeOffset now, TimeSpan window)
        => at is not null && now - at.Value <= window;

    /// <summary>
    /// True when the app is fresh, names a defect, and has been saying so long enough to be believed.
    /// The confirmation window is longer when the service has never reported, so a machine caught
    /// mid-first-install is not branded broken before it finishes.
    /// </summary>
    private static bool AppAssertsDefect(StaffStatusSignals s, DateTimeOffset now)
    {
        if (!Fresh(s.AppReportAt, now, AppStale)
            || s.AppServiceState is null
            || !AppServiceStates.Defects.Contains(s.AppServiceState)
            || s.AppDefectSinceAt is null)
        {
            return false;
        }

        var confirm = s.ServiceHeartbeatAt is null ? ColdDefectConfirm : AppDefectConfirm;
        return now - s.AppDefectSinceAt.Value >= confirm;
    }

    private static string ReasonFor(string? appServiceState) => appServiceState switch
    {
        AppServiceStates.Stopped => "service_stopped",
        AppServiceStates.Disabled => "service_disabled",
        AppServiceStates.NotInstalled => "service_deleted",
        AppServiceStates.ExeMissing => "service_missing",
        _ => "service_defect"
    };
}
