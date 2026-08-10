using System.Collections.Concurrent;

namespace Teamscop.Api.Services;

/// <summary>Per-source (IP + deviceKey) sliding-window backoff for anonymous TOTP verify.</summary>
public interface ITotpSourceBackoff
{
    void EnsureAllowed(string sourceKey);
    void RecordFailure(string sourceKey);
    void RecordSuccess(string sourceKey);
}

public sealed class TotpSourceBackoff : ITotpSourceBackoff
{
    private const int MaxFailures = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void EnsureAllowed(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        if (_entries.TryGetValue(sourceKey, out var e) && e.LockedUntil > DateTimeOffset.UtcNow)
        {
            throw new UnauthorizedAccessException("TOTP verification temporarily blocked from this source.");
        }
    }

    public void RecordFailure(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            sourceKey,
            _ => new Entry { Failures = 1, WindowStart = now, LockedUntil = null },
            (_, e) =>
            {
                if (now - e.WindowStart > Window)
                {
                    e.Failures = 1;
                    e.WindowStart = now;
                    e.LockedUntil = null;
                }
                else
                {
                    e.Failures += 1;
                    if (e.Failures >= MaxFailures)
                    {
                        e.LockedUntil = now.Add(Window);
                    }
                }

                return e;
            });
    }

    public void RecordSuccess(string sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            _entries.TryRemove(sourceKey, out _);
        }
    }

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }
}
