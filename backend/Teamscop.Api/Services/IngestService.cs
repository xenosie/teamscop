using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teamscop.Api.Data;
using Teamscop.Api.Options;
using Teamscop.Api.Services.Insights;
using Teamscop.Engine.Sync;
using Teamscop.Api.Errors;

namespace Teamscop.Api.Services;

public interface IIngestService
{
    Task<IngestBatchResponse> IngestBatchAsync(Guid userId, IngestBatchRequest request, CancellationToken ct);
}

public sealed class IngestService(
    AppDbContext db,
    IScreenshotBlobStorage screenshots,
    IOptions<IngestOptions> options,
    ILogger<IngestService> logger) : IIngestService
{
    private readonly IngestOptions _limits = options.Value;

    private static readonly HashSet<string> AllowedTypes =
    [
        AgentEventTypes.Heartbeat,
        AgentEventTypes.Connectivity,
        AgentEventTypes.TimeTrack,
        AgentEventTypes.ScreenshotMeta,
        AgentEventTypes.BrowserHistory,
        AgentEventTypes.UsbEvent,
        AgentEventTypes.Registration,
        AgentEventTypes.Install,
        AgentEventTypes.PowerOff,
        AgentEventTypes.Uninstall
    ];

    /// <summary>
    /// One validated event. <paramref name="Payload"/> is the JSON that will actually be stored —
    /// parseable by construction, so no read path has to defend against a body ingest already
    /// rejected.
    /// </summary>
    private sealed record Prepared(
        IngestEventDto Event,
        string EventType,
        string Payload);

    public async Task<IngestBatchResponse> IngestBatchAsync(Guid userId, IngestBatchRequest request, CancellationToken ct)
    {
        if (request.Events is null || request.Events.Count == 0)
        {
            return new IngestBatchResponse { AcceptedIds = [], DuplicateIds = [], Rejected = [] };
        }

        if (request.Events.Count > _limits.MaxBatchEvents)
        {
            throw new InvalidOperationException($"Batch too large (max {_limits.MaxBatchEvents}).");
        }

        var batchBytes = 0L;
        foreach (var e in request.Events)
        {
            batchBytes += Encoding.UTF8.GetByteCount(e.PayloadJson ?? "");
            if (batchBytes > _limits.MaxBatchBytes)
            {
                throw new InvalidOperationException($"Batch exceeds {_limits.MaxBatchBytes} byte limit.");
            }
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new SessionInvalidException("User not found.");

        // §4.5 / §1.7 — nobody monitors themselves, and an admin is monitored by nobody. Until now
        // this held only by luck: every READ path filters Role == Staff, so admin-owned rows would
        // have been stored, invisible in every view, and removed 30 days later by retention. The
        // installer puts the same capturing service on the admin's PC, so the one thing standing
        // between "the owner's own screen is in the database" and "it is not" was the agent
        // choosing not to send. The server is the last line of defence, so it refuses.
        if (user.Role != UserRole.Staff)
        {
            logger.LogWarning(
                "Rejected an ingest batch of {Count} event(s) from non-staff user {UserId} ({Role})",
                request.Events.Count, user.Id, user.Role);
            throw new UnauthorizedAccessException("Only staff machines report tracking data.");
        }

        // The (UserId, ClientEventId) unique index is the ONLY dedup rule. A batch that arrives
        // late — or out of vault order after a reconnect — is stored, never dropped (§13.1).
        var ids = request.Events.Select(e => e.ClientEventId).Distinct().ToList();
        var seen = (await db.AgentEvents
                .Where(e => e.UserId == userId && ids.Contains(e.ClientEventId))
                .Select(e => e.ClientEventId)
                .ToListAsync(ct))
            .ToHashSet();

        var duplicates = new List<Guid>();
        var rejected = new List<IngestRejectedItem>();
        var prepared = Prepare(request.Events, seen, duplicates, rejected);

        // The staff member's quality setting is the §3.2 per-display budget. Loaded once, only when
        // the batch actually carries a capture, so ordinary heartbeat/timetrack batches pay nothing.
        string? screenshotQuality = null;
        if (prepared.Any(p => p.EventType == AgentEventTypes.ScreenshotMeta))
        {
            screenshotQuality = await db.StaffTrackingConfigs.AsNoTracking()
                .Where(c => c.StaffUserId == user.Id)
                .Select(c => c.ScreenshotQuality)
                .FirstOrDefaultAsync(ct);
        }

        var accepted = new List<Guid>(prepared.Count);
        foreach (var item in prepared)
        {
            var eventId = Guid.NewGuid();
            var payloadToStore = item.EventType == AgentEventTypes.ScreenshotMeta
                ? await ExtractScreenshotBlobsAsync(user.Id, eventId, item.Payload, screenshotQuality, ct)
                : item.Payload;

            var row = new AgentEvent
            {
                Id = eventId,
                CompanyId = user.CompanyId,
                UserId = user.Id,
                ClientEventId = item.Event.ClientEventId,
                EventType = item.EventType,
                OccurredAt = item.Event.OccurredAt.ToUniversalTime(),
                ReceivedAt = DateTimeOffset.UtcNow,
                PayloadJson = payloadToStore
            };

            // Parse the timetrack window once, here, so every read path is a column read.
            if (item.EventType == AgentEventTypes.TimeTrack
                && TimeTrackSegmentReader.TryRead(item.Payload, out var segment))
            {
                var seconds = (int)Math.Clamp(segment.DurationSeconds, 0, int.MaxValue);
                row.SegmentStartedAt = segment.Start.ToUniversalTime();
                row.WorkedSeconds = segment.Working ? seconds : 0;
                row.IdleSeconds = segment.Working ? 0 : seconds;
            }

            db.AgentEvents.Add(row);
            accepted.Add(item.Event.ClientEventId);
            ApplyLiveness(user, item);
        }

        await db.SaveChangesAsync(ct);
        return new IngestBatchResponse
        {
            AcceptedIds = accepted,
            DuplicateIds = duplicates.Distinct().ToList(),
            Rejected = rejected
        };
    }

    /// <summary>
    /// Validates every event and reads its vault envelope in one pass, so a payload is parsed once
    /// rather than once to validate it and again to find the sequence.
    /// </summary>
    private List<Prepared> Prepare(
        IReadOnlyList<IngestEventDto> events,
        HashSet<Guid> seen,
        List<Guid> duplicates,
        List<IngestRejectedItem> rejected)
    {
        var prepared = new List<Prepared>(events.Count);
        foreach (var evt in events.OrderBy(e => e.OccurredAt))
        {
            if (!seen.Add(evt.ClientEventId))
            {
                duplicates.Add(evt.ClientEventId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(evt.EventType) || !AllowedTypes.Contains(evt.EventType))
            {
                Reject(evt.ClientEventId, $"Unsupported event type: {evt.EventType}");
                continue;
            }

            if (evt.PayloadJson is null || evt.PayloadJson.Length > _limits.MaxPayloadChars)
            {
                Reject(evt.ClientEventId, "Payload too large or missing.");
                continue;
            }

            var payload = string.IsNullOrWhiteSpace(evt.PayloadJson) ? "{}" : evt.PayloadJson;
            using var doc = TryParse(payload);
            if (doc is null)
            {
                Reject(evt.ClientEventId, "Invalid payloadJson.");
                continue;
            }

            prepared.Add(new Prepared(evt, evt.EventType, payload));
        }

        return prepared;

        void Reject(Guid clientEventId, string reason)
        {
            // Nothing was stored, so the id must not shadow a later copy in the same batch.
            seen.Remove(clientEventId);
            rejected.Add(new IngestRejectedItem { ClientEventId = clientEventId, Reason = reason });
        }
    }

    private static void ApplyLiveness(UserAccount user, Prepared item)
    {
        // §14 — an authorised uninstall is what makes a machine yellow rather than merely silent.
        // Recorded from the event because the uninstaller sends it at the point of commitment, so
        // it arrives while the machine is still able to talk.
        if (item.EventType == AgentEventTypes.Uninstall)
        {
            user.UninstalledAt = DateTimeOffset.UtcNow;
            return;
        }

        // A fresh registration means the product is back on this machine.
        if (item.EventType == AgentEventTypes.Registration)
        {
            user.UninstalledAt = null;
            return;
        }

        if (item.EventType is not (AgentEventTypes.Heartbeat or AgentEventTypes.Connectivity))
        {
            return;
        }

        user.LastHeartbeatAt = DateTimeOffset.UtcNow;
        user.LastSeenAt = DateTimeOffset.UtcNow;
        if (item.EventType != AgentEventTypes.Connectivity)
        {
            user.LastOnline = true;
            return;
        }

        using var doc = TryParse(item.Payload);
        if (doc is not null
            && AgentEventPayload.Bool(doc.RootElement, "apiReachable", "ApiReachable") is { } reachable)
        {
            user.LastOnline = reachable;
        }
    }

    /// <summary>
    /// Moves the captured WebP images out of the row and leaves compact metadata behind (B4), so no
    /// read path ever pulls image bytes out of PostgreSQL to answer "how many displays, how big". A
    /// payload that is not the capture envelope is stored as sent; a failure to write the blob
    /// propagates, because a half-stored capture the agent believes it delivered is a silent drop.
    ///
    /// The wire carries <c>webpBase64</c> (§3.1); <c>jpegBase64</c> is still accepted so a mixed
    /// fleet mid-rollout never drops a frame. <paramref name="quality"/> is the staff member's §3.2
    /// budget — a display far over it is logged, never rejected (§13.1).
    /// </summary>
    private async Task<string> ExtractScreenshotBlobsAsync(
        Guid userId, Guid eventId, string payloadJson, string? quality, CancellationToken ct)
    {
        using var inner = AgentEventPayload.TryOpen(payloadJson, "displays");
        if (inner is null
            || !inner.RootElement.TryGetProperty("displays", out var displays)
            || displays.ValueKind != JsonValueKind.Array)
        {
            return payloadJson;
        }

        var warnAbove = ScreenshotBudget.WarnAbove(quality);
        var metaDisplays = new List<object>();
        var i = 0;
        foreach (var d in displays.EnumerateArray())
        {
            i++;
            var idx = AgentEventPayload.Int(d, "displayIndex", "DisplayIndex") ?? i;
            byte[]? image = null;
            if (AgentEventPayload.String(d, "webpBase64", "WebpBase64", "jpegBase64", "JpegBase64") is { Length: > 0 } imageBase64
                && TryDecode(imageBase64, eventId) is { Length: > 0 } decoded)
            {
                image = decoded;
                if (image.Length > warnAbove)
                {
                    logger.LogWarning(
                        "Screenshot display {Display} for user {UserId} is {Size} bytes, over the {Quality} budget",
                        idx, userId, image.Length, quality ?? "Medium");
                }

                await screenshots.WriteDisplayAsync(userId, eventId, idx, image, ct);
            }

            metaDisplays.Add(new
            {
                displayIndex = idx,
                width = AgentEventPayload.Int(d, "width", "Width") ?? 0,
                height = AgentEventPayload.Int(d, "height", "Height") ?? 0,
                size = image?.Length ?? AgentEventPayload.Int(d, "size", "Size") ?? 0
            });
        }

        return JsonSerializer.Serialize(new
        {
            storage = "file",
            displays = metaDisplays
        });
    }

    private byte[]? TryDecode(string base64, Guid eventId)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Screenshot display for event {EventId} was not valid base64", eventId);
            return null;
        }
    }

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
