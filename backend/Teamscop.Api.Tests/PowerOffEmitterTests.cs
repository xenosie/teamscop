using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Tests;

public class PowerOffEmitterTests
{
    [Fact]
    public void Create_Builds_PowerOff_Payload()
    {
        var item = PowerOffEmitter.Create(PowerOffEmitter.ReasonServiceStop);
        Assert.Equal(AgentEventTypes.PowerOff, item.EventType);
        using var doc = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal(AgentEventTypes.PowerOff, doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(PowerOffEmitter.ReasonServiceStop, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EmitAsync_Enqueues_And_Flushes_When_Api_Reachable()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-poweroff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var queue = new FileOutboxQueue(root);
            var api = new CapturingSyncApi();
            var engine = new SyncEngine(
                new OnlineProbe(),
                queue,
                api,
                new SyncEngineOptions { BatchSize = 10 });

            await PowerOffEmitter.EmitAsync(queue, engine, accessToken: "tok");

            Assert.Contains(api.Pushed, batch =>
                batch.Any(i => i.EventType == AgentEventTypes.PowerOff));
            Assert.Equal(0, queue.PendingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OnlineProbe : IConnectivityProbe
    {
        public Task<ConnectivityStatus> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectivityStatus
            {
                IsOnline = true,
                ApiReachable = true,
                LatencyMs = 1,
                Detail = "ok"
            });
    }

    private sealed class CapturingSyncApi : ISyncApiClient
    {
        public List<IReadOnlyList<OutboxItem>> Pushed { get; } = [];

        public Task<IngestBatchResponse> PushBatchAsync(
            string accessToken,
            IReadOnlyList<OutboxItem> items,
            CancellationToken cancellationToken = default)
        {
            Pushed.Add(items);
            return Task.FromResult(new IngestBatchResponse
            {
                AcceptedIds = items.Select(i => i.ClientEventId).ToList(),
                DuplicateIds = [],
                Rejected = []
            });
        }
    }
}
