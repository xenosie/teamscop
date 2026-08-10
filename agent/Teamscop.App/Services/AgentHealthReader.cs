using System.Text.Json;
using System.Text.Json.Serialization;
using Teamscop.App.Composition;

namespace Teamscop.App.Services;

/// <summary>What the sticker says about the engine. Ordered worst to best; the dot colour follows.</summary>
public enum AgentHealthStatus
{
    /// <summary>Nothing can be confirmed either way. Never presented as "fine".</summary>
    Unknown,

    /// <summary>The engine is not doing its job, or nothing has arrived for long enough to say so.</summary>
    NotReporting,

    /// <summary>Working, but behind: a queue is draining or the last upload is old.</summary>
    CatchingUp,

    /// <summary>Capture running, data uploading, last sync recent — the proof §14.4 asks for.</summary>
    Protected
}

/// <summary>
/// The engine's own account of itself, written by the staff service each loop. Every field is
/// optional: an older install writes no file at all, and the sticker composes what it can observe
/// instead (see <c>TimeTrackStickerViewModel</c>).
/// </summary>
public sealed class AgentHealthFile
{
    [JsonPropertyName("writtenAtUtc")] public DateTimeOffset? WrittenAtUtc { get; set; }
    [JsonPropertyName("helperAlive")] public bool? HelperAlive { get; set; }
    [JsonPropertyName("trackingOk")] public bool? TrackingOk { get; set; }
    [JsonPropertyName("pendingOutbox")] public int? PendingOutbox { get; set; }
    [JsonPropertyName("lastCaptureAtUtc")] public DateTimeOffset? LastCaptureAtUtc { get; set; }
    [JsonPropertyName("lastUploadOkAtUtc")] public DateTimeOffset? LastUploadOkAtUtc { get; set; }
    [JsonPropertyName("loopSeconds")] public int? LoopSeconds { get; set; }
}

/// <summary>
/// A15 / §14.4 — reads <c>agent-health.json</c> from the agent's own directory, beside
/// <c>agent-state.json</c>. Polled rather than watched: the sticker asks at most once a minute, so
/// a FileSystemWatcher would cost a native handle and a callback thread to save one small read on
/// cheap hardware (§15.2).
///
/// The staff service does not write this file yet; until it does, <see cref="TryRead"/> returns
/// null and the sticker falls back to what the app can observe for itself. The expected contract
/// is <see cref="AgentHealthFile"/>.
/// </summary>
public sealed class AgentHealthReader
{
    public const string FileName = "agent-health.json";

    /// <summary>Loops missed before the engine counts as silent. Three is one full retry cycle.</summary>
    private const int StaleLoops = 3;

    /// <summary>Used when the file omits loopSeconds; the staff worker's default cadence.</summary>
    private static readonly TimeSpan DefaultLoop = TimeSpan.FromSeconds(30);

    /// <summary>A queue this deep is a backlog worth showing, not ordinary in-flight work.</summary>
    private const int BacklogThreshold = 50;

    private static readonly TimeSpan UploadStaleAfter = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SessionStore _session;
    private readonly UiLog _log;
    private bool _loggedMissing;

    public AgentHealthReader(SessionStore session, UiLog log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>Beside the session state, so it follows whichever store this session uses.</summary>
    public string Path
    {
        get
        {
            var directory = System.IO.Path.GetDirectoryName(_session.StatePath);
            return string.IsNullOrWhiteSpace(directory)
                ? FileName
                : System.IO.Path.Combine(directory, FileName);
        }
    }

    /// <summary>The file's contents, or null when it is absent or unreadable. Never throws.</summary>
    public AgentHealthFile? TryRead()
    {
        var path = Path;
        try
        {
            if (!File.Exists(path))
            {
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    _log.Debug($"No {FileName} at {path} — the sticker reports from observed data");
                }

                return null;
            }

            _loggedMissing = false;
            return JsonSerializer.Deserialize<AgentHealthFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written or locked file is not a healthy signal, but it is not a crash either.
            _log.Debug($"{FileName} unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The engine's state, decided from the file alone. A stale file is treated exactly like a
    /// dead engine: the whole point of §14.4 is that "cannot confirm" never looks like "fine".
    /// </summary>
    public static (AgentHealthStatus Status, string Headline, string Detail) Evaluate(
        AgentHealthFile file, DateTimeOffset nowUtc, CompanyClock clock)
    {
        var loop = file.LoopSeconds is > 0
            ? TimeSpan.FromSeconds(file.LoopSeconds.Value)
            : DefaultLoop;

        if (file.WrittenAtUtc is not { } writtenAt || nowUtc - writtenAt > loop * StaleLoops)
        {
            return (
                AgentHealthStatus.NotReporting,
                "Not reporting",
                file.WrittenAtUtc is { } stale
                    ? $"The tracking service last ran at {clock.FormatTime(stale)}."
                    : "The tracking service is not running.");
        }

        if (file.HelperAlive == false)
        {
            return (AgentHealthStatus.NotReporting, "Not reporting", "The capture helper is not running.");
        }

        if (file.TrackingOk == false)
        {
            return (AgentHealthStatus.NotReporting, "Not reporting", "Capture is failing on this machine.");
        }

        var pending = file.PendingOutbox ?? 0;
        var uploadStale = file.LastUploadOkAtUtc is { } upload && nowUtc - upload > UploadStaleAfter;
        if (pending > BacklogThreshold || uploadStale)
        {
            return (
                AgentHealthStatus.CatchingUp,
                "Catching up",
                file.LastUploadOkAtUtc is { } upload2
                    ? $"{pending} queued · last upload {clock.FormatTime(upload2)}."
                    : $"{pending} queued · nothing uploaded yet.");
        }

        var capture = file.LastCaptureAtUtc is { } shot
            ? $"Capture OK · last screenshot {clock.FormatTime(shot)}"
            : "Capture OK";
        var upload3 = file.LastUploadOkAtUtc is { } ok
            ? $" · uploaded {clock.FormatTime(ok)}"
            : string.Empty;
        return (AgentHealthStatus.Protected, "Protected", $"{capture} · {pending} queued{upload3}.");
    }
}
