using Teamscop.Api.Services.Insights;

namespace Teamscop.Api.Tests;

/// <summary>
/// §14 — the four-state classifier, one test per rule and per tie-break.
///
/// The tests that matter most here are the ones asserting a machine is NOT broken. Marking a
/// healthy PC black tells the owner an employee sabotaged their machine, which is a serious
/// accusation to get wrong; a missed detection is merely a gap. Every case where the old
/// heartbeat-only model would have guessed is pinned below.
/// </summary>
public class StaffAgentStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static StaffStatusSignals Signals(
        TimeSpan? serviceAgo = null,
        TimeSpan? appAgo = null,
        string? appServiceState = null,
        TimeSpan? defectAgo = null,
        string? captureState = CaptureStates.Ok,
        string? captureReason = null,
        bool componentsMissing = false,
        string? missingComponents = null,
        TimeSpan? uninstalledAgo = null,
        bool serviceNeverReported = false)
        => new(
            ServiceHeartbeatAt: serviceNeverReported ? null : Now - (serviceAgo ?? TimeSpan.Zero),
            AppReportAt: appAgo is null ? null : Now - appAgo.Value,
            AppServiceState: appServiceState,
            AppDefectSinceAt: defectAgo is null ? null : Now - defectAgo.Value,
            CaptureState: captureState,
            CaptureReason: captureReason,
            ComponentsMissing: componentsMissing,
            MissingComponents: missingComponents,
            UninstalledAt: uninstalledAgo is null ? null : Now - uninstalledAgo.Value);

    private static string Status(StaffStatusSignals s) => StaffAgentStatus.Classify(s, Now).Status;

    [Fact]
    public void FreshHeartbeatAndCaptureOk_IsOnline()
        => Assert.Equal(AgentStatuses.Online, Status(Signals(serviceAgo: TimeSpan.FromSeconds(30))));

    [Fact]
    public void FreshHeartbeatAndCaptureBroken_IsBrokenWithTheAgentsReason()
    {
        var verdict = StaffAgentStatus.Classify(
            Signals(captureState: CaptureStates.Broken, captureReason: "no_session_helper_stale"),
            Now);

        Assert.Equal(AgentStatuses.Broken, verdict.Status);
        Assert.Equal("no_session_helper_stale", verdict.Reason);
    }

    /// <summary>
    /// The single most important regression guard. Nobody is logged on overnight, so no helper can
    /// run and no screenshot can exist. That is a closed office, not sabotage.
    /// </summary>
    [Fact]
    public void FreshHeartbeatAndNobodyLoggedOn_IsOnline_NotBroken()
        => Assert.Equal(AgentStatuses.Online, Status(Signals(captureState: CaptureStates.IdleNoUser)));

    [Theory]
    [InlineData(CaptureStates.Disabled)]
    [InlineData(CaptureStates.UnsupportedSession)]
    [InlineData(CaptureStates.Starting)]
    public void FreshHeartbeatAndNonFaultCaptureState_IsOnline(string captureState)
        => Assert.Equal(AgentStatuses.Online, Status(Signals(captureState: captureState)));

    [Fact]
    public void DeletedComponentFiles_AreBroken_EvenWhileTheServiceStillReports()
    {
        var verdict = StaffAgentStatus.Classify(
            Signals(componentsMissing: true, missingComponents: "Teamscop.SessionHelper.exe"),
            Now);

        Assert.Equal(AgentStatuses.Broken, verdict.Status);
        Assert.Contains("Teamscop.SessionHelper.exe", verdict.Reason);
    }

    /// <summary>A gutted install must still be caught when only the app survives to report it.</summary>
    [Fact]
    public void DeletedComponentFiles_AreBroken_WhenOnlyTheAppIsReporting()
        => Assert.Equal(
            AgentStatuses.Broken,
            Status(Signals(
                serviceNeverReported: true,
                appAgo: TimeSpan.FromSeconds(30),
                componentsMissing: true)));

    [Fact]
    public void StaleServiceAndAppSaysStopped_UnderDebounce_IsOffline()
        => Assert.Equal(
            AgentStatuses.Offline,
            Status(Signals(
                serviceAgo: TimeSpan.FromMinutes(5),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.Stopped,
                defectAgo: TimeSpan.FromSeconds(30))));

    [Fact]
    public void StaleServiceAndAppSaysStopped_PastDebounce_IsBroken()
    {
        var verdict = StaffAgentStatus.Classify(
            Signals(
                serviceAgo: TimeSpan.FromMinutes(5),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.Stopped,
                defectAgo: TimeSpan.FromMinutes(3)),
            Now);

        Assert.Equal(AgentStatuses.Broken, verdict.Status);
        Assert.Equal("service_stopped", verdict.Reason);
    }

    [Theory]
    [InlineData(AppServiceStates.Disabled, "service_disabled")]
    [InlineData(AppServiceStates.NotInstalled, "service_deleted")]
    [InlineData(AppServiceStates.ExeMissing, "service_missing")]
    public void EachAppAssertedDefect_HasItsOwnReason(string appState, string expectedReason)
    {
        var verdict = StaffAgentStatus.Classify(
            Signals(
                serviceAgo: TimeSpan.FromMinutes(5),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: appState,
                defectAgo: TimeSpan.FromMinutes(3)),
            Now);

        Assert.Equal(AgentStatuses.Broken, verdict.Status);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    /// <summary>Resume from sleep: both channels are briefly stale, and neither is at fault.</summary>
    [Fact]
    public void StaleServiceAndAppSaysRunning_UnderTenMinutes_IsOffline()
        => Assert.Equal(
            AgentStatuses.Offline,
            Status(Signals(
                serviceAgo: TimeSpan.FromMinutes(5),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.Running)));

    [Fact]
    public void StaleServiceAndAppSaysRunning_PastTenMinutes_IsBrokenServiceSilent()
    {
        var verdict = StaffAgentStatus.Classify(
            Signals(
                serviceAgo: TimeSpan.FromMinutes(15),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.Running),
            Now);

        Assert.Equal(AgentStatuses.Broken, verdict.Status);
        Assert.Equal("service_silent", verdict.Reason);
    }

    /// <summary>An upgrade stops the service on purpose. Unknown is not a defect.</summary>
    [Theory]
    [InlineData(AppServiceStates.Unknown)]
    [InlineData(AppServiceStates.StartPending)]
    public void AppClaimsThatAssertNothing_NeverProduceBroken(string appState)
        => Assert.Equal(
            AgentStatuses.Offline,
            Status(Signals(
                serviceAgo: TimeSpan.FromMinutes(20),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: appState,
                defectAgo: TimeSpan.FromHours(1))));

    [Fact]
    public void UninstalledAndServiceGone_IsUninstalled_BeatsTheAppsDefectClaim()
        => Assert.Equal(
            AgentStatuses.Uninstalled,
            Status(Signals(
                serviceAgo: TimeSpan.FromMinutes(20),
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.NotInstalled,
                defectAgo: TimeSpan.FromMinutes(15),
                uninstalledAgo: TimeSpan.FromMinutes(10))));

    /// <summary>
    /// The uninstall is recorded at the point of commitment, so the service can still be finishing
    /// its teardown. Until it actually stops, the machine is demonstrably alive.
    /// </summary>
    [Fact]
    public void UninstallRecordedButServiceStillReporting_IsOnline()
        => Assert.Equal(
            AgentStatuses.Online,
            Status(Signals(serviceAgo: TimeSpan.FromSeconds(20), uninstalledAgo: TimeSpan.FromSeconds(30))));

    [Fact]
    public void NeverHeardFromEitherChannel_IsNotInstalled()
    {
        var verdict = StaffAgentStatus.Classify(Signals(serviceNeverReported: true), Now);

        Assert.Equal(AgentStatuses.Uninstalled, verdict.Status);
        Assert.Equal("not_installed", verdict.Reason);
    }

    /// <summary>
    /// Enrolled, then the service deleted before it ever reported. The app says so, and after the
    /// cold-confirm window that is sabotage — it must not be laundered as "never installed".
    /// </summary>
    [Fact]
    public void ServiceDeletedBeforeFirstHeartbeat_PastColdWindow_IsBroken_NotNotInstalled()
        => Assert.Equal(
            AgentStatuses.Broken,
            Status(Signals(
                serviceNeverReported: true,
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.NotInstalled,
                defectAgo: TimeSpan.FromMinutes(11))));

    [Fact]
    public void ServiceDeletedBeforeFirstHeartbeat_InsideColdWindow_IsStillNotInstalled()
        => Assert.Equal(
            AgentStatuses.Uninstalled,
            Status(Signals(
                serviceNeverReported: true,
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.NotInstalled,
                defectAgo: TimeSpan.FromMinutes(2))));

    /// <summary>
    /// A freshly installed machine runs its service tokenless until the employee joins, so it sends
    /// no heartbeat while the app truthfully says "running". That is install day, not sabotage.
    /// </summary>
    [Fact]
    public void ServiceNeverHeartbeatedAndAppSaysRunning_IsNotBroken()
        => Assert.NotEqual(
            AgentStatuses.Broken,
            Status(Signals(
                serviceNeverReported: true,
                appAgo: TimeSpan.FromSeconds(30),
                appServiceState: AppServiceStates.Running)));

    /// <summary>Both silent is a powered-off PC as far as anyone can tell. Grey, never black.</summary>
    [Fact]
    public void BothChannelsSilent_IsOffline_NeverBroken()
        => Assert.Equal(
            AgentStatuses.Offline,
            Status(Signals(serviceAgo: TimeSpan.FromHours(2), appAgo: TimeSpan.FromHours(2))));

    /// <summary>
    /// The app channel runs as the monitored user, so it must never be able to manufacture health.
    /// It appears only in black-producing rules; a forged "everything is fine" changes nothing.
    /// </summary>
    [Fact]
    public void ForgedAppReportClaimingRunning_CannotProduceOnline()
        => Assert.NotEqual(
            AgentStatuses.Online,
            Status(Signals(
                serviceAgo: TimeSpan.FromHours(1),
                appAgo: TimeSpan.FromSeconds(1),
                appServiceState: AppServiceStates.Running,
                captureState: CaptureStates.Ok)));

    /// <summary>A live service outranks the app's hearsay about it: the traffic is direct evidence.</summary>
    [Fact]
    public void ServiceReportingWhileAppClaimsItIsStopped_IsOnline()
        => Assert.Equal(
            AgentStatuses.Online,
            Status(Signals(
                serviceAgo: TimeSpan.FromSeconds(20),
                appAgo: TimeSpan.FromSeconds(20),
                appServiceState: AppServiceStates.Stopped,
                defectAgo: TimeSpan.FromHours(1))));
}
