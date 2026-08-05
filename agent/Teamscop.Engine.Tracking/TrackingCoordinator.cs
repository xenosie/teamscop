using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Tracking;

/// <summary>
/// Low-impact tracking orchestrator:
/// 1) write encrypted+compressed vault record (tamper-evident chain)
/// 2) enqueue sync outbox item stamped with company business time
/// </summary>
public sealed class TrackingCoordinator
{
    private readonly SecureVault _vault;
    private readonly IOutboxQueue _outbox;
    private readonly TimeTrackEngine _timeTrack;
    private readonly ScreenshotEngine _screenshots = new();
    private readonly ChromeHistoryWatcher _chrome;
    private readonly BusinessClock _businessClock;
    private StaffTrackingConfig _config;
    private DateTimeOffset _lastScreenshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimeTrackFlush = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastIntegrityFullScan = DateTimeOffset.MinValue;

    public TrackingCoordinator(
        SecureVault vault,
        IOutboxQueue outbox,
        ChromeHistoryWatcher chrome,
        StaffTrackingConfig? initialConfig = null,
        TimeTrackEngine? timeTrack = null,
        BusinessClock? businessClock = null)
    {
        _vault = vault;
        _outbox = outbox;
        _chrome = chrome;
        _config = initialConfig ?? new StaffTrackingConfig();
        _timeTrack = timeTrack ?? new TimeTrackEngine();
        _businessClock = businessClock ?? new BusinessClock();
    }

    public StaffTrackingConfig Config => _config;
    public BusinessClock BusinessClock => _businessClock;

    public void ApplyConfig(StaffTrackingConfig config) => _config = config;

    public void ApplyBusinessClock(BusinessClockConfig config) => _businessClock.Apply(config);

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var full = DateTimeOffset.UtcNow - _lastIntegrityFullScan > TimeSpan.FromHours(1);
        var report = _vault.Verify(fullScan: full);
        if (full)
        {
            _lastIntegrityFullScan = DateTimeOffset.UtcNow;
        }

        if (!report.Ok)
        {
            await EnqueueVaultAlertAsync(report, cancellationToken).ConfigureAwait(false);
        }

        if (_config.TimeTrackEnabled)
        {
            await TickTimeTrackAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_config.ScreenshotEnabled &&
            DateTimeOffset.UtcNow - _lastScreenshotAt >= TimeSpan.FromSeconds(Math.Max(30, _config.ScreenshotPeriodSeconds)))
        {
            await TickScreenshotAsync(cancellationToken).ConfigureAwait(false);
            _lastScreenshotAt = DateTimeOffset.UtcNow;
        }

        if (_config.BrowserHistoryEnabled)
        {
            await TickChromeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TickTimeTrackAsync(CancellationToken ct)
    {
        var sample = _timeTrack.Poll();
        if (DateTimeOffset.UtcNow - _lastTimeTrackFlush < TimeSpan.FromSeconds(60))
        {
            return;
        }

        var endedUtc = DateTimeOffset.UtcNow;
        var startedUtc = _lastTimeTrackFlush;
        var segment = _timeTrack.CloseSegment(endedUtc);
        _lastTimeTrackFlush = endedUtc;

        var startedBiz = _businessClock.At(startedUtc);
        var endedBiz = _businessClock.At(endedUtc);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sample.State,
            sample.IdleSeconds,
            startedAtUtc = startedUtc,
            endedAtUtc = endedUtc,
            startedAtBusiness = startedBiz.BusinessLocalIso,
            endedAtBusiness = endedBiz.BusinessLocalIso,
            durationSeconds = segment.DurationSeconds,
            businessTimeZoneId = endedBiz.TimeZoneId,
            businessClockVersion = endedBiz.ClockVersion,
            businessSynchronized = endedBiz.Synchronized,
            algorithm = "last_input_hysteresis_v1"
        });
        await PersistAndEnqueueAsync(AgentEventTypes.TimeTrack, "timetrack", payload, endedUtc, ct).ConfigureAwait(false);
    }

    private async Task TickScreenshotAsync(CancellationToken ct)
    {
        var captures = _screenshots.CaptureAllDisplays(_config);
        if (captures.Count == 0)
        {
            return;
        }

        var payload = _screenshots.SerializeCaptures(captures, _config.ConfigVersion);
        await PersistAndEnqueueAsync(AgentEventTypes.ScreenshotMeta, "screenshot", payload, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    private async Task TickChromeAsync(CancellationToken ct)
    {
        var visits = _chrome.PollNewVisits();
        if (visits.Count == 0)
        {
            return;
        }

        var payload = _chrome.SerializeVisits(visits);
        await PersistAndEnqueueAsync(AgentEventTypes.BrowserHistory, "browser_history", payload, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    private async Task PersistAndEnqueueAsync(
        string eventType,
        string vaultKind,
        byte[] plain,
        DateTimeOffset occurredUtc,
        CancellationToken ct)
    {
        var biz = _businessClock.At(occurredUtc);
        var append = _vault.Append(new VaultRecord
        {
            Kind = vaultKind,
            OccurredAt = occurredUtc,
            PlainPayload = plain
        });

        var envelope = new
        {
            vaultSequence = append.Sequence,
            chainHash = append.ChainHashHex,
            configVersion = _config.ConfigVersion,
            occurredAtUtc = occurredUtc,
            businessLocal = biz.BusinessLocalIso,
            businessTimeZoneId = biz.TimeZoneId,
            businessClockVersion = biz.ClockVersion,
            businessSynchronized = biz.Synchronized,
            payloadBase64 = Convert.ToBase64String(plain)
        };

        await _outbox.EnqueueAsync(OutboxItem.Create(eventType, envelope, occurredUtc), ct).ConfigureAwait(false);
    }

    private Task EnqueueVaultAlertAsync(VaultIntegrityReport report, CancellationToken ct)
    {
        var biz = _businessClock.Now();
        return _outbox.EnqueueAsync(OutboxItem.Create(AgentEventTypes.VaultAlert, new
        {
            alert = "vault_integrity",
            report.Ok,
            report.Error,
            report.ChainBreak,
            report.TamperedRecord,
            report.TipMissing,
            report.ExpectedNextSequence,
            report.HighestSequenceFound,
            report.RecordCount,
            businessLocal = biz.BusinessLocalIso,
            businessTimeZoneId = biz.TimeZoneId,
            businessClockVersion = biz.ClockVersion
        }), ct);
    }
}
