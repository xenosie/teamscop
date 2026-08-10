using System.Text.Json;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.SessionHelper;

/// <summary>
/// Interactive-session capture helper: time track / screenshots / Chrome → named pipe → StaffService vault.
/// Launched by the service into the active console session (never by an HKCU Run key, which the
/// installer's locked ACL made unusable).
///
/// The service is the only process that can append to the vault, so this helper holds no config of
/// its own: what it captures is whatever the last pipe reply said (§6.2). The ping interval is
/// therefore the per-staff config latency, which is why it is 5 seconds and not a minute.
///
/// It writes ONLY under <c>%LOCALAPPDATA%\Teamscop</c>. It must never touch
/// <c>%ProgramData%\Teamscop\Agent</c>: that tree is locked to SYSTEM+Administrators, so the old
/// <c>new LocalAgentStore(AgentRole.Staff)</c> in the first line of Main threw
/// UnauthorizedAccessException for a standard user BEFORE the first log line — a WinExe with no
/// console died leaving nothing but a generic .NET Runtime event-log entry, which is exactly why
/// zero screenshots ever reached the server.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private static async Task<int> Main(string[] args)
    {
        // HelperLog is constructed FIRST and Main is wrapped whole, so any startup failure is
        // recorded before anything can throw. Nothing above this line may allocate a path that can
        // fail — %LOCALAPPDATA% always exists for the logged-on user.
        var log = new HelperLog();
        try
        {
            // The WebP encode is this process's only real CPU cost, and it runs on the employee's
            // own machine every capture period. Below-normal priority means the encode yields to
            // whatever the person is actually doing — on §15.2 cheap hardware the difference is a
            // capture that is invisible versus one that stutters the machine every few minutes.
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Priority is best effort; capture must run either way.
            }

            return await RunAsync(log, args);
        }
        catch (Exception ex)
        {
            log.Write($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(HelperLog log, string[] args)
    {
        using var cts = new CancellationTokenSource();

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Teamscop");
        Directory.CreateDirectory(root);

        var timeTrack = new TimeTrackEngine();
        var screenshots = new ScreenshotEngine();
        var chrome = new ChromeHistoryWatcher(root);
        var config = new StaffTrackingConfig();
        var lastScreenshot = DateTimeOffset.MinValue;
        var lastTimeFlush = DateTimeOffset.UtcNow;
        var client = new SessionHelperPipeClient();
        var reconnectDelay = MinReconnectDelay;
        long lastAppliedConfigVersion = -1;

        log.Write(
            $"SessionHelper starting (pipe={SessionHelperPipeNames.Default}, root={root}, " +
            $"observesInput={timeTrack.CanObserveInput}, args={args.Length})");
        if (!timeTrack.CanObserveInput)
        {
            // Should never happen — the helper is spawned into the interactive session — but if it
            // does, say so rather than silently emit nothing.
            log.Write($"WARNING: input not observable from this process ({timeTrack.InputUnavailableReason})");
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await client.EnsureConnectedAsync(cts.Token, timeoutMs: 3000);
                    reconnectDelay = MinReconnectDelay;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Write($"Pipe not available ({ex.GetType().Name}); retrying in {reconnectDelay.TotalSeconds:0}s");
                    await Task.Delay(reconnectDelay, cts.Token);
                    reconnectDelay = reconnectDelay < MaxReconnectDelay
                        ? TimeSpan.FromSeconds(Math.Min(MaxReconnectDelay.TotalSeconds, reconnectDelay.TotalSeconds * 2))
                        : MaxReconnectDelay;
                    continue;
                }

                var reply = await client.PingAsync(cts.Token);
                ApplyConfigFromReply(config, reply);
                if (config.ConfigVersion != lastAppliedConfigVersion)
                {
                    lastAppliedConfigVersion = config.ConfigVersion;
                    log.Write(
                        $"Config v{config.ConfigVersion} applied: screenshots={config.ScreenshotEnabled}@" +
                        $"{config.ScreenshotPeriodSeconds}s/{config.ScreenshotQuality} " +
                        $"timetrack={config.TimeTrackEnabled} browsing={config.BrowserHistoryEnabled}");
                }

                if (config.TimeTrackEnabled)
                {
                    // Poll FIRST so the state the window carries is sampled across the window it
                    // describes, not after it (the old order closed the window then polled). Poll
                    // accumulates worked/idle from last-input deltas, so calling it every loop is
                    // both correct and cadence-independent.
                    timeTrack.Poll();
                    if (DateTimeOffset.UtcNow - lastTimeFlush >= TrackingDefaults.FlushPeriod)
                    {
                        lastTimeFlush = DateTimeOffset.UtcNow;
                        var window = timeTrack.CloseWindow();
                        if (window is not null)
                        {
                            await client.SendCaptureAsync(
                                AgentEventTypes.TimeTrack, "timetrack",
                                TimeTrackPayload.Serialize(window, "session_helper"),
                                window.EndedAt, cts.Token);
                        }
                    }
                }

                // No local floor on the period: TrackingConfigService validates 30 s – 24 h server
                // side, and clamping here would silently override what the admin configured (§6.2).
                if (config.ScreenshotEnabled
                    && DateTimeOffset.UtcNow - lastScreenshot >= TimeSpan.FromSeconds(config.ScreenshotPeriodSeconds))
                {
                    var result = screenshots.CaptureAllDisplays(config);
                    if (result.Ok)
                    {
                        var payload = screenshots.SerializeCaptures(result.Displays, config.ConfigVersion);
                        await client.SendCaptureAsync(
                            AgentEventTypes.ScreenshotMeta, "screenshot", payload, DateTimeOffset.UtcNow, cts.Token);
                    }
                    else if (result.UnavailableReason == ScreenshotCaptureResult.ReasonLocked)
                    {
                        // Locked or in standby: not an error and not a completed cycle. lastScreenshot
                        // is left alone so the next loop after unlock captures immediately instead of
                        // waiting out a full period that elapsed against a black screen.
                        continue;
                    }
                    else
                    {
                        log.Write($"Screenshot cycle produced nothing: {result.UnavailableReason}");
                    }

                    lastScreenshot = DateTimeOffset.UtcNow;
                }

                if (config.BrowserHistoryEnabled)
                {
                    var visits = chrome.PollNewVisits();
                    if (visits.Count > 0)
                    {
                        var payload = chrome.SerializeVisits(visits);
                        await client.SendCaptureAsync(
                            AgentEventTypes.BrowserHistory, "browser_history", payload, DateTimeOffset.UtcNow, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Write($"Loop error: {ex.GetType().Name}: {ex.Message}");
                try { await client.DisposeAsync(); } catch (IOException) { /* already gone */ }
                client = new SessionHelperPipeClient();
            }

            try
            {
                await Task.Delay(PingPeriod, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await client.DisposeAsync();
        log.Write("SessionHelper stopping");
        return 0;
    }

    private static void ApplyConfigFromReply(StaffTrackingConfig config, SessionHelperReply reply)
    {
        if (reply.ScreenshotEnabled is { } screenshotEnabled)
        {
            config.ScreenshotEnabled = screenshotEnabled;
        }

        if (reply.TimeTrackEnabled is { } timeTrackEnabled)
        {
            config.TimeTrackEnabled = timeTrackEnabled;
        }

        if (reply.BrowserHistoryEnabled is { } browserHistoryEnabled)
        {
            config.BrowserHistoryEnabled = browserHistoryEnabled;
        }

        if (reply.ScreenshotPeriodSeconds is { } screenshotPeriodSeconds)
        {
            config.ScreenshotPeriodSeconds = screenshotPeriodSeconds;
        }

        if (!string.IsNullOrWhiteSpace(reply.ScreenshotQuality)
            && Enum.TryParse<ScreenshotQuality>(reply.ScreenshotQuality, ignoreCase: true, out var quality))
        {
            config.ScreenshotQuality = quality;
        }

        if (reply.ConfigVersion is { } configVersion)
        {
            config.ConfigVersion = configVersion;
        }
    }
}
