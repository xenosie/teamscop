namespace Teamscop.Engine.Sync;

public sealed class SyncEngineOptions
{
    public int BatchSize { get; set; } = 50;
    public int ProbeEveryCycles { get; set; } = 1;
}

/// <summary>
/// Connectivity check → enqueue status → flush durable outbox when online.
/// </summary>
public sealed class SyncEngine(
    IConnectivityProbe probe,
    IOutboxQueue outbox,
    ISyncApiClient syncApi,
    SyncEngineOptions? options = null)
{
    private readonly SyncEngineOptions _options = options ?? new SyncEngineOptions();
    private ConnectivityStatus? _lastStatus;

    public ConnectivityStatus? LastStatus => _lastStatus;
    public int PendingCount => outbox.PendingCount;

    public async Task<ConnectivityStatus> ProbeAndRecordAsync(CancellationToken cancellationToken = default)
    {
        var status = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        _lastStatus = status;
        await outbox.EnqueueAsync(
            OutboxItem.Create(AgentEventTypes.Connectivity, new
            {
                status.IsOnline,
                status.ApiReachable,
                status.LatencyMs,
                status.Detail
            }),
            cancellationToken).ConfigureAwait(false);
        return status;
    }

    public Task EnqueueAsync(OutboxItem item, CancellationToken cancellationToken = default)
        => outbox.EnqueueAsync(item, cancellationToken);

    public async Task<int> FlushAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return 0;
        }

        if (_lastStatus is { ApiReachable: false })
        {
            // Quick re-check before giving up this cycle.
            _lastStatus = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!_lastStatus.ApiReachable)
            {
                return 0;
            }
        }

        var pending = await outbox.PeekPendingAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        try
        {
            var result = await syncApi.PushBatchAsync(accessToken, pending, cancellationToken).ConfigureAwait(false);
            var done = result.AcceptedIds.Concat(result.DuplicateIds).Distinct().ToList();
            await outbox.AcknowledgeAsync(done, cancellationToken).ConfigureAwait(false);

            // Poison items: record reason then remove from pending (never retry permanently invalid types/payloads).
            var rejectedIds = new List<Guid>();
            foreach (var bad in result.Rejected)
            {
                await outbox.MarkFailedAsync(bad.ClientEventId, bad.Reason, cancellationToken).ConfigureAwait(false);
                rejectedIds.Add(bad.ClientEventId);
            }

            if (rejectedIds.Count > 0)
            {
                await outbox.AcknowledgeAsync(rejectedIds, cancellationToken).ConfigureAwait(false);
            }

            return done.Count;
        }
        catch (Exception ex)
        {
            // Transport / server failure: bump attempts but keep items for retry.
            foreach (var item in pending)
            {
                await outbox.MarkFailedAsync(item.ClientEventId, ex.Message, cancellationToken).ConfigureAwait(false);
            }

            _lastStatus = new ConnectivityStatus
            {
                IsOnline = false,
                ApiReachable = false,
                Detail = ex.GetType().Name
            };
            throw;
        }
    }
}
