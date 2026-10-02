using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Services;
using DocumentList = System.Windows.Documents.List;

namespace Scriblucent.Tests;

public class EditorListTests
{
    [Fact]
    public void Conversion_replaces_markers_and_only_changes_selected_items() => Sta(() =>
    {
        using var h = new Harness("first", "second", "third");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        var paragraphs = h.Paragraphs();
        h.Select(paragraphs[1]); h.Lists.ApplyStyle(TextMarkerStyle.None);
        Assert.Equal([TextMarkerStyle.Decimal, TextMarkerStyle.None, TextMarkerStyle.Decimal], h.RootLists().Select(l => l.MarkerStyle));
        Assert.Equal(3, h.RootLists()[2].StartIndex);
        Assert.True(EditorLists.Checkbox(paragraphs[1], out _, out _));
        Assert.Equal("first", EditorLists.InlineText(paragraphs[0]));
        h.Select(paragraphs[1]); h.Lists.ApplyStyle(TextMarkerStyle.Square);
        Assert.Equal("second", EditorLists.InlineText(paragraphs[1]));
    });

    [Theory]
    [InlineData(TextMarkerStyle.Disc)]
    [InlineData(TextMarkerStyle.Decimal)]
    [InlineData(TextMarkerStyle.LowerLatin)]
    [InlineData(TextMarkerStyle.UpperRoman)]
    [InlineData(TextMarkerStyle.None)]
    public void Nesting_and_backspace_share_rules(TextMarkerStyle style) => Sta(() =>
    {
        using var h = new Harness("parent", "child", "last");
        h.SelectAll(); h.Lists.ApplyStyle(style);
        var paragraphs = h.Paragraphs();
        h.Caret(paragraphs[1]);
        Assert.True(h.Lists.HandleKey(Key.Tab, ModifierKeys.None, false));
        Assert.IsType<ListItem>(EditorLists.Ancestor<DocumentList>(paragraphs[1])!.Parent);
        h.Caret(paragraphs[1]);
        Assert.True(h.Lists.HandleKey(Key.Back, ModifierKeys.None, false));
        Assert.Equal(3, h.RootLists().Single().ListItems.Count);
        h.Caret(paragraphs[1]);
        Assert.True(h.Lists.HandleKey(Key.Back, ModifierKeys.None, false));
        Assert.Null(EditorLists.Ancestor<ListItem>(paragraphs[1]));
        Assert.Equal("child", EditorLists.InlineText(paragraphs[1]));
    });

