using System.ComponentModel;
using Scriblucent.Localization;

namespace Scriblucent.Services;

/// <summary>Transient selection of logical items, independent of realized WPF containers.</summary>
public sealed class EditorSelectionController : INotifyPropertyChanged, ICollectionSelectionScope
{
    private Guid[] _items = [];
    private Guid[] _allItems = [];
    private readonly List<Guid> _selectionOrder = [];
    private bool _preserveHidden;
    private int _matchingSelectedCount;
    private readonly HashSet<Guid> _selected = [];
    private readonly HashSet<Guid> _available = [];
    private Guid? _anchor;
    public bool IsActive { get; private set; }
    public bool CanSelect { get; private set; } = true;
    public int Count => _selected.Count;
    public int TotalCount => _items.Length;
    public int MatchingSelectedCount => _matchingSelectedCount;
    public bool HasSelection => Count > 0;
    public bool CanSelectAll => _items.Length > MatchingSelectedCount;
    public string StatusText { get; private set; } = string.Empty;
    public string SelectionText => string.Format(LocalizationService.GetString("PremiumSelectedCount"), Count);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Changed;
    internal event Action? Starting;
    internal bool Contains(Guid id) => _selected.Contains(id);
    internal IReadOnlyList<Guid> SelectedIds => _preserveHidden ? _selectionOrder.ToArray() : _items.Where(_selected.Contains).ToArray();

    internal void SetItems(IEnumerable<Guid> items) => SetItems(items, null);
    internal void SetItems(IEnumerable<Guid> allItems, IEnumerable<Guid>? matchingItems)
    {
        var all = allItems.Distinct().ToArray();
        var available = all.ToHashSet();
        var next = matchingItems is null ? all : matchingItems.Where(available.Contains).Distinct().ToArray();
        bool preserve = matchingItems is not null;
        if (_allItems.SequenceEqual(all) && _items.SequenceEqual(next) && _preserveHidden == preserve) return;
        _allItems = all; _items = next; _preserveHidden = preserve;
        StatusText = string.Empty;
        _available.Clear(); _available.UnionWith(all);
        _selected.IntersectWith(preserve ? _available : next.ToHashSet());
        if (_anchor is Guid anchor && !next.Contains(anchor)) _anchor = null;
        Notify();
    }
    internal void SetAvailable(bool value)
    {
        if (CanSelect == value) return;
        CanSelect = value;
        if (!value) { End(); return; }
        Notify();
    }
    internal void Begin(Guid? initial = null)
    {
        if (!CanSelect) return;
        StatusText = string.Empty;
        if (!IsActive) { Starting?.Invoke(); IsActive = true; }
        if (initial is Guid id && _available.Contains(id)) { _selected.Add(id); _anchor = id; }
        Notify();
    }
    internal void Toggle(Guid id, bool range = false, bool additive = false)
    {
        if (!CanSelect || !_available.Contains(id)) return;
        StatusText = string.Empty;
        BeginIfNeeded();
        if (range && _anchor is Guid anchor && _items.Contains(anchor) && _items.Contains(id))
        {
            if (!additive) { if (_preserveHidden) _selected.ExceptWith(_items); else _selected.Clear(); }
            int start = Array.IndexOf(_items, anchor), end = Array.IndexOf(_items, id);
            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) _selected.Add(_items[i]);
        }
        else
        {
            if (!_selected.Remove(id)) _selected.Add(id);
            _anchor = id;
        }
        Notify();
    }
    private void BeginIfNeeded() { if (!IsActive) { Starting?.Invoke(); IsActive = true; } }
    internal void SelectAll()
    {
        if (!CanSelect) return;
        StatusText = string.Empty;
        BeginIfNeeded(); _selected.UnionWith(_items); Notify();
    }
    internal void ClearMatching() { StatusText = string.Empty; _selected.ExceptWith(_items); _anchor = null; Notify(); }
    internal void Clear() { StatusText = string.Empty; _selected.Clear(); _anchor = null; Notify(); }
    internal void SelectOnly(IEnumerable<Guid> ids)
    {
        _selected.Clear(); _selected.UnionWith(ids.Where(_available.Contains));
        _anchor = _items.FirstOrDefault(_selected.Contains);
        Notify();
    }
    internal void ReportStatus(string text) { StatusText = text; Notify(); }
    internal void End() { StatusText = string.Empty; IsActive = false; _selected.Clear(); _anchor = null; Notify(); }
    internal void RefreshText() => Notify();
    private void Notify()
    {
        if (_preserveHidden)
        {
            _matchingSelectedCount = _items.Count(_selected.Contains);
            _selectionOrder.RemoveAll(id => !_selected.Contains(id));
            var ordered = _selectionOrder.ToHashSet();
            foreach (var id in _items.Concat(_allItems)) if (_selected.Contains(id) && ordered.Add(id)) _selectionOrder.Add(id);
        }
        else { _matchingSelectedCount = _selected.Count; _selectionOrder.Clear(); }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }
    event Action? ICollectionSelectionScope.Changed { add => Changed += value; remove => Changed -= value; }
    bool ICollectionSelectionScope.Contains(Guid id) => Contains(id);
    void ICollectionSelectionScope.Begin(Guid? initial) => Begin(initial);
    void ICollectionSelectionScope.Toggle(Guid id, bool range, bool additive) => Toggle(id, range, additive);
    void ICollectionSelectionScope.SelectAll() => SelectAll();
    void ICollectionSelectionScope.Clear() => Clear();
    void ICollectionSelectionScope.ClearMatching() => ClearMatching();
    void ICollectionSelectionScope.End() => End();
}
