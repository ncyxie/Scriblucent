using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Services;

namespace Scriblucent.Controls;

/// <summary>One picker policy for both libraries: retained baskets and range/select-all over matching IDs.</summary>
internal sealed class PickerSelectionBinding<T> : IDisposable where T : class
{
    internal EditorSelectionController Selection { get; } = new();
    private readonly Window _owner;
    private readonly Func<IEnumerable<T>> _all, _matching;
    private readonly Func<T, Guid> _id;
    private readonly Action<T, bool> _set;
    private readonly Action<T, int> _countDelta;
    private readonly Action<IReadOnlyList<T>> _commit;
    private Dictionary<Guid, T> _items = [];
    private Dictionary<Guid, T> _selected = [];
    private bool _syncing;
    internal PickerSelectionBinding(Window owner, Func<IEnumerable<T>> all, Func<IEnumerable<T>> matching, Func<T, Guid> id,
        Action<T, bool> set, Action<T, int> countDelta, Action<IReadOnlyList<T>> commit)
    {
        _owner = owner; _all = all; _matching = matching; _id = id; _set = set; _countDelta = countDelta; _commit = commit;
        Selection.Changed += Sync; owner.PreviewKeyDown += KeyDown;
        Scriblucent.Localization.LocalizationService.CultureChanged += CultureChanged;
    }
    internal bool? AllChecked => Selection.MatchingSelectedCount == 0 ? false : Selection.MatchingSelectedCount == Selection.TotalCount ? true : null;
    internal void RefreshItems()
    {
        _items = _all().ToDictionary(_id);
        Selection.SetItems(_items.Keys, _matching().Select(_id));
    }
    internal void Toggle(T item)
    {
        if (_syncing) return;
        Selection.Toggle(_id(item), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }
    internal void ToggleMatching() { if (AllChecked == true) Selection.ClearMatching(); else Selection.SelectAll(); }
    private void Sync()
    {
        var ordered = Selection.SelectedIds.Where(_items.ContainsKey).Select(id => _items[id]).ToArray();
        var next = ordered.ToDictionary(_id);
        _syncing = true;
        try
        {
            foreach (var pair in _selected) if (!next.ContainsKey(pair.Key)) { _set(pair.Value, false); _countDelta(pair.Value, -1); }
            foreach (var pair in next)
            {
                // A native checkbox changes the source flag before its selection request.
                // Shift may retain that item, so restore the logical state even when its ID
                // was already selected. Counts only change for additions/removals.
                _set(pair.Value, true);
                if (!_selected.ContainsKey(pair.Key)) _countDelta(pair.Value, 1);
            }
            _selected = next; _commit(ordered);
        }
        finally { _syncing = false; }
    }
    private void KeyDown(object sender, KeyEventArgs e)
    {
        for (var p = e.OriginalSource as DependencyObject; p is not null; p = EditorSelectionInput.Parent(p))
        {
            if (p is ComboBox { IsDropDownOpen: true }) return;
            if (p is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox) return;
        }
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Selection.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && Selection.Count > 0) { Selection.Clear(); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is FrameworkElement { DataContext: T item } && e.OriginalSource is not CheckBox)
        { Toggle(item); e.Handled = true; }
    }
    private void CultureChanged(object? sender, EventArgs e) => Selection.RefreshText();
    public void Dispose()
    {
        _owner.PreviewKeyDown -= KeyDown; Selection.Changed -= Sync;
        Scriblucent.Localization.LocalizationService.CultureChanged -= CultureChanged;
    }
}
