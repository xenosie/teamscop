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
        RenameLegacyPendingFiles();
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
        var path = Path.Combine(_pendingDir, $"{DateTime.UtcNow.Ticks:D20}_{item.ClientEventId:N}.json");
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
            RenameLegacyPendingFilesUnsafe();
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

    private void RenameLegacyPendingFiles()
    {
        lock (_gate)
        {
            RenameLegacyPendingFilesUnsafe();
        }
    }

    /// <summary>
    /// Legacy files were `{guid:N}.json` and sort after tick-prefixed names, starving the queue.
    /// Rename using LastWriteTimeUtc ticks so they dequeue in approximate arrival order.
    /// </summary>
    private void RenameLegacyPendingFilesUnsafe()
    {
        foreach (var path in Directory.EnumerateFiles(_pendingDir, "*.json").ToList())
        {
            var name = Path.GetFileName(path);
            // New form: 20-digit ticks + '_' + 32-char guid + .json
            if (name.Length >= 54 && name[20] == '_' && char.IsDigit(name[0]))
            {
                continue;
            }

            // Legacy form is `{guid:N}.json` — a 32-char stem. (The prior `name.Length != 36` guard
            // was off by one: the real name is 37 chars, so no legacy file was ever migrated and it
            // kept starving behind tick-prefixed names.)
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length != 32 || !Guid.TryParseExact(stem, "N", out var id))
            {
                continue;
            }

            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            var dest = Path.Combine(_pendingDir, $"{ticks:D20}_{id:N}.json");
            try
            {
                if (!File.Exists(dest))
                {
                    File.Move(path, dest);
                }
            }
            catch
            {
                // ignore — will retry on next peek
            }
        }
    }

    public Task AcknowledgeAsync(IEnumerable<Guid> clientEventIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var id in clientEventIds)
            {
                var pending = FindPendingPath(id);
                if (pending is null)
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
            var path = FindPendingPath(clientEventId);
            if (path is null)
            {
                return Task.CompletedTask;
            }

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

    private string? FindPendingPath(Guid clientEventId)
    {
        var legacy = Path.Combine(_pendingDir, $"{clientEventId:N}.json");
        if (File.Exists(legacy))
        {
            return legacy;
        }

        var suffix = $"_{clientEventId:N}.json";
        return Directory.EnumerateFiles(_pendingDir, "*.json")
            .FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
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
