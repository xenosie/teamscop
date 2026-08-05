using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.StaffService;

/// <summary>
/// Staff background loop: tracking (low priority) + connectivity + durable encrypted vault + sync flush.
/// </summary>
public sealed class StaffAgentWorker(
    ILogger<StaffAgentWorker> logger,
    IConfiguration configuration,
    LocalAgentStore store,
    LifecycleApiClient lifecycleApi,
    SyncEngine syncEngine,
    TrackingCoordinator tracking,
    ConfigRealtimeClient configClient) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep this service from preempting interactive work.
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* ignore */ }

        var policy = RolePolicy.For(AgentRole.Staff);
        logger.LogInformation(
            "Teamscop staff agent starting. InstallRoot={Root} Service={Service}",
            policy.RecommendedInstallRoot,
            ServiceInstallerHints.ServiceName);

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

                        configClient.ConfigChanged += cfg =>
                        {
                            tracking.ApplyConfig(cfg);
                            logger.LogInformation("Tracking config updated v{Version} quality={Quality} period={Period}s",
                                cfg.ConfigVersion, cfg.ScreenshotQuality, cfg.ScreenshotPeriodSeconds);
                        };
                        configStarted = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Config realtime not ready; will retry");
                    }
                }

                // Tracking first writes local vault; sync later pushes if online.
                await tracking.TickAsync(stoppingToken);

                var status = await syncEngine.ProbeAndRecordAsync(stoppingToken);
                logger.LogDebug(
                    "online={Online} pending={Pending} configV={ConfigV}",
                    status.ApiReachable,
                    syncEngine.PendingCount,
                    tracking.Config.ConfigVersion);

                if (!string.IsNullOrWhiteSpace(state.AccessToken))
                {
                    await syncEngine.EnqueueAsync(
                        OutboxItem.Create(AgentEventTypes.Heartbeat, new
                        {
                            deviceKey = state.DeviceKey,
                            role = state.Role ?? "staff",
                            pending = syncEngine.PendingCount
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
        logger.LogInformation("Teamscop staff agent stopping.");
    }
}
