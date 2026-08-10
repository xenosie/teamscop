using System.Text;
using System.Text.Json;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.Api.Tests;

public class TrackingCoordinatorRecoveryTests
{
    [Fact]
    public async Task FirstTick_ReEnqueuesRecordsCommittedButNeverEnqueued()
    {
        var root = Path.Combine(Path.GetTempPath(), "teamscop-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = SecureVault.DeriveMasterKey("device-abc", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

            // Model a crash between vault commit and outbox enqueue: two committed vault records,
            // an empty outbox, and no MarkEnqueued.
            var seededVault = new SecureVault(root, key);
            var firstId = Guid.NewGuid();
            seededVault.Append(new VaultRecord
            {
                RecordId = firstId,
                Kind = "timetrack",
                OccurredAt = DateTimeOffset.UtcNow,
                PlainPayload = Encoding.UTF8.GetBytes("{\"state\":\"Working\"}")
            });
            seededVault.Append(new VaultRecord
            {
                RecordId = Guid.NewGuid(),
                Kind = "browser_history",
                OccurredAt = DateTimeOffset.UtcNow,
                PlainPayload = Encoding.UTF8.GetBytes("{\"visits\":[]}")
            });

            var vault = new SecureVault(root, key);
            var outbox = new FileOutboxQueue(root);
            var config = new StaffTrackingConfig
            {
                TimeTrackEnabled = false,
                ScreenshotEnabled = false,
                BrowserHistoryEnabled = false
            };
            var coordinator = new TrackingCoordinator(vault, outbox, config);

            await coordinator.TickAsync();

            var pending = await outbox.PeekPendingAsync(10);
            Assert.Equal(2, pending.Count);

            // The RecordId becomes the ClientEventId so the server deduplicates a recovery re-send.
            Assert.Contains(pending, p => p.ClientEventId == firstId);

            var recovered = pending.Single(p => p.ClientEventId == firstId);
            Assert.Equal(AgentEventTypes.TimeTrack, recovered.EventType);
            using var doc = JsonDocument.Parse(recovered.PayloadJson);
            // The envelope carries the captured payload; the vault sequence and chain hash it used to
            // ride with are gone (§1.1). The RecordId → ClientEventId is what dedups a re-send.
            Assert.Equal("{\"state\":\"Working\"}", DecodePayload(doc.RootElement));

            // A second start finds nothing left to recover — the watermark advanced.
            var reopened = new TrackingCoordinator(new SecureVault(root, key), new FileOutboxQueue(root), config);
            await reopened.TickAsync();
            Assert.Empty(vault.ReadUnenqueued());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string DecodePayload(JsonElement envelope)
        => Encoding.UTF8.GetString(Convert.FromBase64String(envelope.GetProperty("payloadBase64").GetString()!));
}
