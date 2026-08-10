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
    // Phase 9 — App history (lifecycle)
    public const string Registration = "registration";
    public const string PowerOff = "power_off";
    public const string Uninstall = "uninstall";

    /// <summary>
    /// One row per completed install/upgrade, emitted by the service on its first loop after the
    /// installer stamps the machine. OccurredAt carries the installer's own timestamp, so the app
    /// history shows exactly WHEN the product landed, not when the service first got around to
    /// reporting it.
    /// </summary>
    public const string Install = "install";

    /// <summary>
    /// §14 — a change in a machine's four-state status, written by the SERVER, never by an agent.
    /// It records what the server concluded from the evidence, so it must not be accepted from
    /// ingest: an agent that could post its own status could declare itself healthy.
    /// </summary>
    public const string AppStatus = "app_status";
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

public sealed class IngestRejectedItem
{
    public Guid ClientEventId { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class IngestBatchResponse
{
    public required IReadOnlyList<Guid> AcceptedIds { get; init; }
    public required IReadOnlyList<Guid> DuplicateIds { get; init; }
    public IReadOnlyList<IngestRejectedItem> Rejected { get; init; } = [];
}
