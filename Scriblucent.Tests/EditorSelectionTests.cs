using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorSelectionTests
{
    private static (EditorSelectionController Selection, Guid[] Ids) Create(int count = 8)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new EditorSelectionController(); selection.SetItems(ids);
        return (selection, ids);
    }
    [Fact]
    public void Selecting_is_logical_and_select_all_includes_offscreen_items()
    {
        var (selection, ids) = Create(500);
        int starts = 0; selection.Starting += () => starts++;
        selection.Begin(ids[300]); selection.SelectAll();
        Assert.Equal(500, selection.Count); Assert.False(selection.CanSelectAll);
        Assert.Equal(ids, selection.SelectedIds); Assert.Equal(1, starts);
        selection.Clear(); Assert.True(selection.IsActive); Assert.Equal(0, selection.Count);
        selection.End(); Assert.False(selection.IsActive);
    }
    [Fact]
    public void Shift_replaces_and_ctrl_shift_adds_range_in_display_order()
    {
        var (selection, ids) = Create();
        selection.Toggle(ids[3]); selection.Toggle(ids[6], range: true);
        Assert.Equal(ids[3..7], selection.SelectedIds);
        selection.Toggle(ids[1], range: true);
        Assert.Equal(ids[1..4], selection.SelectedIds);
        selection.Toggle(ids[7]); selection.Toggle(ids[6], range: true, additive: true);
        Assert.Equal(new[] { ids[1], ids[2], ids[3], ids[6], ids[7] }, selection.SelectedIds);
    }
    [Fact]
    public void Filtering_prunes_hidden_items_and_resets_missing_anchor()
    {
        var (selection, ids) = Create();
        selection.SelectAll(); selection.Toggle(ids[7]);
        selection.SetItems(ids[2..5]);
        Assert.Equal(ids[2..5], selection.SelectedIds);
        selection.Clear(); selection.Toggle(ids[7], range: true);
        Assert.Empty(selection.SelectedIds);
        selection.Toggle(ids[4], range: true);
        Assert.Equal(new[] { ids[4] }, selection.SelectedIds);
    }
    [Fact]
    public void Reordering_keeps_identity_and_uses_new_order_for_range()
    {
        var (selection, ids) = Create(4);
        selection.Toggle(ids[0]); selection.SetItems(ids.Reverse());
        selection.Toggle(ids[2], range: true);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, selection.SelectedIds);
    }
    [Fact]
    public void Mode_changes_clear_selection_and_block_unavailable_entry()
    {
        var (selection, ids) = Create();
        selection.SelectAll(); selection.SetAvailable(false);
        selection.Begin(ids[0]); selection.Toggle(ids[1]); selection.SelectAll();
        Assert.False(selection.IsActive); Assert.Empty(selection.SelectedIds);
        selection.SetAvailable(true); selection.Begin(ids[0]); Assert.Equal(1, selection.Count);
    }
    [Fact]
    public void Selection_is_per_editor_and_duplicate_selection_excludes_hidden_copies()
    {
        var (first, ids) = Create(); var (second, _) = Create();
        first.SelectOnly([ids[0], Guid.NewGuid()]);
        Assert.Equal(new[] { ids[0] }, first.SelectedIds); Assert.Equal(0, second.Count);
    }
    [Fact]
    public void Repeated_toggling_and_list_replacement_leave_no_stale_selection()
    {
        var (selection, ids) = Create(200);
        for (int i = 0; i < 40; i++) { selection.SelectAll(); selection.Toggle(ids[i]); selection.SetItems(ids); }
        Assert.Equal(199, selection.Count);
        selection.SetItems([]); Assert.Equal(0, selection.Count); Assert.True(selection.IsActive);
        selection.End(); selection.SetItems(ids); Assert.Empty(selection.SelectedIds);
    }
    [Fact]
    public void Removing_anchor_prevents_range_from_reaching_outside_current_list()
    {
        var (selection, ids) = Create();
        selection.Begin(ids[0]); selection.SetItems(ids[3..]); selection.Toggle(ids[5], true);
        Assert.Equal(new[] { ids[5] }, selection.SelectedIds);
    }
}
