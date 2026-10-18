namespace Scriblucent.Services;

/// <summary>Identity-based insertion shared by filtered lists, dashboards and editors.</summary>
internal static class ItemOrdering
{
    public static List<T> Move<T>(IEnumerable<T> source, IReadOnlySet<T> moving, T anchor, bool after) where T : notnull
    {
        var original = source.ToList();
        if (moving.Count == 0 || moving.Contains(anchor) || !original.Contains(anchor)) return original;
        var block = original.Where(moving.Contains).ToList();
        if (block.Count != moving.Count) return original;
        var result = original.Where(item => !moving.Contains(item)).ToList();
        result.InsertRange(result.IndexOf(anchor) + (after ? 1 : 0), block);
        return result;
    }
}

/// <summary>Local arrangement metadata, independent of document modification dates.</summary>
internal sealed class ManualItemOrder
{
    private List<Guid> _ids = [];
    private readonly Dictionary<Guid, int> _positions = [];
    public IReadOnlyList<Guid> Ids => _ids;
    public IReadOnlyDictionary<Guid, int> Positions => _positions;
    public bool HasOrder => _ids.Count != 0;
    public int Position(Guid id) => _positions.GetValueOrDefault(id, -1);
    public void Set(IEnumerable<Guid>? ids)
    {
        _ids = ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        _positions.Clear();
        for (int i = 0; i < _ids.Count; i++) _positions[_ids[i]] = i;
    }
    public void Reconcile(IEnumerable<Guid> liveAndTrashed)
    {
        if (!HasOrder) return;
        var current = liveAndTrashed.Distinct().ToList();
        var retained = current.ToHashSet();
        var missing = current.Where(id => !_positions.ContainsKey(id)).ToList();
        if (missing.Count == 0 && _ids.All(retained.Contains)) return;
        missing.AddRange(_ids.Where(retained.Contains));
        Set(missing);
    }
    public bool Move(IReadOnlySet<Guid> moving, Guid anchor, bool after)
    {
        var result = ItemOrdering.Move(_ids, moving, anchor, after);
        if (result.SequenceEqual(_ids)) return false;
        Set(result); return true;
    }
}
