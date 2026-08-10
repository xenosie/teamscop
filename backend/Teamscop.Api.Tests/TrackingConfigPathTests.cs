using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;
using Teamscop.StaffService;

namespace Teamscop.Api.Tests;

/// <summary>
/// C2 / §6.2 — "the per-staff config channel is a core feature, not optional", and until now nothing
/// checked that a config the admin sets ever reaches the process that actually captures.
///
/// The real path is: hub or HTTP pull → <c>TrackingCoordinator.ApplyConfig</c> → the named pipe →
/// <c>Teamscop.SessionHelper</c>, which is a different process in the interactive session because a
/// LocalSystem service cannot see the desktop. These tests run the two real halves of that pipe
/// against each other — <see cref="SessionHelperPipeClient"/> as the helper,
/// <see cref="CapturePipeServer"/> as the service — so a change on either side that breaks the
/// contract fails here rather than on a customer's machine.
///
/// The second half is the fallback. If the helper dies, the service must go back to capturing in
/// process rather than latching "helper owns capture" forever and reporting a silent idle day.
/// </summary>
public class TrackingConfigPathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePerStaffConfigReachesTheCapturingProcess_AndAChangeIsPickedUp()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 45, quality: ScreenshotQuality.Low, version: 7));
        await using var pipe = rig.StartPipe();
        await using var helper = new SessionHelperPipeClient(rig.PipeName);
        await helper.EnsureConnectedAsync(CancellationToken.None, timeoutMs: 15000);

        var first = await helper.PingAsync(CancellationToken.None);
        Assert.True(first.Ok);
        Assert.Equal(45, first.ScreenshotPeriodSeconds);
        Assert.Equal("Low", first.ScreenshotQuality);
        Assert.True(first.ScreenshotEnabled);
        Assert.Equal(7, first.ConfigVersion);

        // The admin drops this employee to a ten-minute interval and turns browsing off.
        rig.Coordinator.ApplyConfig(new StaffTrackingConfig
        {
            ScreenshotEnabled = true,
            ScreenshotPeriodSeconds = 600,
            ScreenshotQuality = ScreenshotQuality.High,
            TimeTrackEnabled = true,
            BrowserHistoryEnabled = false,
            ConfigVersion = 8
        });

        var second = await helper.PingAsync(CancellationToken.None);
        Assert.Equal(600, second.ScreenshotPeriodSeconds);
        Assert.Equal("High", second.ScreenshotQuality);
        Assert.False(second.BrowserHistoryEnabled);
        Assert.Equal(8, second.ConfigVersion);

        // A ping is also the liveness signal — the service now knows the helper owns capture.
        Assert.True(rig.Coordinator.PreferSessionHelper);
        Assert.True(rig.Coordinator.IsSessionHelperAlive);
    }

    [Fact]
    public async Task ConfigGatesCapture_ADisabledStreamIsDroppedRatherThanStored()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, screenshots: false, version: 3));
        await using var pipe = rig.StartPipe();
        await using var helper = new SessionHelperPipeClient(rig.PipeName);
        await helper.EnsureConnectedAsync(CancellationToken.None, timeoutMs: 15000);

        await helper.SendCaptureAsync(
            AgentEventTypes.ScreenshotMeta, "screenshot", Payload("{\"displays\":[]}"), rig.Clock.Now, CancellationToken.None);

        // §6.2 — turning screenshots off for one employee has to stop the bytes being written, not
        // merely stop them being shown. Nothing reaches the vault and nothing is queued for upload.
        Assert.Empty(await rig.PendingOfType(AgentEventTypes.ScreenshotMeta));

        rig.Coordinator.ApplyConfig(Config(period: 60, screenshots: true, version: 4));
        await helper.SendCaptureAsync(
            AgentEventTypes.ScreenshotMeta, "screenshot", Payload("{\"displays\":[]}"), rig.Clock.Now, CancellationToken.None);

        var stored = Assert.Single(await rig.PendingOfType(AgentEventTypes.ScreenshotMeta));
        using var envelope = JsonDocument.Parse(stored.PayloadJson);
        Assert.Equal(4, envelope.RootElement.GetProperty("configVersion").GetInt64());
    }

    [Fact]
    public async Task ThePipeRefusesFramesThatDoNotBelongInTheVault()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, version: 1));
        await using var pipe = rig.StartPipe();
        await using var helper = new SessionHelperPipeClient(rig.PipeName);
        await helper.EnsureConnectedAsync(CancellationToken.None, timeoutMs: 15000);

        // Everything arriving here is signed into the vault as authentic, so the pipe is a trust
        // boundary: any local process can connect on a developer box, and only the allowlist stands
        // between that and forged evidence.
        var wrongKind = await Assert.ThrowsAsync<InvalidOperationException>(() => helper.SendCaptureAsync(
            AgentEventTypes.ScreenshotMeta, "timetrack", Payload("{}"), rig.Clock.Now, CancellationToken.None));
        Assert.Contains("vault_kind_mismatch", wrongKind.Message, StringComparison.Ordinal);

        var wrongType = await Assert.ThrowsAsync<InvalidOperationException>(() => helper.SendCaptureAsync(
            AgentEventTypes.UsbEvent, "usb", Payload("{}"), rig.Clock.Now, CancellationToken.None));
        Assert.Contains("event_type_not_allowed", wrongType.Message, StringComparison.Ordinal);

        var notJson = await Assert.ThrowsAsync<InvalidOperationException>(() => helper.SendCaptureAsync(
            AgentEventTypes.TimeTrack, "timetrack", Payload("this is not json"), rig.Clock.Now, CancellationToken.None));
        Assert.Contains("bad_payload", notJson.Message, StringComparison.Ordinal);

        Assert.Equal(3, pipe.RejectedFrames);
    }

    [Fact]
    public async Task ABackdatedFrameIsClampedBeforeItIsSigned()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, version: 1));
        await using var pipe = rig.StartPipe();
        await using var helper = new SessionHelperPipeClient(rig.PipeName);
        await helper.EnsureConnectedAsync(CancellationToken.None, timeoutMs: 15000);

        // A caller-chosen timestamp is a way to backdate a record the service then signs — for
        // instance to fill in an afternoon the employee was not at the machine.
        var before = DateTimeOffset.UtcNow;
        await helper.SendCaptureAsync(
            AgentEventTypes.TimeTrack, "timetrack", Payload("{\"state\":\"Working\"}"),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var stored = Assert.Single(await rig.PendingOfType(AgentEventTypes.TimeTrack));
        using var envelope = JsonDocument.Parse(stored.PayloadJson);
        var occurred = envelope.RootElement.GetProperty("occurredAtUtc").GetDateTimeOffset();
        Assert.InRange(occurred, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task AFloodingClientIsCutOff_BeforeTheConfigGateEvenMatters()
    {
        using var root = new AgentTestRoot("tracking");
        // Every stream off, so the budget is measured with no vault writes in the way — and so this
        // also shows the budget is applied before the config gate. A disabled stream must not be a
        // free channel for a local process to hammer the service on.
        var rig = new TrackingRig(root, Config(period: 60, screenshots: false, timeTrack: false, browsing: false));
        await using var pipe = rig.StartPipe();
        await using var helper = new SessionHelperPipeClient(rig.PipeName);
        await helper.EnsureConnectedAsync(CancellationToken.None, timeoutMs: 15000);

        for (var frame = 0; frame < 60; frame++)
        {
            await helper.SendCaptureAsync(
                AgentEventTypes.TimeTrack, "timetrack", Payload("{\"n\":1}"), rig.Clock.Now, CancellationToken.None);
        }

        Assert.Equal(60, pipe.AcceptedFrames);

        // The 61st frame inside one minute drops the connection outright, so the client sees the
        // pipe close rather than a reply.
        await Assert.ThrowsAsync<InvalidOperationException>(() => helper.SendCaptureAsync(
            AgentEventTypes.TimeTrack, "timetrack", Payload("{\"n\":1}"), rig.Clock.Now, CancellationToken.None));
        Assert.Equal(1, pipe.RejectedFrames);
    }

    [Fact]
    public async Task EachCaptureStreamIsGatedIndependently()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, screenshots: false, timeTrack: true, browsing: false, version: 9));

        await rig.Coordinator.PersistFromHelperAsync(
            AgentEventTypes.ScreenshotMeta, "screenshot", Payload("{}"), rig.Clock.Now);
        await rig.Coordinator.PersistFromHelperAsync(
            AgentEventTypes.BrowserHistory, "browser_history", Payload("{}"), rig.Clock.Now);
        await rig.Coordinator.PersistFromHelperAsync(
            AgentEventTypes.TimeTrack, "timetrack", Payload("{}"), rig.Clock.Now);

        // §6.2 is per stream, not one on/off switch: an admin who turns screenshots off for someone
        // has not turned their timetrack off, and their sticker still has to work (§4.5).
        Assert.Empty(await rig.PendingOfType(AgentEventTypes.ScreenshotMeta));
        Assert.Empty(await rig.PendingOfType(AgentEventTypes.BrowserHistory));
        Assert.Single(await rig.PendingOfType(AgentEventTypes.TimeTrack));

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Coordinator.PersistFromHelperAsync(
            AgentEventTypes.UsbEvent, "usb", Payload("{}"), rig.Clock.Now));
    }

    [Fact]
    public async Task ACoordinatorNeverPinged_ReportsHelperNeverStarted_AndCapturesNothing()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, version: 2));

        // This is the whole outage in one assertion. Before any helper has EVER pinged, the health
        // flags must read "not there" — the old code returned helperAlive:true here, so a total
        // capture blackout published as a healthy install and the server could never reach its
        // "Tracking helper not running" branch.
        Assert.False(rig.Coordinator.IsSessionHelperAlive);
        Assert.False(rig.Coordinator.PreferSessionHelper);
        Assert.Equal(HelperStates.NeverStarted, rig.Coordinator.HelperState);
        Assert.Equal("no_session_helper_never_started", rig.Coordinator.CaptureUnavailableReason);

        // And the service captures nothing itself: a tick over an empty vault emits no screenshot,
        // no timetrack — the period surfaces as a real gap rather than fabricated idle.
        rig.Clock.Advance(TimeSpan.FromSeconds(120));
        await rig.Coordinator.TickAsync();
        Assert.Empty(await rig.PendingOfType(AgentEventTypes.ScreenshotMeta));
        Assert.Empty(await rig.PendingOfType(AgentEventTypes.TimeTrack));
    }

    [Fact]
    public void HelperStateDistinguishesNeverStartedFromStale()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, version: 1));

        rig.Coordinator.NoteSessionHelperAlive();
        Assert.Equal(HelperStates.Alive, rig.Coordinator.HelperState);
        Assert.True(rig.Coordinator.IsSessionHelperAlive);

        // A helper that connected once and then went quiet is "stale", NOT "never_started" — the two
        // are different facts and the admin health card should say which.
        rig.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.False(rig.Coordinator.IsSessionHelperAlive);
        Assert.Equal(HelperStates.Stale, rig.Coordinator.HelperState);
        Assert.Equal("no_session_helper_stale", rig.Coordinator.CaptureUnavailableReason);
    }

    [Fact]
    public async Task TrackingHealthFollowsRealCaptures_NotJustAliveness()
    {
        using var root = new AgentTestRoot("tracking");
        var rig = new TrackingRig(root, Config(period: 60, version: 1));

        rig.Coordinator.NoteSessionHelperAlive();
        Assert.True(rig.Coordinator.IsTrackingHealthy);

        // §14.4 — the sticker is proof the engine is running, so a helper that pings but has stopped
        // capturing must not read as healthy.
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        rig.Coordinator.NoteSessionHelperAlive();
        Assert.True(rig.Coordinator.IsSessionHelperAlive);
        Assert.False(rig.Coordinator.IsTrackingHealthy);

        await rig.Coordinator.PersistFromHelperAsync(
            AgentEventTypes.TimeTrack, "timetrack", Payload("{\"state\":\"Working\"}"), rig.Clock.Now);
        Assert.True(rig.Coordinator.IsTrackingHealthy);

        // A helper that stops pinging altogether is unhealthy regardless of the last capture.
        rig.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.False(rig.Coordinator.IsSessionHelperAlive);
        Assert.False(rig.Coordinator.IsTrackingHealthy);
    }

    private static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    private static StaffTrackingConfig Config(
        int period,
        bool screenshots = true,
        bool timeTrack = true,
        bool browsing = true,
        ScreenshotQuality quality = ScreenshotQuality.Medium,
        long version = 1)
        => new()
        {
            ScreenshotEnabled = screenshots,
            ScreenshotPeriodSeconds = period,
            ScreenshotQuality = quality,
            TimeTrackEnabled = timeTrack,
            BrowserHistoryEnabled = browsing,
            ConfigVersion = version
        };

    /// <summary>A coordinator on a real vault and a real outbox, with a clock the test drives.</summary>
    private sealed class TrackingRig
    {
        private readonly FileOutboxQueue _outbox;

        public TrackingRig(AgentTestRoot root, StaffTrackingConfig config)
        {
            var key = SecureVault.DeriveMasterKey("device-abc", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            _outbox = new FileOutboxQueue(root.Path);
            Clock = new TestClock(T0);
            Coordinator = new TrackingCoordinator(
                new SecureVault(root.Path, key),
                _outbox,
                config,
                now: Clock.Func);
            PipeName = "teamscop-test-" + Guid.NewGuid().ToString("N");
        }

        public TestClock Clock { get; }

        public TrackingCoordinator Coordinator { get; }

        public string PipeName { get; }

        public CapturePipeServer StartPipe()
        {
            var server = new CapturePipeServer(Coordinator, NullLogger<CapturePipeServer>.Instance, PipeName);
            server.Start();
            return server;
        }

        public async Task<IReadOnlyList<OutboxItem>> PendingOfType(string eventType)
        {
            var pending = await _outbox.PeekPendingAsync(200);
            return pending.Where(p => string.Equals(p.EventType, eventType, StringComparison.Ordinal)).ToList();
        }
    }
}
