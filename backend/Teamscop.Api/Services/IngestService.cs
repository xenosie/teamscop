using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teamscop.Api.Data;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services;

public interface IIngestService
{
    Task<IngestBatchResponse> IngestBatchAsync(Guid userId, IngestBatchRequest request, CancellationToken ct);
}

public sealed class IngestService(AppDbContext db) : IIngestService
{
    private static readonly HashSet<string> AllowedTypes =
    [
        AgentEventTypes.Heartbeat,
        AgentEventTypes.Connectivity,
        AgentEventTypes.TimeTrack,
        AgentEventTypes.ScreenshotMeta,
        AgentEventTypes.BrowserHistory,
        AgentEventTypes.UsbEvent,
        AgentEventTypes.VaultAlert
    ];

    public async Task<IngestBatchResponse> IngestBatchAsync(Guid userId, IngestBatchRequest request, CancellationToken ct)
    {
        if (request.Events is null || request.Events.Count == 0)
        {
            return new IngestBatchResponse { AcceptedIds = [], DuplicateIds = [] };
        }

        if (request.Events.Count > 200)
        {
            throw new InvalidOperationException("Batch too large (max 200).");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        var ids = request.Events.Select(e => e.ClientEventId).Distinct().ToList();
        var existing = await db.AgentEvents
            .Where(e => e.UserId == userId && ids.Contains(e.ClientEventId))
            .Select(e => e.ClientEventId)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        var seqState = await db.AgentSequenceStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (seqState is null)
        {
            seqState = new AgentSequenceState { UserId = userId, LastVaultSequence = 0 };
            db.AgentSequenceStates.Add(seqState);
        }

        var accepted = new List<Guid>();
        var duplicates = new List<Guid>();

        foreach (var evt in request.Events.OrderBy(e => e.OccurredAt))
        {
            if (existingSet.Contains(evt.ClientEventId) || accepted.Contains(evt.ClientEventId))
            {
                duplicates.Add(evt.ClientEventId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(evt.EventType) || !AllowedTypes.Contains(evt.EventType))
            {
                throw new InvalidOperationException($"Unsupported event type: {evt.EventType}");
            }

            if (evt.PayloadJson.Length > 2_000_000)
            {
                throw new InvalidOperationException("Payload too large.");
            }

            try
            {
                using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(evt.PayloadJson) ? "{}" : evt.PayloadJson);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"Invalid payloadJson for {evt.ClientEventId}");
            }

            long? vaultSeq = null;
            string? chainHash = null;
            DateTime? businessOccurredAt = null;
            string? businessTz = null;
            long? businessClockVersion = null;
            try
            {
                using var doc = JsonDocument.Parse(evt.PayloadJson);
                if (doc.RootElement.TryGetProperty("vaultSequence", out var seqEl) && seqEl.TryGetInt64(out var seq))
                {
                    vaultSeq = seq;
                }
                else if (doc.RootElement.TryGetProperty("VaultSequence", out var seqEl2) && seqEl2.TryGetInt64(out var seq2))
                {
                    vaultSeq = seq2;
                }

                if (doc.RootElement.TryGetProperty("chainHash", out var hashEl))
                {
                    chainHash = hashEl.GetString();
                }
                else if (doc.RootElement.TryGetProperty("ChainHash", out var hashEl2))
                {
                    chainHash = hashEl2.GetString();
                }

                if (doc.RootElement.TryGetProperty("businessLocal", out var bizEl))
                {
                    if (DateTime.TryParse(bizEl.GetString(), out var bizDt))
                    {
                        businessOccurredAt = bizDt;
                    }
                }

                if (doc.RootElement.TryGetProperty("businessTimeZoneId", out var tzEl))
                {
                    businessTz = tzEl.GetString();
                }

                if (doc.RootElement.TryGetProperty("businessClockVersion", out var verEl) && verEl.TryGetInt64(out var ver))
                {
                    businessClockVersion = ver;
                }
            }
            catch (JsonException)
            {
                // ignore
            }

            if (vaultSeq is long incomingSeq && incomingSeq > 0)
            {
                if (seqState.LastVaultSequence > 0 && incomingSeq > seqState.LastVaultSequence + 1)
                {
                    seqState.GapCount += incomingSeq - seqState.LastVaultSequence - 1;
                }

                if (incomingSeq > seqState.LastVaultSequence)
                {
                    seqState.LastVaultSequence = incomingSeq;
                    seqState.LastChainHash = chainHash;
                    seqState.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            db.AgentEvents.Add(new AgentEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = user.CompanyId,
                UserId = user.Id,
                ClientEventId = evt.ClientEventId,
                EventType = evt.EventType,
                OccurredAt = evt.OccurredAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                PayloadJson = evt.PayloadJson,
                VaultSequence = vaultSeq,
                ChainHash = chainHash,
                BusinessOccurredAt = businessOccurredAt,
                BusinessTimeZoneId = businessTz,
                BusinessClockVersion = businessClockVersion
            });
            accepted.Add(evt.ClientEventId);
            existingSet.Add(evt.ClientEventId);

            if (evt.EventType is AgentEventTypes.Heartbeat or AgentEventTypes.Connectivity)
            {
                user.LastHeartbeatAt = DateTimeOffset.UtcNow;
                user.LastSeenAt = DateTimeOffset.UtcNow;
                if (evt.EventType == AgentEventTypes.Connectivity)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(evt.PayloadJson);
                        if (doc.RootElement.TryGetProperty("apiReachable", out var reachable))
                        {
                            user.LastOnline = reachable.GetBoolean();
                        }
                        else if (doc.RootElement.TryGetProperty("ApiReachable", out var reachablePascal))
                        {
                            user.LastOnline = reachablePascal.GetBoolean();
                        }
                    }
                    catch (JsonException)
                    {
                        // ignore
                    }
                }
                else
                {
                    user.LastOnline = true;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new IngestBatchResponse { AcceptedIds = accepted, DuplicateIds = duplicates.Distinct().ToList() };
    }
}
