using System.Text.Json;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.SessionHelper;

/// <summary>
/// Interactive-session capture helper: time track / screenshots / Chrome → named pipe → StaffService vault.
/// Autostarted at user logon by the staff installer.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var store = new LocalAgentStore(AgentRole.Staff);
        var root = Path.GetDirectoryName(store.StatePath)
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "Teamscop", "Staff");
        Directory.CreateDirectory(root);

        var timeTrack = new TimeTrackEngine();
        var screenshots = new ScreenshotEngine();
        var chrome = new ChromeHistoryWatcher(root);
        var config = new StaffTrackingConfig();
        var lastScreenshot = DateTimeOffset.MinValue;
        var lastTimeFlush = DateTimeOffset.UtcNow;
        var client = new SessionHelperPipeClient();

        Console.WriteLine("Teamscop SessionHelper starting (pipe={0})", SessionHelperPipeNames.Default);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await client.EnsureConnectedAsync(cts.Token, timeoutMs: 3000);
                }
                catch
                {
                    await Task.Delay(2000, cts.Token);
                    continue;
                }

                await client.PingAsync(cts.Token);

                if (config.TimeTrackEnabled)
                {
                    _ = timeTrack.Poll();
                    if (DateTimeOffset.UtcNow - lastTimeFlush >= TimeSpan.FromSeconds(60))
                    {
                        var ended = DateTimeOffset.UtcNow;
                        var started = lastTimeFlush;
                        var segment = timeTrack.CloseSegment(ended);
                        lastTimeFlush = ended;
                        var sample = timeTrack.Poll();
                        var payload = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            sample.State,
                            sample.IdleSeconds,
                            startedAtUtc = started,
                            endedAtUtc = ended,
                            durationSeconds = segment.DurationSeconds,
                            algorithm = "last_input_hysteresis_v1",
                            source = "session_helper"
                        });
                        await client.SendCaptureAsync(
                            AgentEventTypes.TimeTrack, "timetrack", payload, ended, cts.Token);
                    }
                }

                if (config.ScreenshotEnabled
                    && DateTimeOffset.UtcNow - lastScreenshot
                    >= TimeSpan.FromSeconds(Math.Max(30, config.ScreenshotPeriodSeconds)))
                {
                    var captures = screenshots.CaptureAllDisplays(config);
                    if (captures.Count > 0)
                    {
                        var payload = screenshots.SerializeCaptures(captures, config.ConfigVersion);
                        await client.SendCaptureAsync(
                            AgentEventTypes.ScreenshotMeta, "screenshot", payload, DateTimeOffset.UtcNow, cts.Token);
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
                Console.Error.WriteLine("SessionHelper loop error: {0}", ex.Message);
                try { await client.DisposeAsync(); } catch { /* ignore */ }
                client = new SessionHelperPipeClient();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await client.DisposeAsync();
        return 0;
    }
}
