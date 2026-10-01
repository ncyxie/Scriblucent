using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorTableTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(10000)]
    public void Long_cell_text_wraps_and_table_stays_in_viewport(double pageWidth) => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        var first = table.RowGroups[0].Rows[0].Cells[0];
        first.Blocks.Clear();
        first.Blocks.Add(new Paragraph(new Run(string.Concat(Enumerable.Repeat("Long note text that should wrap. ", 16)))));
        var editor = Editor(table, pageWidth);
        var layout = new EditorTableLayout();
        layout.Apply(editor.Document, 500);
        Measure(editor);
        var start = first.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
        var end = first.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!.GetCharacterRect(LogicalDirection.Backward);
        Assert.True(end.Top > start.Top + 50, "The long cell must have multiple wrapped lines.");
        Assert.True(EditorTableLayout.Bounds(table, editor).Width <= 500.01);
        Assert.Equal(pageWidth, editor.Document.PageWidth);
    });

    [Fact]
    public void Narrowing_and_reopening_keep_preferred_widths_and_native_formatting() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        EditorTableLayout.SetWidths(table, [250, 150, 200]);
        EditorTables.ToggleHeader(table);
        table.RowGroups[0].Rows[1].Cells[1].Background = Brushes.LightBlue;
        table.RowGroups[0].Rows[1].Cells[1].Blocks.Clear();
        table.RowGroups[0].Rows[1].Cells[1].Blocks.Add(new Paragraph(new Bold(new Run("Formatted"))));
        var editor = Editor(table);
        var layout = new EditorTableLayout();
        layout.Apply(editor.Document, 300);
        Assert.True(table.Columns.Sum(column => column.Width.Value) + table.CellSpacing * 4 <= 300.01);
        var displayWidths = table.Columns.Select(column => column.Width.Value).ToArray();
        using var stream = new MemoryStream();
        using (layout.UsePreferredWidths(editor.Document))
            new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Save(stream, DataFormats.XamlPackage);
        Assert.Equal(displayWidths, table.Columns.Select(column => column.Width.Value).ToArray());
        stream.Position = 0;
        var reopened = new FlowDocument();
        new TextRange(reopened.ContentStart, reopened.ContentEnd).Load(stream, DataFormats.XamlPackage);
        var restored = EditorTables.Enumerate(reopened.Blocks).Single();
        Assert.Equal(new double[] { 250, 150, 200 }, restored.Columns.Select(column => column.Width.Value));
        Assert.True(EditorTables.HasHeader(restored));
        Assert.NotNull(restored.RowGroups[0].Rows[1].Cells[1].Background);
        Assert.Equal("Formatted", EditorTables.CellText(restored.RowGroups[0].Rows[1].Cells[1]));
        layout.Apply(editor.Document, 900);
        Assert.Equal(new double[] { 250, 150, 200 }, table.Columns.Select(column => column.Width.Value));
    });

    [Fact]
    public void Legacy_expanding_and_implicit_columns_are_normalized() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        foreach (var column in table.Columns) column.Width = new GridLength(1, GridUnitType.Star);
        var editor = Editor(table, 10000);
        var layout = new EditorTableLayout();
        layout.Apply(editor.Document, 550);
        Assert.All(table.Columns, column => Assert.Equal(160, column.Width.Value));
        var implicitColumns = EditorTables.Create(2, 2);
        implicitColumns.Columns.Clear();
        editor.Document.Blocks.Add(implicitColumns);
        layout.Apply(editor.Document, 550);
        Assert.Equal(2, implicitColumns.Columns.Count);
        Assert.True(EditorTables.IsRectangular(implicitColumns));
    });

    [Fact]
    public void Nested_table_fits_its_parent_cell() => RunSta(() =>
    {
        var outer = EditorTables.Create(1, 2);
        var nested = EditorTables.Create(2, 2);
        outer.RowGroups[0].Rows[0].Cells[0].Blocks.Add(nested);
        var editor = Editor(outer);
        new EditorTableLayout().Apply(editor.Document, 400);
        Assert.True(nested.Columns.Sum(column => column.Width.Value) + nested.CellSpacing * 3
            <= outer.Columns[0].Width.Value - 14 + 0.01,
            $"Nested width {nested.Columns.Sum(column => column.Width.Value) + nested.CellSpacing * 3}; parent {outer.Columns[0].Width.Value}; tables {EditorTables.Enumerate(editor.Document.Blocks).Count()}; nested parent {nested.Parent?.GetType().Name}");
    });

    [Fact]
    public void Resize_is_one_undo_step_and_preserves_surrounding_typing() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 2);
        var editor = Editor(table);
        Measure(editor);
        var after = (Paragraph)table.NextBlock!;
        editor.CaretPosition = after.ContentStart;
        editor.Selection.Text = "Earlier typing";
        EditorTables.Edit(editor, table, () => EditorTableLayout.SetWidths(table, [220, 100]));
        Assert.True(editor.CanUndo);
        editor.Undo();
        var undone = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal(new double[] { 160, 160 }, undone.Columns.Select(column => column.Width.Value));
        Assert.Contains("Earlier typing", new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text);
        editor.Redo();
        var redone = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal(new double[] { 220, 100 }, redone.Columns.Select(column => column.Width.Value));
    });

    [Fact]
    public void Table_edit_keeps_live_inline_controls_and_restores_images_on_undo() => RunSta(() =>
    {
        var table = EditorTables.Create(1, 2);
        var image = new Image { Width = 30, Height = 25 };
        var paragraph = (Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock!;
        paragraph.Inlines.Add(new InlineUIContainer(image));
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        EditorTables.Edit(editor, table, () => EditorTableLayout.SetWidths(table, [200, 120]));
        Assert.Same(image, paragraph.Inlines.OfType<InlineUIContainer>().Single().Child);
        editor.Undo();
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        var restoredParagraph = (Paragraph)restored.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock!;
        Assert.IsType<Image>(restoredParagraph.Inlines.OfType<InlineUIContainer>().Single().Child);
        editor.Redo();
        restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        restoredParagraph = (Paragraph)restored.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock!;
        Assert.IsType<Image>(restoredParagraph.Inlines.OfType<InlineUIContainer>().Single().Child);
    });

    [Fact]
    public void Failed_table_edit_rolls_back_content_and_widths() => RunSta(() =>
    {
        var table = EditorTables.Create(1, 2);
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        Assert.Throws<InvalidOperationException>(() => EditorTables.Edit(editor, table, () =>
        {
            table.Columns[0].Width = new GridLength(300);
            table.RowGroups[0].Rows[0].Cells[0].Blocks.Clear();
            throw new InvalidOperationException("Failed edit");
        }));
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal(160, restored.Columns[0].Width.Value);
        Assert.Single(restored.RowGroups[0].Rows[0].Cells[0].Blocks);
    });

    [Fact]
    public void Moving_a_column_keeps_its_width_formatting_and_content_and_can_be_undone() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        EditorTables.PasteGrid(table, 0, 0, [["a", "b", "c"], ["d", "e", "f"]]);
        EditorTableLayout.SetWidths(table, [90, 180, 120]);
        table.RowGroups[0].Rows[1].Cells[1].Background = Brushes.LightGreen;
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        EditorTables.Edit(editor, table, () => EditorTables.MoveColumn(table, 1, -1));
        Assert.Equal(new double[] { 180, 90, 120 }, table.Columns.Select(column => column.Width.Value));
        Assert.Equal("e", EditorTables.CellText(table.RowGroups[0].Rows[1].Cells[0]));
        Assert.NotNull(table.RowGroups[0].Rows[1].Cells[0].Background);
        editor.Undo();
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal(new double[] { 90, 180, 120 }, restored.Columns.Select(column => column.Width.Value));
        Assert.Equal("e", EditorTables.CellText(restored.RowGroups[0].Rows[1].Cells[1]));
    });

    [Fact]
    public void Grid_paste_grows_the_table_preserves_neighbors_and_undo_restores_original() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 2);
        EditorTables.PasteGrid(table, 0, 0, [["keep", "old"], ["neighbor", "old"]]);
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        EditorTables.Edit(editor, table, () => EditorTables.PasteGrid(table, 1, 1,
            [["first", ""], ["line 1\nline 2", "last"]]));
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(3, EditorTables.Rows(table).Length);
        Assert.Equal("keep", EditorTables.CellText(table.RowGroups[0].Rows[0].Cells[0]));
        Assert.Equal("neighbor", EditorTables.CellText(table.RowGroups[0].Rows[1].Cells[0]));
        Assert.Contains("line 2", EditorTables.CellText(table.RowGroups[0].Rows[2].Cells[1]));
        editor.Undo();
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal(2, restored.Columns.Count);
        Assert.Equal(2, EditorTables.Rows(restored).Length);
        Assert.Equal("old", EditorTables.CellText(restored.RowGroups[0].Rows[1].Cells[1]));
    });

    [Theory]
    [InlineData(0, "a\r\nd\r\ng")]
    [InlineData(1, "b\r\ne\r\nh")]
    [InlineData(2, "c\r\nf\r\ni")]
    public void Column_selection_copies_only_selected_cells_and_geometry_stays_in_that_column(int column, string expected) => RunSta(() =>
    {
        var table = EditorTables.Create(3, 3);
        EditorTables.PasteGrid(table, 0, 0, [["a", "b", "c"], ["d", "e", "f"], ["g", "h", "i"]]);
        var editor = Editor(table);
        Measure(editor);
        editor.Selection.Select(table.RowGroups[0].Rows[0].Cells[column].ContentStart,
            table.RowGroups[0].Rows[2].Cells[column].ContentEnd);
        Assert.True(EditorTables.TryGetRange(editor.Selection.Start, editor.Selection.End, out var selected, out var range));
        Assert.Same(table, selected);
        Assert.Equal(expected, EditorTables.CopyGrid(table, range));
        var left = EditorTableLayout.CellBounds(table, 0, 0, editor);
        var middle = EditorTableLayout.CellBounds(table, 0, 1, editor);
        var right = EditorTableLayout.CellBounds(table, 0, 2, editor);
        Assert.False(middle.IsEmpty);
        Assert.True(middle.Left >= left.Right);
        Assert.True(middle.Right <= right.Left);
    });

    [Fact]
    public void Resize_borders_stay_fixed_when_cell_text_is_centered_or_right_aligned() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 2);
        EditorTables.PasteGrid(table, 0, 0, [["short", "text"], ["last", "row"]]);
        var editor = Editor(table);
        Measure(editor);
        var original = EditorTableLayout.Bounds(table, editor);
        var cell = table.RowGroups[0].Rows[0].Cells[0];
        foreach (var alignment in new[] { TextAlignment.Center, TextAlignment.Right })
        {
            cell.TextAlignment = alignment;
            Measure(editor);
            var bounds = EditorTableLayout.Bounds(table, editor);
            Assert.True(Math.Abs(original.Left - bounds.Left) < 0.01,
                $"Border moved {original.Left} -> {bounds.Left} with {alignment} alignment.");
        }
    });

    [Fact]
    public void Partial_cell_text_selection_keeps_normal_rich_text_copy() => RunSta(() =>
    {
        var table = EditorTables.Create(1, 2);
        EditorTables.PasteGrid(table, 0, 0, [["abcdef", "other"]]);
        var editor = Editor(table);
        var run = ((Paragraph)table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock!).Inlines.OfType<Run>().Single();
        editor.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!, run.ContentStart.GetPositionAtOffset(3)!);
        Assert.False(EditorTables.TryGetRange(editor.Selection.Start, editor.Selection.End, out _, out _));
        Assert.Equal("bc", editor.Selection.Text);
    });

    [Theory]
    [InlineData("a\tb\r\nc\td\r\n", 2, "d")]
    [InlineData("a\t\n\tb", 2, "b")]
    [InlineData("\"first\nsecond\"\t\"a\"\"b\"", 1, "a\"b")]
    public void Spreadsheet_clipboard_grids_preserve_empty_and_quoted_fields(string text, int rows, string last)
    {
        Assert.True(EditorTables.TryParseGrid(text, out var grid));
        Assert.Equal(rows, grid.Length);
        Assert.Equal(last, grid[^1][^1]);
    }

    [Theory]
    [InlineData("ordinary text")]
    [InlineData("first\nsecond")]
    [InlineData("a\tb\nc")]
    [InlineData("\"unterminated\tb")]
    public void Ordinary_text_and_invalid_grids_are_not_intercepted(string text)
        => Assert.False(EditorTables.TryParseGrid(text, out _));

    [Fact]
    public void Copy_and_paste_round_trip_embedded_tabs_quotes_and_newlines() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 2);
        var values = new string[][] { ["one\ttwo", "a\"b"], ["first\nsecond\n", ""] };
        EditorTables.PasteGrid(table, 0, 0, values);
        var text = EditorTables.CopyGrid(table, new EditorTables.CellRange(0, 1, 0, 1));
        Assert.True(EditorTables.TryParseGrid(text, out var grid));
        Assert.Equal(values[0], grid[0]);
        Assert.Equal(values[1], grid[1]);
    });

    [Fact]
    public void Copying_a_single_column_preserves_grid_shape_on_paste() => RunSta(() =>
    {
        var data = new DataObject();
        data.SetText("first\r\nsecond\r\nthird");
        Assert.False(EditorTables.TryReadGrid(data, out _));
        data.SetData(EditorTables.ClipboardGridFormat, "first\r\nsecond\r\nthird");
        Assert.True(EditorTables.TryReadGrid(data, out var grid));
        Assert.Equal(3, grid.Length);
        Assert.All(grid, row => Assert.Single(row));
        data.SetData(EditorTables.ClipboardGridFormat, "");
        Assert.True(EditorTables.TryReadGrid(data, out grid));
        Assert.Equal("", Assert.Single(Assert.Single(grid)));
    });

    [Fact]
    public void Whole_table_selection_includes_empty_cells_without_surrounding_paragraphs() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        var editor = Editor(table);
        Measure(editor);
        editor.Selection.Select(table.ContentStart, table.ContentEnd);
        Assert.True(EditorTables.TryGetRange(editor.Selection.Start, editor.Selection.End, out _, out var range));
        Assert.Equal(new EditorTables.CellRange(0, 1, 0, 2), range);
        Assert.Equal("\t\t\r\n\t\t", EditorTables.CopyGrid(table, range));
    });

    [Fact]
    public void Divider_and_edge_resizing_preserve_proportions_and_minimum_widths()
    {
        var divided = EditorTableLayout.Resize([100, 200, 150], 1, 500, 600);
        Assert.Equal(450, divided.Sum());
        Assert.Equal(40, divided[1]);
        Assert.Equal(150, divided[2]);
        var expanded = EditorTableLayout.Resize([100, 200], 2, 800, 450);
        Assert.Equal(450, expanded.Sum());
        Assert.Equal(2, expanded[1] / expanded[0]);
        var shrunk = EditorTableLayout.Resize([80, 240], 2, -1000, 500);
        Assert.Equal(40, shrunk[0]);
        Assert.Equal(120, shrunk[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Selected_column_or_row_alignment_preserves_rich_text_neighbors_and_undo(bool column) => RunSta(() =>
    {
        var table = EditorTables.Create(3, 3);
        EditorTables.PasteGrid(table, 0, 0, [["a", "b", "c"], ["d", "e", "f"], ["g", "h", "i"]]);
        var styled = table.RowGroups[0].Rows[1].Cells[1];
        styled.Blocks.Clear();
        styled.Blocks.Add(new Paragraph(new Bold(new Run("formatted"))));
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        editor.Selection.Select(table.RowGroups[0].Rows[column ? 0 : 1].Cells[column ? 1 : 0].ContentStart,
            table.RowGroups[0].Rows[column ? 2 : 1].Cells[column ? 1 : 2].ContentEnd);
        var selected = EditorTables.SelectedCells(editor.Selection.Start, editor.Selection.End, table, styled);
        Assert.Equal(3, selected.Length);
        EditorTables.Edit(editor, table, () => EditorTables.Align(selected, TextAlignment.Center));
        Assert.All(selected, cell => Assert.Equal(TextAlignment.Center, cell.TextAlignment));
        Assert.Equal(TextAlignment.Left, table.RowGroups[0].Rows[0].Cells[0].TextAlignment);
        Assert.IsType<Bold>(((Paragraph)styled.Blocks.FirstBlock!).Inlines.FirstInline);
        Assert.Equal("formatted", EditorTables.CellText(styled));
        editor.Undo();
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.All(EditorTables.Rows(restored).SelectMany(row => row.Cells.Cast<TableCell>()),
            cell => Assert.Equal(TextAlignment.Left, cell.TextAlignment));
        Assert.Equal("formatted", EditorTables.CellText(restored.RowGroups[0].Rows[1].Cells[1]));
        Assert.True(editor.CanRedo);
    });

    [Fact]
    public void Clearing_a_cell_range_preserves_structure_widths_and_styles_and_is_undoable() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 3);
        EditorTables.PasteGrid(table, 0, 0, [["keep", "one", "two"], ["keep", "three", "four"]]);
        EditorTableLayout.SetWidths(table, [110, 180, 140]);
        var selected = EditorTables.Cells(table, new EditorTables.CellRange(0, 1, 1, 2)).ToArray();
        EditorTables.Align(selected, TextAlignment.Right);
        foreach (var cell in selected) cell.Background = Brushes.LightBlue;
        var editor = Editor(table);
        Measure(editor);
        ClearUndo(editor);
        EditorTables.Edit(editor, table, () => EditorTables.Clear(selected));
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(2, EditorTables.Rows(table).Length);
        Assert.Equal(new double[] { 110, 180, 140 }, table.Columns.Select(item => item.Width.Value));
        Assert.All(selected, cell =>
        {
            Assert.Equal("", EditorTables.CellText(cell));
            Assert.Equal(TextAlignment.Right, cell.TextAlignment);
            Assert.NotNull(cell.Background);
        });
        Assert.Equal("keep", EditorTables.CellText(table.RowGroups[0].Rows[0].Cells[0]));
        editor.Undo();
        var restored = EditorTables.Enumerate(editor.Document.Blocks).Single();
        Assert.Equal("four", EditorTables.CellText(restored.RowGroups[0].Rows[1].Cells[2]));
    });

    [Fact]
    public void Actual_cell_boxes_cover_tall_rows_and_resize_targets_follow_padding_and_reflow() => RunSta(() =>
    {
        var table = EditorTables.Create(2, 2);
        table.Padding = new Thickness(12, 8, 9, 6);
        table.Margin = new Thickness(7, 13, 5, 11);
        table.CellSpacing = 6;
        var row = table.RowGroups[0].Rows[0];
        row.Cells[0].Blocks.Clear();
        row.Cells[0].Blocks.Add(new Paragraph(new Run("short")) { Margin = new Thickness(9, 30, 3, 20) });
        row.Cells[1].Blocks.Clear();
        row.Cells[1].Blocks.Add(new Paragraph(new InlineUIContainer(new Border { Width = 60, Height = 120 })));
        var editor = Editor(table);
        editor.Padding = new Thickness(18, 16, 18, 16);
        Measure(editor);
        var left = EditorTableLayout.CellBounds(table, 0, 0, editor);
        var right = EditorTableLayout.CellBounds(table, 0, 1, editor);
        var bounds = EditorTableLayout.Bounds(table, editor);
        Assert.True(left.Height >= 120);
        Assert.Equal(left.Top, right.Top, precision: 2);
        Assert.Equal(left.Bottom, right.Bottom, precision: 2);
        Assert.True(bounds.Contains(left) && bounds.Contains(right));
        Assert.True(left.Left >= bounds.Left + table.Padding.Left);
        var divider = EditorTableLayout.DividerPosition(table, editor, 1);
        Assert.InRange(divider, left.Right, right.Left);
        Assert.Equal(1, EditorTableLayout.HitDivider(table, editor, new Point(divider + 4, left.Top + 10)));
        Assert.Equal(0, EditorTableLayout.HitDivider(table, editor, new Point(divider + 7, left.Top + 10)));
        Assert.Equal(0, EditorTableLayout.HitDivider(table, editor, new Point(divider, bounds.Bottom + 6)));
        EditorTableLayout.SetWidths(table, [220, 140]);
        Measure(editor);
        Assert.True(EditorTableLayout.DividerPosition(table, editor, 1) > divider + 50);
    });

    [Fact]
    public void Cell_boxes_apply_scroll_offset_once() => RunSta(() =>
    {
        var table = EditorTables.Create(40, 2);
        var editor = Editor(table);
        Measure(editor);
        var before = EditorTableLayout.CellBounds(table, 10, 1, editor);
        var viewer = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor)!;
        viewer.ScrollToVerticalOffset(120);
        Measure(editor);
        var after = EditorTableLayout.CellBounds(table, 10, 1, editor);
        Assert.True(viewer.VerticalOffset > 0);
        Assert.Equal(before.Top, after.Top + viewer.VerticalOffset, precision: 2);
        Assert.Equal(before.Left, after.Left, precision: 2);
        Assert.Equal(before.Size, after.Size);
    });

    private static RichTextBox Editor(Table table, double pageWidth = double.NaN)
    {
        var editor = new RichTextBox { Width = 600, Height = 800, FontSize = 14,
            HorizontalScrollBarVisibility = double.IsNaN(pageWidth) ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden };
        editor.Document.Blocks.Clear();
        editor.Document.PageWidth = pageWidth;
        editor.Document.Blocks.Add(table);
        editor.Document.Blocks.Add(new Paragraph());
        return editor;
    }

    private static void Measure(RichTextBox editor)
    {
        editor.Measure(new Size(600, 800));
        editor.Arrange(new Rect(0, 0, 600, 800));
        editor.UpdateLayout();
    }

    private static void ClearUndo(RichTextBox editor)
    {
        editor.IsUndoEnabled = false;
        editor.IsUndoEnabled = true;
    }

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { scenario(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA table scenario timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
