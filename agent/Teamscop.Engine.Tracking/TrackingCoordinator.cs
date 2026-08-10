using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Tracking;

/// <summary>How the interactive capture helper is doing, as one word the admin can be shown.</summary>
public static class HelperStates
{
    /// <summary>No helper has EVER connected. Distinct from "stale" on purpose — see below.</summary>
    public const string NeverStarted = "never_started";

    /// <summary>A helper connected once and has now gone quiet.</summary>
    public const string Stale = "stale";

    public const string Alive = "alive";
}

/// <summary>
/// Low-impact tracking orchestrator for the LocalSystem service:
/// 1) write encrypted+compressed vault records the helper sends (§1.2 keeps the data unreadable on disk)
/// 2) enqueue sync outbox items stamped with company business time
///
/// <b>The service captures nothing itself, by design.</b> It used to fall back to in-process capture
/// whenever no helper had connected, and every one of those three streams is impossible from
/// session 0: <c>GetLastInputInfo</c> reports the calling session (so idle equalled machine uptime
/// and every window was emitted as Rest), the service window station is attached to no display (so
/// screenshots were always empty), and <c>LocalApplicationData</c> under LocalSystem is
/// <c>systemprofile</c>, where no Chrome profile exists. The timetrack half was the worst of the
/// three: it produced an unbroken chain of fabricated Rest windows, claiming the employee was
/// present when the engine was in fact blind.
///
/// §5.4 ("PC off, asleep, or agent not running → rendered as idle") is a RENDERING rule for absent
/// data. It is not a licence to invent idle records: emitting nothing renders identically in the
/// timeline, which is the honest fact.
/// </summary>
public sealed class TrackingCoordinator
{
    /// <summary>No ping for this long and the helper is not there.</summary>
    public static readonly TimeSpan HelperLivenessWindow = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan CaptureHealthWindow = TimeSpan.FromMinutes(5);

    private readonly SecureVault _vault;
    private readonly IOutboxQueue _outbox;
    private readonly BusinessClock _businessClock;

    /// <summary>
    /// The only clock this class reads. Defaults to <c>DateTimeOffset.UtcNow</c>, so production
    /// behaviour is unchanged; tests inject one so the 90-second helper-liveness window and the
    /// five-minute health window can be exercised without sleeping.
    /// </summary>
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<BusinessClockConfig>? _onBusinessClockChanged;
    private StaffTrackingConfig _config;
    private DateTimeOffset? _lastHelperSeenAt;
    private DateTimeOffset _lastSuccessfulCaptureAt;
    private bool _recovered;

