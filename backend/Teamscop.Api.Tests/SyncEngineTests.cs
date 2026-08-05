using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class SyncEngineTests
{
    [Fact]
    public async Task FileOutbox_Enqueue_Peek_Ack()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var queue = new FileOutboxQueue(root);
            var item = OutboxItem.Create(AgentEventTypes.Heartbeat, new { ok = true });
            await queue.EnqueueAsync(item);
            Assert.Equal(1, queue.PendingCount);

            var pending = await queue.PeekPendingAsync(10);
            Assert.Single(pending);
            Assert.Equal(item.ClientEventId, pending[0].ClientEventId);

            await queue.AcknowledgeAsync([item.ClientEventId]);
            Assert.Equal(0, queue.PendingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyncEngine_Flush_AcksAcceptedIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var queue = new FileOutboxQueue(root);
            var item = OutboxItem.Create(AgentEventTypes.Connectivity, new { apiReachable = true });
            await queue.EnqueueAsync(item);

            var probe = new FakeProbe(true);
            var api = new FakeSyncApi();
            var engine = new SyncEngine(probe, queue, api, new SyncEngineOptions { BatchSize = 10 });
            await engine.ProbeAndRecordAsync();
            var flushed = await engine.FlushAsync("token");
            Assert.True(flushed >= 1);
            Assert.Equal(0, queue.PendingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeProbe(bool online) : IConnectivityProbe
    {
        public Task<ConnectivityStatus> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectivityStatus
            {
                IsOnline = online,
                ApiReachable = online,
                LatencyMs = 5,
                Detail = "fake"
            });
    }

    private sealed class FakeSyncApi : ISyncApiClient
    {
        public Task<IngestBatchResponse> PushBatchAsync(
            string accessToken,
            IReadOnlyList<OutboxItem> items,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new IngestBatchResponse
            {
                AcceptedIds = items.Select(i => i.ClientEventId).ToList(),
                DuplicateIds = []
            });
    }
}
