using Avalonia.Media.Imaging;

namespace Teamscop.App.Services;

/// <summary>Small in-memory LRU for decoded screenshot bitmaps (thumbs or full frames).</summary>
public sealed class BitmapLruCache
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, Bitmap Bitmap)> _map = new();
    private readonly object _gate = new();

    public BitmapLruCache(int capacity)
    {
        _capacity = Math.Max(4, capacity);
    }

    public bool TryGet(string key, out Bitmap? bitmap)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                bitmap = entry.Bitmap;
                return true;
            }
        }

        bitmap = null;
        return false;
    }

    public void Set(string key, Bitmap bitmap)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing.Node);
                if (!ReferenceEquals(existing.Bitmap, bitmap))
                {
                    existing.Bitmap.Dispose();
                }

                var node = _order.AddFirst(key);
                _map[key] = (node, bitmap);
                return;
            }

            while (_map.Count >= _capacity && _order.Last is { } last)
            {
                // Do not Dispose here — tiles may still display the bitmap.
                // Clear() disposes when the gallery resets.
                _map.Remove(last.Value);
                _order.RemoveLast();
            }

            var n = _order.AddFirst(key);
            _map[key] = (n, bitmap);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _map.Values)
            {
                entry.Bitmap.Dispose();
            }

            _map.Clear();
            _order.Clear();
        }
    }
}
