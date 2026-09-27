using System.Collections;
using Scriblucent.Localization;

namespace Scriblucent.ViewModels;

internal readonly record struct TrashItemEntry(
    TrashItemKind Kind,
    Guid Id,
    string Title,
    DateTime? DeletedAtUtc,
    object Source);

/// <summary>
/// A read-only IList so WPF can virtualize a very large Trash list without constructing a
/// TrashItemViewModel for every row. Filtered views share the same row objects and identity.
/// </summary>
internal sealed class LazyTrashItemList : IList, IReadOnlyList<TrashItemViewModel>
{
    private sealed class SharedState
    {
        public required TrashItemEntry[] Entries;
        public required TrashItemViewModel?[] Items;
        public required int RetentionDays;
        public Dictionary<TrashItemViewModel, int> CreatedIndices { get; } = new();
    }

    private readonly SharedState _shared;
    private readonly int[]? _indices;
    private readonly int[]? _reverseIndices;

    public LazyTrashItemList(TrashItemEntry[] entries, int retentionDays)
    {
        _shared = new SharedState
        {
            Entries = entries,
            Items = new TrashItemViewModel?[entries.Length],
            RetentionDays = retentionDays
        };
    }

    private LazyTrashItemList(SharedState shared, int[] indices, int[] reverseIndices)
    {
        _shared = shared;
        _indices = indices;
        _reverseIndices = reverseIndices;
    }

    public int Count => _indices?.Length ?? _shared.Entries.Length;
    internal int CreatedItemCount => _shared.CreatedIndices.Count;

    internal IEnumerable<object> EnumerateSources()
    {
        for (var i = 0; i < Count; i++)
            yield return _shared.Entries[_indices is null ? i : _indices[i]].Source;
    }

    public TrashItemViewModel this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var sourceIndex = _indices is null ? index : _indices[index];
            if (_shared.Items[sourceIndex] is { } item)
                return item;

            var entry = _shared.Entries[sourceIndex];
            item = new TrashItemViewModel(entry.Kind, entry.Id, entry.Title,
                entry.DeletedAtUtc, entry.Source, _shared.RetentionDays);
            _shared.Items[sourceIndex] = item;
            _shared.CreatedIndices[item] = sourceIndex;
            return item;
        }
    }

    public LazyTrashItemList Filter(TrashItemKind? kind, string? query)
    {
        if (kind is null && string.IsNullOrWhiteSpace(query))
            return this;

        var search = query?.Trim() ?? string.Empty;
        var kindNames = search.Length == 0
            ? Array.Empty<string>()
            : [
                LocalizationService.GetString("NotesDashboard"),
                LocalizationService.GetString("FlashcardsDashboard"),
                LocalizationService.GetString("MindMapsDashboard"),
                LocalizationService.GetString("QuizzesDashboard")
            ];
        var matches = new List<int>();
        for (var i = 0; i < _shared.Entries.Length; i++)
        {
            var entry = _shared.Entries[i];
            if (kind.HasValue && entry.Kind != kind.Value)
                continue;
            if (search.Length > 0)
            {
                var title = string.IsNullOrWhiteSpace(entry.Title)
                    ? LocalizationService.GetString("Untitled") : entry.Title;
                if (!title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    && !kindNames[(int)entry.Kind].Contains(search, StringComparison.CurrentCultureIgnoreCase))
                    continue;
            }
            matches.Add(i);
        }

        var indices = matches.ToArray();
        var reverseIndices = new int[_shared.Entries.Length];
        Array.Fill(reverseIndices, -1);
        for (var i = 0; i < indices.Length; i++)
            reverseIndices[indices[i]] = i;
        return new LazyTrashItemList(_shared, indices, reverseIndices);
    }

    public LazyTrashItemList ExcludeSources(IReadOnlySet<object> excluded)
    {
        if (excluded.Count == 0)
            return this;

        var matches = new List<int>(Count - Math.Min(Count, excluded.Count));
        for (var i = 0; i < Count; i++)
        {
            var sourceIndex = _indices is null ? i : _indices[i];
            if (!excluded.Contains(_shared.Entries[sourceIndex].Source))
                matches.Add(sourceIndex);
        }

        var indices = matches.ToArray();
        var reverseIndices = new int[_shared.Entries.Length];
        Array.Fill(reverseIndices, -1);
        for (var i = 0; i < indices.Length; i++)
            reverseIndices[indices[i]] = i;
        return new LazyTrashItemList(_shared, indices, reverseIndices);
    }

    public int IndexOf(object? value)
    {
        if (value is not TrashItemViewModel item
            || !_shared.CreatedIndices.TryGetValue(item, out var sourceIndex))
            return -1;
        return _reverseIndices is null ? sourceIndex : _reverseIndices[sourceIndex];
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<TrashItemViewModel> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
