using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class AppBrokenWatchdogTests
{
    [Fact]
    public async Task Tick_Emits_Once_When_Required_File_Missing_Then_Debounces()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-appbroken-" + Guid.NewGuid().ToString("N"));
        var outboxRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outboxRoot);
        try
        {
            File.WriteAllText(Path.Combine(root, "keep.dll"), "ok");
            File.WriteAllText(Path.Combine(root, "gone.dll"), "ok");

            var queue = new FileOutboxQueue(outboxRoot);
            var watchdog = new AppBrokenWatchdog(root, queue, ["keep.dll", "gone.dll"]);

            File.Delete(Path.Combine(root, "gone.dll"));

            Assert.True(await watchdog.TickAsync());
            Assert.False(await watchdog.TickAsync());
            Assert.Equal(1, queue.PendingCount);

            var pending = await queue.PeekPendingAsync(10);
            Assert.Equal(AgentEventTypes.AppBroken, pending[0].EventType);
            using var doc = JsonDocument.Parse(pending[0].PayloadJson);
            Assert.Equal(AgentEventTypes.AppBroken, doc.RootElement.GetProperty("kind").GetString());
            Assert.Contains(
                doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()),
                m => m == "gone.dll");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Tick_Emits_When_File_Hash_Altered_Then_Resets_After_Healthy()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-appbroken-" + Guid.NewGuid().ToString("N"));
        var outboxRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outboxRoot);
        try
        {
            var target = Path.Combine(root, "core.dll");
            File.WriteAllText(target, "v1");

            var queue = new FileOutboxQueue(outboxRoot);
            var watchdog = new AppBrokenWatchdog(root, queue, ["core.dll"]);
            Assert.False(await watchdog.TickAsync());

            File.WriteAllText(target, "v2-tampered");
            Assert.True(await watchdog.TickAsync());
            Assert.False(await watchdog.TickAsync());

            var report = watchdog.Inspect();
            Assert.Contains("core.dll", report.Altered);

            // Restore original content → healthy, debounce clears, new incident can fire later.
            File.WriteAllText(target, "v1");
            Assert.False(await watchdog.TickAsync());
            Assert.Null(watchdog.LastEmittedFingerprint);

            File.WriteAllText(target, "v3");
            Assert.True(await watchdog.TickAsync());
            Assert.Equal(2, queue.PendingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Inspect_Healthy_When_All_Required_Present()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-appbroken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.dll"), "a");
            File.WriteAllText(Path.Combine(root, "b.dll"), "b");
            var queue = new FileOutboxQueue(Path.Combine(root, "outbox-data"));
            var watchdog = new AppBrokenWatchdog(root, queue, ["a.dll", "b.dll"]);
            Assert.True(watchdog.Inspect().IsHealthy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
