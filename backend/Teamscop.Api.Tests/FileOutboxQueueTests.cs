using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

/// <summary>
/// C2 — the outbox is the one component that stands between §13.1 ("never drop anything") and data
/// loss, and it had no coverage at all. Everything here is about what happens to the queue on a
/// machine that is offline for a week, crashes mid-write, or is upgraded from an older agent.
///
/// The file name is the whole ordering contract: <c>{UtcNow.Ticks:D20}_{clientEventId:N}.json</c>,
/// read back with an ordinal sort. Two of these tests exist because that contract was broken once —
/// legacy <c>{guid:N}.json</c> names sort after every tick-prefixed name, so an old queue sat behind
/// every new arrival forever, and the migration guard that was meant to fix it tested the wrong
/// length and never fired.
/// </summary>
public class FileOutboxQueueTests
{
    [Fact]
    public async Task EnqueueOrderIsPreserved_ByTheMonotonicFilenamePrefix()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var item = OutboxItem.Create(AgentEventTypes.TimeTrack, new { i });
            ids.Add(item.ClientEventId);
            await queue.EnqueueAsync(item);
            AdvanceFilenameClock();
        }

        var pending = await queue.PeekPendingAsync(10);
        Assert.Equal(ids, pending.Select(p => p.ClientEventId).ToList());
        Assert.Equal(5, queue.PendingCount);

        // The ordering is a property of the name, not of enumeration order, so assert the shape.
        foreach (var name in Directory.GetFiles(PendingDir(root)).Select(f => Path.GetFileName(f)))
        {
            Assert.Matches(@"^\d{20}_[0-9a-f]{32}\.json$", name);
        }
    }

    [Fact]
    public async Task PeekReturnsTheOldestFirst_AndOnlyAsManyAsAsked()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);

        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var item = OutboxItem.Create(AgentEventTypes.Heartbeat, new { i });
            ids.Add(item.ClientEventId);
            await queue.EnqueueAsync(item);
            AdvanceFilenameClock();
        }

        var batch = await queue.PeekPendingAsync(2);
        Assert.Equal(ids.Take(2), batch.Select(b => b.ClientEventId));

        // Peek does not consume: the flush only removes what the server acknowledged.
        Assert.Equal(4, queue.PendingCount);
        Assert.Equal(ids.Take(2), (await queue.PeekPendingAsync(2)).Select(b => b.ClientEventId));
    }

    [Fact]
    public async Task LegacyFilenames_AreRenamedOnOpen_AndKeepTheirArrivalOrder()
    {
        using var root = new AgentTestRoot("outbox");
        // Create the folders the way an older agent left them, before this version ever opens.
        new FileOutboxQueue(root.Path);

        var older = WriteLegacyPending(root, AgentEventTypes.BrowserHistory, new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        var newer = WriteLegacyPending(root, AgentEventTypes.BrowserHistory, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        var upgraded = new FileOutboxQueue(root.Path);

        // Nothing is left in the legacy shape, and the new names carry the arrival time.
        var names = Directory.GetFiles(PendingDir(root), "*.json").Select(f => Path.GetFileName(f)).ToList();
        Assert.All(names, n => Assert.Matches(@"^\d{20}_[0-9a-f]{32}\.json$", n));

        var pending = await upgraded.PeekPendingAsync(10);
        Assert.Equal(new[] { older, newer }, pending.Select(p => p.ClientEventId).ToArray());
    }

    [Fact]
    public async Task AnOldQueueIsNotStarvedByNewArrivals()
    {
        using var root = new AgentTestRoot("outbox");
        new FileOutboxQueue(root.Path);

        // A week of offline buffering under the old naming scheme, then the agent is upgraded and
        // starts producing tick-prefixed names. `{guid}.json` sorts after `2026…_{guid}.json` for
        // every ordinal comparison, so before the migration this backlog never reached the server.
        var stranded = WriteLegacyPending(root, AgentEventTypes.TimeTrack, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var queue = new FileOutboxQueue(root.Path);
        for (var i = 0; i < 3; i++)
        {
            await queue.EnqueueAsync(OutboxItem.Create(AgentEventTypes.Heartbeat, new { i }));
            AdvanceFilenameClock();
        }

        var head = await queue.PeekPendingAsync(1);
        Assert.Equal(stranded, Assert.Single(head).ClientEventId);
    }

    [Fact]
    public async Task LegacyFilesArrivingAfterOpen_AreMigratedOnTheNextPeek()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);
        await queue.EnqueueAsync(OutboxItem.Create(AgentEventTypes.Heartbeat, new { n = 1 }));

        // The rename also runs on every peek, so an upgrade that races the first flush still drains.
        var late = WriteLegacyPending(root, AgentEventTypes.TimeTrack, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var pending = await queue.PeekPendingAsync(10);
        Assert.Equal(late, pending[0].ClientEventId);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task AHalfWrittenFileIsInvisible_AndACorruptRecordDoesNotBlockWhatIsBehindIt()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);

        // Enqueue writes `<name>.tmp` then renames. A crash between the two leaves the .tmp behind:
        // it must never be read as a queued event, and must never be counted as pending work.
        File.WriteAllText(Path.Combine(PendingDir(root), "00000000000000000001_" + Guid.NewGuid().ToString("N") + ".json.tmp"), "{\"hal");

        // A record whose write was torn: correct name, unparseable body. It sorts to the head, so if
        // it stopped the scan the whole queue would stall behind one bad file.
        File.WriteAllText(Path.Combine(PendingDir(root), "00000000000000000002_" + Guid.NewGuid().ToString("N") + ".json"), "{ not json");

        var good = OutboxItem.Create(AgentEventTypes.TimeTrack, new { ok = true });
        await queue.EnqueueAsync(good);

        var pending = await queue.PeekPendingAsync(10);
        Assert.Equal(good.ClientEventId, Assert.Single(pending).ClientEventId);

        // Two *.json files exist — the readable one and the torn one. The .tmp is not one of them.
        Assert.Equal(2, queue.PendingCount);
    }

    [Fact]
    public async Task AcknowledgedItemsLeavePending_AndAreNotRedelivered()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);

        var items = new List<OutboxItem>();
        for (var i = 0; i < 3; i++)
        {
            var item = OutboxItem.Create(AgentEventTypes.ScreenshotMeta, new { i });
            items.Add(item);
            await queue.EnqueueAsync(item);
            AdvanceFilenameClock();
        }

        await queue.AcknowledgeAsync(items.Take(2).Select(i => i.ClientEventId));

        Assert.Equal(1, queue.PendingCount);
        var remaining = await queue.PeekPendingAsync(10);
        Assert.Equal(items[2].ClientEventId, Assert.Single(remaining).ClientEventId);

        var sent = Directory.GetFiles(root.Combine("outbox", "sent"), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            items.Take(2).Select(i => i.ClientEventId.ToString("N")).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            sent);

        // Re-acknowledging is what a duplicate server response looks like; it must be a no-op.
        await queue.AcknowledgeAsync(items.Take(2).Select(i => i.ClientEventId));
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public async Task MarkFailed_RecordsTheAttempt_WithoutLosingTheItemOrItsPlace()
    {
        using var root = new AgentTestRoot("outbox");
        var queue = new FileOutboxQueue(root.Path);

        var first = OutboxItem.Create(AgentEventTypes.TimeTrack, new { n = 1 });
        await queue.EnqueueAsync(first);
        AdvanceFilenameClock();
        var second = OutboxItem.Create(AgentEventTypes.TimeTrack, new { n = 2 });
        await queue.EnqueueAsync(second);

        await queue.MarkFailedAsync(first.ClientEventId, new string('e', 900));
        await queue.MarkFailedAsync(first.ClientEventId, "timeout");
        await queue.MarkFailedAsync(Guid.NewGuid(), "unknown id"); // must not throw

        var pending = await queue.PeekPendingAsync(10);
        Assert.Equal(2, pending.Count);
        // §13.1 — a failed upload is retried, not dropped, and it keeps its position at the head.
        Assert.Equal(first.ClientEventId, pending[0].ClientEventId);
        Assert.Equal(2, pending[0].AttemptCount);
        Assert.Equal("timeout", pending[0].LastError);
        Assert.NotNull(pending[0].LastAttemptAt);

        await queue.MarkFailedAsync(second.ClientEventId, new string('x', 900));
        var after = await queue.PeekPendingAsync(10);
        Assert.Equal(500, after[1].LastError!.Length); // bounded so one bad server reply cannot bloat the queue
    }

    [Fact]
    public async Task PendingWorkSurvivesAProcessRestart()
    {
        using var root = new AgentTestRoot("outbox");
        var item = OutboxItem.Create(AgentEventTypes.PowerOff, new { reason = "service_stop" });
        await new FileOutboxQueue(root.Path).EnqueueAsync(item);

        // §13.1 — the buffer is on disk precisely so a reboot does not cost the machine its backlog.
        var reopened = new FileOutboxQueue(root.Path);
        var pending = await reopened.PeekPendingAsync(10);
        Assert.Equal(item.ClientEventId, Assert.Single(pending).ClientEventId);
        Assert.Equal(AgentEventTypes.PowerOff, pending[0].EventType);
        Assert.Equal(item.PayloadJson, pending[0].PayloadJson);
    }

    private static string PendingDir(AgentTestRoot root) => root.Combine("outbox", "pending");

    /// <summary>
    /// Writes a queue file exactly as the pre-upgrade agent did — <c>{clientEventId:N}.json</c> —
    /// and back-dates it, because the migration derives the sort prefix from LastWriteTimeUtc.
    /// </summary>
    private static Guid WriteLegacyPending(AgentTestRoot root, string eventType, DateTime writtenAtUtc)
    {
        var item = OutboxItem.Create(eventType, new { legacy = true });
        var path = Path.Combine(PendingDir(root), $"{item.ClientEventId:N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(item));
        File.SetLastWriteTimeUtc(path, writtenAtUtc);
        return item.ClientEventId;
    }

    /// <summary>
    /// Spins until the system clock's tick count moves on. This is not a wait for anything to
    /// happen — it establishes the precondition the FIFO contract is built on (one enqueue per
    /// tick), so the assertion never depends on two writes landing in different 100 ns windows by
    /// luck. It costs microseconds.
    /// </summary>
    private static void AdvanceFilenameClock()
    {
        var start = DateTime.UtcNow.Ticks;
        while (DateTime.UtcNow.Ticks == start)
        {
        }
    }
}
