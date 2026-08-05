using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;
using Teamscop.Engine.Usb;

namespace Teamscop.StaffService;

/// <summary>
/// Staff background loop: USB gate + tracking + business clock + connectivity + vault + sync flush.
/// </summary>
public sealed class StaffAgentWorker(
    ILogger<StaffAgentWorker> logger,
    IConfiguration configuration,
    LocalAgentStore store,
    LifecycleApiClient lifecycleApi,
    SyncEngine syncEngine,
    TrackingCoordinator tracking,
    ConfigRealtimeClient configClient,
    UsbSessionController usb) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* ignore */ }

        var policy = RolePolicy.For(AgentRole.Staff);
        logger.LogInformation(
            "Teamscop staff agent starting. InstallRoot={Root} Service={Service}",
            policy.RecommendedInstallRoot,
            ServiceInstallerHints.ServiceName);

        try
        {
            await usb.StartAsync(stoppingToken);
            logger.LogInformation(
                "USB mass-storage gate active. PolicyBlocked={Blocked} Supported={Supported}",
                usb.PolicyBlocked,
                usb.PolicySupported);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "USB gate failed to start; continuing without USB block");
        }

        var loopSeconds = configuration.GetValue("Agent:SyncSeconds", 30);
        var configStarted = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = store.Load();

                if (!configStarted && !string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    try
                    {
                        await configClient.StartAsync(state.AccessToken, stoppingToken);
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                        var snap = await configClient.PullSnapshotAsync(http, state.AccessToken, stoppingToken);
                        if (snap is not null)
                        {
                            tracking.ApplyConfig(snap);
                        }

                        var biz = await configClient.PullBusinessTimeAsync(http, state.AccessToken, stoppingToken);
                        if (biz is not null)
                        {
                            tracking.ApplyBusinessClock(biz);
                        }

                        configClient.ConfigChanged += cfg =>
                        {
                            tracking.ApplyConfig(cfg);
                            logger.LogInformation("Tracking config updated v{Version}", cfg.ConfigVersion);
                        };
                        configClient.BusinessTimeChanged += cfg =>
                        {
                            tracking.ApplyBusinessClock(cfg);
                            logger.LogInformation(
                                "Business clock synced v{Version} tz={Tz} localAnchor={Y}-{M}-{D} {h}:{m}:{s}",
                                cfg.ClockVersion, cfg.TimeZoneId,
                                cfg.AnchorYear, cfg.AnchorMonth, cfg.AnchorDay,
                                cfg.AnchorHour, cfg.AnchorMinute, cfg.AnchorSecond);
                        };
                        configStarted = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Config realtime not ready; will retry");
                    }
                }

                await tracking.TickAsync(stoppingToken);

                var status = await syncEngine.ProbeAndRecordAsync(stoppingToken);
                var bizNow = tracking.BusinessClock.Now();
                logger.LogDebug(
                    "online={Online} pending={Pending} biz={Biz} v={ClockV}",
                    status.ApiReachable,
                    syncEngine.PendingCount,
                    bizNow.BusinessLocalIso,
                    bizNow.ClockVersion);

                if (!string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    await syncEngine.EnqueueAsync(
                        OutboxItem.Create(AgentEventTypes.Heartbeat, new
                        {
                            deviceKey = state.DeviceKey,
                            role = state.Role ?? "staff",
                            pending = syncEngine.PendingCount,
                            businessLocal = bizNow.BusinessLocalIso,
                            businessTimeZoneId = bizNow.TimeZoneId,
                            businessClockVersion = bizNow.ClockVersion,
                            businessSynchronized = bizNow.Synchronized
                        }),
                        stoppingToken);

                    if (status.ApiReachable)
                    {
                        try { await lifecycleApi.HeartbeatAsync(state.AccessToken, stoppingToken); }
                        catch (Exception ex) { logger.LogWarning(ex, "Lifecycle heartbeat failed"); }

                        var flushed = await syncEngine.FlushAsync(state.AccessToken, stoppingToken);
                        if (flushed > 0)
                        {
                            logger.LogInformation("Flushed {Count} outbox events", flushed);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Staff agent iteration failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(loopSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await configClient.DisposeAsync();
        await usb.DisposeAsync();
        logger.LogInformation("Teamscop staff agent stopping.");
    }
}
