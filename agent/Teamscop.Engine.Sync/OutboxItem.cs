using System.Text.Json;

namespace Teamscop.Engine.Sync;

public static class AgentEventTypes
{
    public const string Heartbeat = "heartbeat";
    public const string Connectivity = "connectivity";
    public const string TimeTrack = "timetrack";
    public const string ScreenshotMeta = "screenshot_meta";
    public const string BrowserHistory = "browser_history";
    public const string UsbEvent = "usb_event";
    public const string VaultAlert = "vault_alert";
}

public sealed class OutboxItem
{
    public Guid ClientEventId { get; set; } = Guid.NewGuid();
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string PayloadJson { get; set; } = "{}";
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    public static OutboxItem Create<T>(string eventType, T payload, DateTimeOffset? occurredAt = null)
        => new()
        {
            EventType = eventType,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(payload)
        };
}

public sealed class IngestBatchRequest
{
    public required IReadOnlyList<IngestEventDto> Events { get; init; }
}

public sealed class IngestEventDto
{
    public required Guid ClientEventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string PayloadJson { get; init; }
}

public sealed class IngestBatchResponse
{
    public required IReadOnlyList<Guid> AcceptedIds { get; init; }
    public required IReadOnlyList<Guid> DuplicateIds { get; init; }
}
