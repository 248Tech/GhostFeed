namespace GhostFeed;

/// <summary>Ordered play list with a cursor. Dedupes by media id; trims old history to bound memory.</summary>
sealed class VideoQueue
{
    const int MaxItems = 600, TrimBy = 200;

    readonly List<VideoItem> _items = new();
    readonly HashSet<string> _ids = new();
    int _index = -1;

    public int Count => _items.Count;
    public int Index => _index;
    public int Remaining => _items.Count - 1 - _index;
    public VideoItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public VideoItem? PeekNext => _index + 1 < _items.Count ? _items[_index + 1] : null;

    /// <returns>Number of new items added.</returns>
    public int Add(IEnumerable<VideoItem> items)
    {
        int added = 0;
        foreach (var it in items)
        {
            if (!_ids.Add(it.MediaId)) continue;
            _items.Add(it);
            added++;
        }
        if (_items.Count > MaxItems && _index > TrimBy)
        {
            for (int i = 0; i < TrimBy; i++) _ids.Remove(_items[i].MediaId);
            _items.RemoveRange(0, TrimBy);
            _index -= TrimBy;
        }
        return added;
    }

    public VideoItem? Next()
    {
        if (_index + 1 >= _items.Count) return null;
        return _items[++_index];
    }

    public VideoItem? Prev()
    {
        if (_index <= 0) return null;
        return _items[--_index];
    }

    /// <summary>Remove everything after the current item (used when the feed source changes).</summary>
    public void ResetUpcoming()
    {
        for (int i = _index + 1; i < _items.Count; i++) _ids.Remove(_items[i].MediaId);
        if (_index + 1 < _items.Count) _items.RemoveRange(_index + 1, _items.Count - _index - 1);
    }

    public void Clear()
    {
        _items.Clear();
        _ids.Clear();
        _index = -1;
    }
}
