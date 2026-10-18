using System.Windows;
using System.Windows.Controls;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Views;

public partial class TrashPanel
{
    private TrashSelectionScope? _trashSelection;
    public static readonly DependencyProperty IsTrashSelectingProperty = DependencyProperty.Register(nameof(IsTrashSelecting), typeof(bool), typeof(TrashPanel), new PropertyMetadata(false));
    public bool IsTrashSelecting { get => (bool)GetValue(IsTrashSelectingProperty); private set => SetValue(IsTrashSelectingProperty, value); }
    private int SelectedTrashCount => IsLogicalAllSelected ? TrashedItemsListBox.Items.Count - _logicalSelectionExclusions.Count : TrashedItemsListBox.SelectedItems.Count;
    private void InitializeTrashSelection()
    {
        _trashSelection = new(this);
        CollectionSelectionFrame.SetSelection(this, _trashSelection);
        TrashSelectionDock.Initialize(_trashSelection,
            new("TrashRestore", AppIconKind.Undo, () => RestoreSelectedButton_Click(this, new RoutedEventArgs())),
            new("TrashDeletePermanently", AppIconKind.Delete, () => DeleteSelectedButton_Click(this, new RoutedEventArgs())), () => new ContextMenu());
        // Overlay panels are reused. Their scope remains owned by this panel rather than the window.
        Unloaded += (_, _) => EndTrashSelection();
    }
    private void EndTrashSelection()
    {
        _suppressSelectionChanged = true;
        try { IsLogicalAllSelected = false; ClearLogicalSelectionExclusions(); TrashedItemsListBox.UnselectAll(); IsTrashSelecting = false; }
        finally { _suppressSelectionChanged = false; }
        UpdateSelectionActions();
    }
    private void TrashSelectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrashItemViewModel item }) return;
        IsTrashSelecting = true;
        if (IsLogicalAllSelected) { _logicalSelectionExclusions.Remove(item); item.IsExcludedFromLogicalSelection = false; }
        else if (!TrashedItemsListBox.SelectedItems.Contains(item)) TrashedItemsListBox.SelectedItems.Add(item);
        UpdateSelectionActions(); TrashSelectionDock.FocusOnEntry();
    }
    private void ToggleTrashItem(TrashItemViewModel item)
    {
        IsTrashSelecting = true;
        if (IsLogicalAllSelected)
        {
            if (!_logicalSelectionExclusions.Remove(item)) _logicalSelectionExclusions.Add(item);
            item.IsExcludedFromLogicalSelection = _logicalSelectionExclusions.Contains(item);
        }
        else if (TrashedItemsListBox.SelectedItems.Contains(item)) TrashedItemsListBox.SelectedItems.Remove(item);
        else TrashedItemsListBox.SelectedItems.Add(item);
        UpdateSelectionActions();
    }
    private void TrashToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CollectionSelectionToggle toggle)
            toggle.ToggleRequested = () => { if (toggle.DataContext is TrashItemViewModel item) ToggleTrashItem(item); };
    }
    private sealed class TrashSelectionScope(TrashPanel owner) : ICollectionSelectionScope
    {
        public bool IsActive => owner.IsTrashSelecting;
        public bool CanSelect => owner.TrashedItemsListBox.Items.Count > 0;
        public int Count => owner.SelectedTrashCount;
        public int TotalCount => owner.TrashedItemsListBox.Items.Count;
        public string StatusText => string.Empty;
        public event Action? Changed;
        internal void Notify() => Changed?.Invoke();
        public bool Contains(Guid id) => owner.IsLogicalAllSelected
            ? owner.TrashedItemsListBox.ItemsSource is LazyTrashItemList list && list.IndexOfId(id) >= 0 && !owner._logicalSelectionExclusions.Any(item => item.Id == id)
            : owner.TrashedItemsListBox.SelectedItems.OfType<TrashItemViewModel>().Any(item => item.Id == id);
        public void Begin(Guid? initial = null)
        {
            owner.IsTrashSelecting = true;
            if (initial is Guid id && !Contains(id)) Toggle(id);
            owner.UpdateSelectionActions();
        }
        public void Toggle(Guid id, bool range = false, bool additive = false)
        {
            if (owner.TrashedItemsListBox.ItemsSource is LazyTrashItemList list)
            {
                int index = list.IndexOfId(id); if (index >= 0) owner.ToggleTrashItem(list[index]);
            }
        }
        public void SelectAll() => owner.SelectAllTrashItems();
        public void Clear() { owner.IsLogicalAllSelected = false; owner.ClearLogicalSelectionExclusions(); owner.TrashedItemsListBox.UnselectAll(); owner.UpdateSelectionActions(); }
        public void End() => owner.EndTrashSelection();
    }
}