    [Fact]
    public void Checkbox_completion_preserves_color_and_manual_decorations() => Sta(() =>
    {
        using var h = new Harness("important");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs().Single();
        Assert.True(EditorLists.Checkbox(p, out var start, out _));
        var range = new TextRange(start, p.ContentEnd);
        range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Red);
        range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
        h.Lists.ToggleCheckbox(p); h.Lists.ToggleCheckbox(p);
        Assert.Equal(Brushes.Red.ToString(), ((Brush)range.GetPropertyValue(TextElement.ForegroundProperty)).ToString());
        Assert.Contains((TextDecorationCollection)range.GetPropertyValue(Inline.TextDecorationsProperty), d => d.Location == TextDecorationLocation.Strikethrough);
        h.Select(p); h.Lists.Remove();
        Assert.Equal("important", EditorLists.InlineText(p));
        Assert.Contains((TextDecorationCollection)new TextRange(p.ContentStart, p.ContentEnd).GetPropertyValue(Inline.TextDecorationsProperty), d => d.Location == TextDecorationLocation.Strikethrough);
    });

    [Fact]
    public void Tab_in_text_and_in_a_table_does_not_change_nesting() => Sta(() =>
    {
        using var h = new Harness("parent", "child");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs()[1];
        h.Editor.CaretPosition = EditorLists.AdvanceCharacters(p.ContentStart, 4);
        Assert.False(h.Lists.HandleKey(Key.Tab, ModifierKeys.None, false));
        h.Caret(p);
        Assert.False(h.Lists.HandleKey(Key.Tab, ModifierKeys.None, true));
        Assert.Equal(2, h.RootLists().Single().ListItems.Count);
    });

    [Fact]
    public void Moving_a_parent_keeps_children_and_checkbox_state() => Sta(() =>
    {
        using var h = new Harness("parent", "child", "other");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs();
        h.Caret(p[1]); h.Lists.Indent(false); h.Lists.ToggleCheckbox(p[1]);
        h.Caret(p[0]); Assert.True(h.Lists.Move(true));
        Assert.Equal(["☐ other", "☐ parent", "☑ child"], h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Fact]
    public void Automatic_start_can_be_undone_to_literal_input() => Sta(() =>
    {
        using var h = new Harness("[]");
        h.Editor.CaretPosition = h.Paragraphs()[0].ContentEnd;
        Assert.True(h.Lists.AutoStart(" "));
        Assert.Equal(TextMarkerStyle.None, h.RootLists().Single().MarkerStyle);
        h.Editor.Undo();
        Assert.Empty(h.RootLists());
        Assert.Equal("[] ", EditorLists.InlineText(h.Paragraphs()[0]));
    });

    [Fact]
    public void Enter_continues_checked_items_as_unchecked_and_exits_empty_items() => Sta(() =>
    {
        using var h = new Harness("task");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs()[0];
        h.Lists.ToggleCheckbox(p);
        h.Editor.CaretPosition = p.ContentEnd;
        h.Lists.Enter(); h.Flush();
        Assert.Equal(2, h.RootLists().Single().ListItems.Count);
        Assert.Equal("☐ ", EditorLists.InlineText(h.Paragraphs()[1]));
        h.Lists.Enter(); h.Flush();
        Assert.Null(EditorLists.Ancestor<ListItem>(h.Editor.CaretPosition.Paragraph));
        Assert.Equal(string.Empty, EditorLists.InlineText(h.Editor.CaretPosition.Paragraph!));
    });

    [Fact]
    public void Linked_numbering_survives_save_reopen_and_updates_after_enter() => Sta(() =>
    {
        using var h = new Harness("one", "two", "break", "three");
        var p = h.Paragraphs();
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(p[3]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[3]); h.Lists.SetNumbering(null);
        Assert.Equal(3, h.RootLists()[1].StartIndex);
        using var stream = new MemoryStream();
        new TextRange(h.Editor.Document.ContentStart, h.Editor.Document.ContentEnd).Save(stream, DataFormats.XamlPackage);
        h.Lists.WriteMetadata(stream);
        stream.Position = 0;
        using var reopened = new Harness();
        reopened.Lists.LoadContent(stream, DataFormats.XamlPackage);
        var lastFirstListItem = reopened.RootLists()[0].ListItems.LastListItem;
        reopened.Editor.CaretPosition = lastFirstListItem.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        reopened.Lists.Enter(); reopened.Flush();
        Assert.Equal(4, reopened.RootLists()[1].StartIndex);
        reopened.Editor.Undo(); reopened.Flush();
        Assert.Equal(3, reopened.RootLists()[1].StartIndex);
        reopened.Editor.Redo(); reopened.Flush();
        Assert.Equal(4, reopened.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Removing_a_middle_number_does_not_restart_the_suffix() => Sta(() =>
    {
        using var h = new Harness("one", "two", "three");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(h.Paragraphs()[1]); h.Lists.Remove();
        Assert.Equal(3, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Markers_support_start_values_and_letters_after_z()
    {
        Assert.Equal("aa. ", EditorLists.Marker(TextMarkerStyle.LowerLatin, 27));
        Assert.Equal("AB. ", EditorLists.Marker(TextMarkerStyle.UpperLatin, 28));
        Assert.Equal("xiv. ", EditorLists.Marker(TextMarkerStyle.LowerRoman, 14));
        Assert.Equal(string.Empty, EditorLists.Marker(TextMarkerStyle.None, 1));
    }

    [Fact]
    public void Legacy_checkboxes_get_hanging_alignment_and_nested_structure() => Sta(() =>
    {
        using var h = new Harness("☐ parent", "☑ child");
        var p = h.Paragraphs(); p[1].Margin = new Thickness(48, 0, 0, 0);
        h.Lists.NormalizeLegacy();
        Assert.Single(h.RootLists());
        Assert.IsType<ListItem>(EditorLists.Ancestor<DocumentList>(p[1])!.Parent);
        Assert.True(p[1].TextIndent < 0);
        Assert.Equal(0, p[1].Margin.Left);
    });

    [Fact]
    public void Numbering_link_itself_is_undoable_when_displayed_value_is_unchanged() => Sta(() =>
    {
        using var h = new Harness("one", "break", "two");
        var p = h.Paragraphs();
        h.Select(p[0]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(p[2]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[2]); h.Lists.SetNumbering(2); h.Lists.SetNumbering(null);
        h.Editor.Undo(); h.Flush();
        h.Editor.CaretPosition = h.Paragraphs()[0].ContentEnd; h.Lists.Enter(); h.Flush();
        Assert.Equal(2, h.RootLists()[1].StartIndex);
        h.Editor.Undo(); h.Flush();
        h.Caret(h.Paragraphs()[2]); h.Lists.SetNumbering(null);
        h.Editor.CaretPosition = h.Paragraphs()[0].ContentEnd; h.Lists.Enter(); h.Flush();
        Assert.Equal(3, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Restarting_the_first_item_preserves_links_from_later_lists() => Sta(() =>
    {
        using var h = new Harness("one", "two", "break", "three");
        var p = h.Paragraphs();
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[0]); h.Lists.SetNumbering(7);
        h.Select(p[3]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[3]); h.Lists.SetNumbering(null);
        Assert.Equal(9, h.RootLists()[1].StartIndex);
        h.Caret(p[0]); h.Lists.SetNumbering(1);
        Assert.Equal(3, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Deleting_a_numbering_source_detaches_without_resetting_its_follower() => Sta(() =>
    {
        using var h = new Harness("one", "two", "break", "three");
        var p = h.Paragraphs();
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(p[3]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[3]); h.Lists.SetNumbering(null);
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.Remove();
        Assert.Equal(3, h.RootLists().Single().StartIndex);
        h.Editor.Undo(); h.Flush();
        h.Editor.CaretPosition = h.Paragraphs()[1].ContentEnd; h.Lists.Enter(); h.Flush();
        Assert.Equal(4, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Plain_paste_into_checklist_creates_unchecked_items_in_one_undo() => Sta(() =>
    {
        using var h = new Harness("task");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs()[0]; h.Editor.CaretPosition = p.ContentEnd;
        h.Lists.PasteText(" A\r\nB\r\n"); h.Flush();
        Assert.Equal(["☐ task A", "☐ B"], h.Paragraphs().Select(EditorLists.InlineText));
        h.Editor.Undo(); h.Flush();
        Assert.Equal("☐ task", EditorLists.InlineText(h.Paragraphs().Single()));
    });

    [Fact]
    public void Rich_copy_paste_preserves_checkbox_state_and_hierarchy() => Sta(() =>
    {
        using var source = new Harness("parent", "child");
        source.SelectAll(); source.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = source.Paragraphs(); source.Caret(p[1]); source.Lists.Indent(false); source.Lists.ToggleCheckbox(p[1]);
        using var stream = new MemoryStream();
        new TextRange(source.Editor.Document.ContentStart, source.Editor.Document.ContentEnd).Save(stream, DataFormats.Rtf);
        using var target = new Harness();
        stream.Position = 0;
        target.Lists.Mutate(() => new TextRange(target.Editor.Document.ContentStart, target.Editor.Document.ContentEnd).Load(stream, DataFormats.Rtf));
        Assert.Equal(["☐ parent", "☑ child"], target.Paragraphs().Select(EditorLists.InlineText));
        Assert.IsType<ListItem>(EditorLists.Ancestor<DocumentList>(target.Paragraphs()[1])!.Parent);
    });

    [Fact]
    public void Partial_selection_across_items_indents_without_replacing_text() => Sta(() =>
    {
        using var h = new Harness("parent", "first", "second", "third");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Decimal, explicitStyle: false);
        var p = h.Paragraphs();
        h.Editor.Selection.Select(EditorLists.AdvanceCharacters(p[1].ContentStart, 2), EditorLists.AdvanceCharacters(p[2].ContentStart, 3));
        Assert.True(h.Lists.HandleKey(Key.Tab, ModifierKeys.None, false));
        Assert.Equal(["parent", "first", "second", "third"], h.Paragraphs().Select(EditorLists.InlineText));
        var nested = EditorLists.Ancestor<DocumentList>(p[1])!;
        Assert.Equal(TextMarkerStyle.LowerLatin, nested.MarkerStyle);
        Assert.Equal(2, nested.ListItems.Count);
        Assert.False(h.Editor.Selection.IsEmpty);
    });

    [Fact]
    public void Nested_empty_enter_promotes_one_level_before_exiting() => Sta(() =>
    {
        using var h = new Harness("parent", string.Empty);
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs()[1]; h.Caret(p); h.Lists.Indent(false);
        h.Caret(p); h.Lists.Enter(); h.Flush();
        Assert.IsType<FlowDocument>(EditorLists.Ancestor<DocumentList>(p)!.Parent);
        Assert.True(EditorLists.Checkbox(p, out _, out _));
        h.Lists.Enter(); h.Flush();
        Assert.Null(EditorLists.Ancestor<ListItem>(p));
    });

    [Fact]
    public void Enter_splits_rich_item_and_shift_enter_keeps_one_item() => Sta(() =>
    {
        using var h = new Harness("alpha beta");
        var p = h.Paragraphs()[0];
        p.Inlines.Add(new InlineUIContainer(new TextBlock { Text = "image" }));
        p.Inlines.Add(new Run("tail") { FontWeight = FontWeights.Bold });
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        h.Editor.CaretPosition = EditorLists.AdvanceCharacters(p.ContentStart, 8);
        h.Lists.Enter(); h.Flush();
        Assert.Equal(2, h.RootLists().Single().ListItems.Count);
        var second = h.Paragraphs()[1];
        Assert.Contains(second.Inlines.Cast<Inline>(), inline => inline is InlineUIContainer);
        Assert.Contains(second.Inlines.Cast<Inline>(), inline => inline is Run { FontWeight: var weight } && weight == FontWeights.Bold);
        h.Editor.CaretPosition = second.ContentEnd;
        Assert.False(h.Lists.HandleKey(Key.Return, ModifierKeys.Shift, false));
        EditingCommands.EnterLineBreak.Execute(null, h.Editor); h.Flush();
        Assert.Equal(2, h.RootLists().Single().ListItems.Count);
        Assert.Contains('\n', EditorLists.InlineText(second));
    });

    [Fact]
    public void Long_selection_converts_every_item_including_offscreen_items() => Sta(() =>
    {
        using var h = new Harness(Enumerable.Range(0, 200).Select(i => "item " + i).ToArray());
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        Assert.Equal(200, h.RootLists().Single().ListItems.Count);
        Assert.All(h.Paragraphs(), p => Assert.True(EditorLists.Checkbox(p, out _, out _)));
    });

    [Fact]
    public void Checkbox_alignment_uses_the_marker_font_size() => Sta(() =>
    {
        using var h = new Harness("wrapped task with several words");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None);
        var p = h.Paragraphs().Single();
        double original = -p.TextIndent;
        h.Select(p); h.Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 36.0);
        h.Lists.RefreshLayout();
        Assert.True(-p.TextIndent > original * 2);
        Assert.Equal(0, p.Margin.Left);
    });

    [Theory]
    [InlineData(TextMarkerStyle.Disc)]
    [InlineData(TextMarkerStyle.Decimal)]
    [InlineData(TextMarkerStyle.None)]
    public void The_first_item_can_be_indented_without_a_previous_parent(TextMarkerStyle style) => Sta(() =>
    {
        using var h = new Harness("first", "second");
        h.SelectAll(); h.Lists.ApplyStyle(style, explicitStyle: false);
        var first = h.Paragraphs()[0]; h.Caret(first);
        Assert.True(h.Lists.HandleKey(Key.Tab, ModifierKeys.None, false));
        var list = EditorLists.Ancestor<DocumentList>(first)!;
        Assert.Equal(1, h.Lists.NestingDepth(list));
        Assert.Equal(48, list.Margin.Left);
        h.Caret(first); h.Lists.HandleKey(Key.Tab, ModifierKeys.Shift, false);
        Assert.Single(h.RootLists());
        Assert.Equal(2, h.RootLists()[0].ListItems.Count);
        Assert.Equal(style, h.RootLists()[0].MarkerStyle);
    });

    [Fact]
    public void Restoring_a_history_package_can_be_undone_with_its_numbering_links() => Sta(() =>
    {
        using var h = new Harness("one", "two", "break", "three");
        var p = h.Paragraphs();
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(p[3]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Caret(p[3]); h.Lists.SetNumbering(null);
        h.Lists.ReplaceContent("a different note");
        h.Editor.Undo(); h.Flush();
        Assert.Equal(2, h.RootLists().Length);
        h.Editor.CaretPosition = h.Paragraphs()[1].ContentEnd; h.Lists.Enter(); h.Flush();
        Assert.Equal(4, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Checkbox_click_uses_the_glyph_bounds_and_ignores_item_text() => Sta(() =>
    {
        using var h = new Harness("task text");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None); h.Flush();
        var p = h.Paragraphs()[0];
        var glyph = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
        Assert.True(h.Lists.ToggleAt(new Point(glyph.Left + 3, glyph.Top + glyph.Height / 2)));
        var text = EditorLists.AdvanceCharacters(p.ContentStart, 5).GetCharacterRect(LogicalDirection.Forward);
        Assert.False(h.Lists.ToggleAt(new Point(text.Left + 1, text.Top + text.Height / 2)));
        Assert.True(EditorLists.Checkbox(p, out _, out bool done)); Assert.True(done);
    });

    [Theory]
    [InlineData("• first\n• second")]
    [InlineData("- first\n- second")]
    [InlineData("8. first\n9. second")]
    [InlineData("(1) first\n(2) second")]
    [InlineData("a) first\nb) second")]
    [InlineData("xviii. first\nxix. second")]
    [InlineData("☑ first\n☐ second")]
    public void Plain_list_paste_adopts_destination_markers_and_is_one_undo(string text) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Decimal); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(text); h.Flush();
        Assert.Equal(["first", "second"], h.Paragraphs().Select(EditorLists.InlineText));
        Assert.Single(h.RootLists()); Assert.Equal(TextMarkerStyle.Decimal, h.RootLists()[0].MarkerStyle);
        Assert.Equal(2, h.RootLists()[0].ListItems.Count);
        Assert.Equal("second", EditorLists.InlineText(h.Editor.CaretPosition.Paragraph!));
        h.Editor.Undo(); h.Flush();
        Assert.Equal("", EditorLists.InlineText(h.Paragraphs().Single()));
        h.Editor.Redo(); h.Flush();
        Assert.Equal(["first", "second"], h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Theory]
    [InlineData("☑ first\n☐ second")]
    [InlineData("[x] first\n[ ] second")]
    public void Plain_checklist_paste_preserves_completion_without_duplicate_boxes(string text) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(text); h.Flush();
        Assert.Equal(["☑ first", "☐ second"], h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Theory]
    [InlineData("- parent\n    - child\n        - grandchild\n- sibling")]
    [InlineData("- parent\n\t- child\n\t\t- grandchild\n- sibling")]
    public void Plain_list_paste_preserves_relative_nesting(string text) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Disc, explicitStyle: false); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(text); h.Flush();
        Assert.Equal(["parent", "child", "grandchild", "sibling"], h.Paragraphs().Select(EditorLists.InlineText));
        Assert.Equal([0, 1, 2, 0], h.Paragraphs().Select(p => h.Lists.NestingDepth(EditorLists.Ancestor<DocumentList>(p)!)));
        Assert.Equal(2, h.RootLists().Single().ListItems.Count);
    });

    [Theory]
    [InlineData("plain\n\ntext", "plain", "", "text")]
    [InlineData("- first\n\n- last\n", "first", "", "last")]
    public void Blank_pasted_lines_stay_inside_the_destination_list(string text, string first, string blank, string last) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(text); h.Flush();
        Assert.Equal(new[] { first, blank, last }.Select(s => "☐ " + s), h.Paragraphs().Select(EditorLists.InlineText));
        Assert.Equal(3, h.RootLists().Single().ListItems.Count);
    });

    [Theory]
    [InlineData("1.5 liters\n2026.10.02\nhttps://example.com\nwell-known")]
    [InlineData("- list-like line\nordinary prose")]
    public void Paste_keeps_prose_and_inconsistent_markers_literal(string text) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Disc); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(text); h.Flush();
        Assert.Equal(text.Split('\n'), h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Fact]
    public void Plain_list_paste_preserves_surrounding_text_and_existing_checkbox_state() => Sta(() =>
    {
        using var h = new Harness("before after");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.None); h.Lists.ToggleCheckbox(h.Paragraphs()[0]);
        h.Editor.CaretPosition = EditorLists.AdvanceCharacters(h.Paragraphs()[0].ContentStart, 9);
        h.Lists.PasteText("☐ first\n☑ second"); h.Flush();
        Assert.Equal(["☑ before first", "☑ secondafter"], h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Fact]
    public void Replacing_selected_items_with_a_plain_list_updates_linked_numbering() => Sta(() =>
    {
        using var h = new Harness("one", "two", "break", "three");
        var p = h.Paragraphs();
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd); h.Lists.ApplyStyle(TextMarkerStyle.Decimal);
        h.Select(p[3]); h.Lists.ApplyStyle(TextMarkerStyle.Decimal); h.Caret(p[3]); h.Lists.SetNumbering(null);
        h.Editor.Selection.Select(p[0].ContentStart, p[1].ContentEnd);
        h.Lists.PasteText("1. a\n2. b\n3. c"); h.Flush();
        Assert.Equal(["a", "b", "c", "break", "three"], h.Paragraphs().Select(EditorLists.InlineText));
        Assert.Equal(4, h.RootLists()[1].StartIndex);
        h.Editor.Undo(); h.Flush(); Assert.Equal(3, h.RootLists()[1].StartIndex);
    });

    [Fact]
    public void Plain_list_paste_outside_a_list_remains_literal() => Sta(() =>
    {
        using var h = new Harness(""); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText("- first\n- second"); h.Flush();
        Assert.Empty(h.RootLists());
        Assert.Equal(["- first", "- second"], h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Theory]
    [InlineData(TextMarkerStyle.Decimal)]
    [InlineData(TextMarkerStyle.None)]
    public void Pasting_a_nested_list_into_a_nested_destination_keeps_its_base_level(TextMarkerStyle style) => Sta(() =>
    {
        using var h = new Harness("owner", "");
        h.SelectAll(); h.Lists.ApplyStyle(style, explicitStyle: false);
        h.Caret(h.Paragraphs()[1]); h.Lists.Indent(false); h.Caret(h.Paragraphs()[1]);
        h.Lists.PasteText("- child\n  - grandchild\n- sibling"); h.Flush();
        Assert.Equal([0, 1, 2, 1], h.Paragraphs().Select(p => h.Lists.NestingDepth(EditorLists.Ancestor<DocumentList>(p)!)));
        Assert.Equal(style == TextMarkerStyle.None
            ? new[] { "☐ owner", "☐ child", "☐ grandchild", "☐ sibling" }
            : new[] { "owner", "child", "grandchild", "sibling" }, h.Paragraphs().Select(EditorLists.InlineText));
    });

    [Theory]
    [InlineData("• task", "task")]
    [InlineData("[x] task", "task")]
    [InlineData("1. prose", "1. prose")]
    [InlineData("(1) prose", "(1) prose")]
    public void Single_item_paste_is_conservative_in_a_bullet_list(string input, string expected) => Sta(() =>
    {
        using var h = new Harness("");
        h.SelectAll(); h.Lists.ApplyStyle(TextMarkerStyle.Disc); h.Caret(h.Paragraphs()[0]);
        h.Lists.PasteText(input); h.Flush();
        Assert.Equal(expected, EditorLists.InlineText(h.Paragraphs().Single()));
    });

    private sealed class Harness : IDisposable
    {
        internal RichTextBox Editor { get; } = new() { AcceptsTab = true, AcceptsReturn = true };
        internal EditorLists Lists { get; }
        private readonly Window _window;
        internal Harness(params string[] lines)
        {
            Editor.Document.Blocks.Clear();
            foreach (var line in lines) Editor.Document.Blocks.Add(new Paragraph(new Run(line)));
            if (lines.Length == 0) Editor.Document.Blocks.Add(new Paragraph());
            Lists = new EditorLists(Editor, () => 48);
            Editor.TextChanged += (_, e) => Lists.TextChanged(e);
            _window = new Window { Content = Editor, Width = 500, Height = 250, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            _window.Show(); Editor.Focus(); Flush();
            Editor.IsUndoEnabled = false; Editor.IsUndoEnabled = true; Lists.ResetHistory();
        }
        internal void Flush() => Editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        internal Paragraph[] Paragraphs() => EditorLists.Paragraphs(Editor.Document.Blocks).ToArray();
        internal DocumentList[] RootLists() => Editor.Document.Blocks.OfType<DocumentList>().ToArray();
        internal void SelectAll() => Editor.SelectAll();
        internal void Select(Paragraph p) => Editor.Selection.Select(p.ContentStart, p.ContentEnd);
        internal void Caret(Paragraph p)
        {
            var start = EditorLists.Checkbox(p, out var text, out _) ? text : p.ContentStart;
            Editor.CaretPosition = start.GetInsertionPosition(LogicalDirection.Forward);
        }
        public void Dispose() => _window.Close();
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "List scenario timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
