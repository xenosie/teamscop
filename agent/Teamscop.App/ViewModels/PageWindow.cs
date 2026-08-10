using System.Collections.ObjectModel;

namespace Teamscop.App.ViewModels;

/// <summary>
/// A fixed-size window over a longer list (§15.1). The bound collection only ever holds the rows
/// the user has asked to see, so a 50-member company materialises 25 containers, not 50, and the
/// per-row work each container triggers (avatars, thumbnails) is paid for the same way.
/// </summary>
public sealed class PageWindow<T>
{
    private readonly ObservableCollection<T> _visible;
    private readonly int _pageSize;
    private IReadOnlyList<T> _source = [];

    public PageWindow(ObservableCollection<T> visible, int pageSize)
    {
        _visible = visible;
        _pageSize = Math.Max(1, pageSize);
    }

    public int Total => _source.Count;
    public int Shown => _visible.Count;
    public bool HasMore => Shown < Total;

    /// <summary>False while everything fits — the footer stays out of the way of small companies.</summary>
    public bool IsPaged => Total > _pageSize;

    /// <summary>"Showing 25 of 47".</summary>
    public string Label => Total == 0 ? string.Empty : $"Showing {Shown} of {Total}";

    /// <summary>Replaces the source and rewinds to the first page.</summary>
    public void Reset(IReadOnlyList<T> source)
    {
        _source = source;
        _visible.Clear();
        Take(_pageSize);
    }

    public void More() => Take(_pageSize);

    public void All() => Take(Total - Shown);

    /// <summary>
    /// Grows the window until <paramref name="item"/> is on screen. Used when a row is selected
    /// from somewhere else (a Today drill-down) and may sit past the current page.
    /// </summary>
    public bool EnsureVisible(T item)
    {
        var index = -1;
        for (var i = 0; i < _source.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(_source[i], item))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        if (index >= Shown)
        {
            Take(index + 1 - Shown);
        }

        return true;
    }

    private void Take(int count)
    {
        var end = Math.Min(Total, Shown + Math.Max(0, count));
        for (var i = Shown; i < end; i++)
        {
            _visible.Add(_source[i]);
        }
    }
}
