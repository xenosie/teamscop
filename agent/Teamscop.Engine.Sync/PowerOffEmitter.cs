namespace Teamscop.Engine.Sync;

/// <summary>PHASE9 clean-stop emitter for <see cref="AgentEventTypes.PowerOff"/>.</summary>
public static class PowerOffEmitter
{
    public const string ReasonServiceStop = "service_stop";
    public const string ReasonShutdown = "shutdown";

    public static OutboxItem Create(
        string reason = ReasonServiceStop,
        string? businessLocal = null,
        string? businessTimeZoneId = null,
        long? businessClockVersion = null,
        bool? businessSynchronized = null)
    {
        if (businessLocal is null)
        {
            return OutboxItem.Create(AgentEventTypes.PowerOff, new
            {
                kind = AgentEventTypes.PowerOff,
                reason
            });
        }

        return OutboxItem.Create(AgentEventTypes.PowerOff, new
        {
            kind = AgentEventTypes.PowerOff,
            reason,
            businessLocal,
            businessTimeZoneId,
            businessClockVersion,
            businessSynchronized
        });
    }

    /// <summary>
    /// Enqueue power_off and best-effort flush. Safe to call after host cancellation
    /// (uses its own short timeout; does not require a live stopping token).
    /// </summary>
    public static async Task EmitAsync(
        IOutboxQueue outbox,
        SyncEngine syncEngine,
        string? accessToken,
        string reason = ReasonServiceStop,
        string? businessLocal = null,
        string? businessTimeZoneId = null,
        long? businessClockVersion = null,
        bool? businessSynchronized = null,
        TimeSpan? flushTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(syncEngine);

        await outbox.EnqueueAsync(
            Create(reason, businessLocal, businessTimeZoneId, businessClockVersion, businessSynchronized),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(flushTimeout ?? TimeSpan.FromSeconds(8));
        try
        {
            await syncEngine.ProbeAndRecordAsync(cts.Token).ConfigureAwait(false);
            await syncEngine.FlushAsync(accessToken, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort only.
        }
        catch
        {
            // Leave event in outbox for next start.
        }
    }
}
