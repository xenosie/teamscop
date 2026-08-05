using System.Text.Json;

namespace Teamscop.Engine.Sync;

public interface IOutboxQueue
{
    Task EnqueueAsync(OutboxItem item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxItem>> PeekPendingAsync(int take, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(IEnumerable<Guid> clientEventIds, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid clientEventId, string error, CancellationToken cancellationToken = default);
    int PendingCount { get; }
}

/// <summary>
/// Durable file outbox under the agent data directory. Survives reboot/offline periods.
/// </summary>
public sealed class FileOutboxQueue : IOutboxQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _pendingDir;
    private readonly string _sentDir;
    private readonly object _gate = new();

    public FileOutboxQueue(string rootDirectory)
    {
        _pendingDir = Path.Combine(rootDirectory, "outbox", "pending");
        _sentDir = Path.Combine(rootDirectory, "outbox", "sent");
        Directory.CreateDirectory(_pendingDir);
        Directory.CreateDirectory(_sentDir);
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return Directory.Exists(_pendingDir)
                    ? Directory.GetFiles(_pendingDir, "*.json").Length
                    : 0;
            }
        }
    }

    public Task EnqueueAsync(OutboxItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_pendingDir, $"{item.ClientEventId:N}.json");
        var json = JsonSerializer.Serialize(item, JsonOptions);
        var tmp = path + ".tmp";
        lock (_gate)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxItem>> PeekPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<OutboxItem> items;
        lock (_gate)
        {
            items = Directory.EnumerateFiles(_pendingDir, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Take(Math.Max(1, take))
                .Select(ReadItemUnsafe)
                .Where(i => i is not null)
                .Cast<OutboxItem>()
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<OutboxItem>>(items);
    }

    public Task AcknowledgeAsync(IEnumerable<Guid> clientEventIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var id in clientEventIds)
            {
                var pending = Path.Combine(_pendingDir, $"{id:N}.json");
                if (!File.Exists(pending))
                {
                    continue;
                }

                var sent = Path.Combine(_sentDir, $"{id:N}.json");
                File.Move(pending, sent, overwrite: true);
            }

            // Keep sent folder bounded.
            var sentFiles = Directory.GetFiles(_sentDir, "*.json");
            if (sentFiles.Length > 500)
            {
                foreach (var old in sentFiles.OrderBy(f => f).Take(sentFiles.Length - 500))
                {
                    File.Delete(old);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid clientEventId, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Path.Combine(_pendingDir, $"{clientEventId:N}.json");
            var item = ReadItemUnsafe(path);
            if (item is null)
            {
                return Task.CompletedTask;
            }

            item.AttemptCount += 1;
            item.LastAttemptAt = DateTimeOffset.UtcNow;
            item.LastError = error.Length > 500 ? error[..500] : error;
            File.WriteAllText(path, JsonSerializer.Serialize(item, JsonOptions));
        }

        return Task.CompletedTask;
    }

    private static OutboxItem? ReadItemUnsafe(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