    private static readonly IReadOnlyDictionary<string, string> VaultKindToEventType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["timetrack"] = AgentEventTypes.TimeTrack,
            ["screenshot"] = AgentEventTypes.ScreenshotMeta,
            ["browser_history"] = AgentEventTypes.BrowserHistory
        };

    public TrackingCoordinator(
        SecureVault vault,
        IOutboxQueue outbox,
        StaffTrackingConfig? initialConfig = null,
        BusinessClock? businessClock = null,
        Func<DateTimeOffset>? now = null,
        Action<BusinessClockConfig>? onBusinessClockChanged = null)
    {
        _vault = vault;
        _outbox = outbox;
        _config = initialConfig ?? new StaffTrackingConfig();
        _businessClock = businessClock ?? new BusinessClock();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _onBusinessClockChanged = onBusinessClockChanged;
        _lastSuccessfulCaptureAt = _now();
    }

    public StaffTrackingConfig Config => _config;

    public BusinessClock BusinessClock => _businessClock;

    /// <summary>True once any helper has ever connected. Display only — never a capture switch.</summary>
    public bool PreferSessionHelper => _lastHelperSeenAt is not null;

    /// <summary>
    /// A helper pinged inside the liveness window.
    ///
    /// This used to read <c>!_preferSessionHelper || (now - _lastHelperSeenAt &lt; 90s)</c>, which is
    /// TRUE before any helper has ever connected — so the heartbeat published
    /// <c>helperAlive: true</c> during a total capture blackout and the server could never reach its
    /// own "Tracking helper not running" branch. "Never seen" and "seen recently" must not be the
    /// same value.
    /// </summary>
    public bool IsSessionHelperAlive
        => _lastHelperSeenAt is { } seen && _now() - seen < HelperLivenessWindow;

    /// <summary>never_started / stale / alive — so the admin is told which, not just "not ok".</summary>
    public string HelperState => _lastHelperSeenAt is null
        ? HelperStates.NeverStarted
        : IsSessionHelperAlive ? HelperStates.Alive : HelperStates.Stale;

    public DateTimeOffset? LastHelperSeenAtUtc => _lastHelperSeenAt;

    public DateTimeOffset LastSuccessfulCaptureAtUtc => _lastSuccessfulCaptureAt;

    public bool IsTrackingHealthy
        => IsSessionHelperAlive
           && _now() - _lastSuccessfulCaptureAt < CaptureHealthWindow;

    /// <summary>
    /// Why nothing is being captured, or null when capture is healthy. Surfaced in the heartbeat and
    /// in agent-health.json so a blackout has a cause attached rather than being an unexplained hole.
    /// </summary>
    public string? CaptureUnavailableReason
    {
        get
        {
            if (!IsSessionHelperAlive)
            {
                return HelperState == HelperStates.NeverStarted
                    ? "no_session_helper_never_started"
                    : "no_session_helper_stale";
            }

            return IsTrackingHealthy ? null : "helper_connected_but_not_capturing";
        }
    }

    public void NoteSessionHelperAlive() => _lastHelperSeenAt = _now();

    public void ApplyConfig(StaffTrackingConfig config) => _config = config;

    /// <summary>
    /// Applies a new company business clock and persists it (§6.3) so a restarted service and the
    /// out-of-process uninstall guard derive approval codes on the same company-local time offline.
    /// </summary>
    public void ApplyBusinessClock(BusinessClockConfig config)
    {
        _businessClock.Apply(config);
        _onBusinessClockChanged?.Invoke(config);
    }

    /// <summary>
    /// Crash recovery. No capture happens here and none can: this runs in session 0. When no helper
    /// is connected the service emits nothing at all, and the period simply has no data.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!_recovered)
        {
            // Re-deliver any record committed to the vault but not confirmed enqueued before a crash,
            // so nothing buffered is ever dropped (§13.1).
            await RecoverUnenqueuedAsync(cancellationToken).ConfigureAwait(false);
            _recovered = true;
        }
    }

    private static readonly HashSet<string> HelperEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentEventTypes.TimeTrack,
        AgentEventTypes.ScreenshotMeta,
        AgentEventTypes.BrowserHistory
    };

    /// <summary>Persist a capture payload received from the interactive SessionHelper.</summary>
    public Task PersistFromHelperAsync(
        string eventType,
        string vaultKind,
        byte[] plain,
        DateTimeOffset occurredUtc,
        CancellationToken ct = default)
    {
        NoteSessionHelperAlive();

        if (!HelperEventTypes.Contains(eventType))
        {
            throw new ArgumentException($"Unknown helper event type: {eventType}", nameof(eventType));
        }

        if (string.Equals(eventType, AgentEventTypes.ScreenshotMeta, StringComparison.OrdinalIgnoreCase)
            && !_config.ScreenshotEnabled)
        {
            return Task.CompletedTask;
        }

        if (string.Equals(eventType, AgentEventTypes.TimeTrack, StringComparison.OrdinalIgnoreCase)
            && !_config.TimeTrackEnabled)
        {
            return Task.CompletedTask;
        }

        if (string.Equals(eventType, AgentEventTypes.BrowserHistory, StringComparison.OrdinalIgnoreCase)
            && !_config.BrowserHistoryEnabled)
        {
            return Task.CompletedTask;
        }

        _lastSuccessfulCaptureAt = _now();
        return PersistAndEnqueueAsync(eventType, vaultKind, plain, occurredUtc, ct);
    }

    private async Task PersistAndEnqueueAsync(
        string eventType,
        string vaultKind,
        byte[] plain,
        DateTimeOffset occurredUtc,
        CancellationToken ct)
    {
        var recordId = Guid.NewGuid();
        var append = _vault.Append(new VaultRecord
        {
            RecordId = recordId,
            Kind = vaultKind,
            OccurredAt = occurredUtc,
            PlainPayload = plain
        });

        var item = BuildOutboxItem(eventType, recordId, occurredUtc, plain);
        await _outbox.EnqueueAsync(item, ct).ConfigureAwait(false);

        // Enqueue confirmed. A crash before this line leaves the committed record for
        // RecoverUnenqueuedAsync to re-deliver on the next start.
        _vault.MarkEnqueued(append.Sequence);
    }

    /// <summary>
    /// Wraps a captured payload in the sync envelope. The vault RecordId becomes the outbox
    /// ClientEventId so a crash-recovery re-enqueue is deduplicated by the server's
    /// (UserId, ClientEventId) index instead of double-counting the record.
    /// </summary>
    private OutboxItem BuildOutboxItem(
        string eventType,
        Guid recordId,
        DateTimeOffset occurredUtc,
        byte[] plain)
    {
        var biz = _businessClock.At(occurredUtc);
        var envelope = new
        {
            configVersion = _config.ConfigVersion,
            occurredAtUtc = occurredUtc,
            businessLocal = biz.BusinessLocalIso,
            businessTimeZoneId = biz.TimeZoneId,
            businessClockVersion = biz.ClockVersion,
            businessSynchronized = biz.Synchronized,
            payloadBase64 = Convert.ToBase64String(plain)
        };

        var item = OutboxItem.Create(eventType, envelope, occurredUtc);
        item.ClientEventId = recordId;
        return item;
    }

    private async Task RecoverUnenqueuedAsync(CancellationToken ct)
    {
        foreach (var record in _vault.ReadUnenqueued())
        {
            if (VaultKindToEventType.TryGetValue(record.Kind, out var eventType))
            {
                var item = BuildOutboxItem(eventType, record.RecordId, record.OccurredAt, record.PlainPayload);
                await _outbox.EnqueueAsync(item, ct).ConfigureAwait(false);
            }

            _vault.MarkEnqueued(record.Sequence);
        }
    }
}
