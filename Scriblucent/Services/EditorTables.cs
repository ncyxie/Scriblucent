using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Scriblucent.Services;

/// <summary>Native document table operations shared by the editor and its clipboard commands.</summary>
internal static class EditorTables
{
    internal const double DefaultColumnWidth = 160;
    internal const double MinimumColumnWidth = 40;
    internal const string ClipboardGridFormat = "Scriblucent.TableGrid";

    internal readonly record struct CellRange(int FirstRow, int LastRow, int FirstColumn, int LastColumn);

    internal static IEnumerable<Table> Enumerate(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            if (block is Table table)
            {
                yield return table;
                foreach (var cell in Rows(table).SelectMany(row => row.Cells.Cast<TableCell>()))
                    foreach (var nested in Enumerate(cell.Blocks))
                        yield return nested;
            }
            else if (block is Section section)
            {
                foreach (var nested in Enumerate(section.Blocks)) yield return nested;
            }
            else if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                    foreach (var nested in Enumerate(item.Blocks)) yield return nested;
            }
        }
    }

    internal static TableRow[] Rows(Table table)
        => table.RowGroups.Cast<TableRowGroup>().SelectMany(group => group.Rows.Cast<TableRow>()).ToArray();

    internal static IEnumerable<Table> EnumerateCells(Table table)
        => Rows(table).SelectMany(row => row.Cells.Cast<TableCell>()).SelectMany(cell => Enumerate(cell.Blocks));

    internal static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (current is T found) return found;
        return null;
    }

    internal static bool IsRectangular(Table table)
    {
        var rows = Rows(table);
        return table.Columns.Count > 0 && rows.Length > 0
            && rows.All(row => row.Cells.Count == table.Columns.Count
                && row.Cells.Cast<TableCell>().All(cell => cell.ColumnSpan == 1 && cell.RowSpan == 1));
    }

    internal static TableCell CreateCell(Brush? border = null, string text = "")
        => new(new Paragraph(new Run(text)))
        {
            BorderBrush = border ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4)
        };

    internal static Table Create(int rows, int columns, Brush? border = null)
    {
        var table = new Table();
        for (var column = 0; column < columns; column++)
            table.Columns.Add(new TableColumn { Width = new GridLength(DefaultColumnWidth) });
        var group = new TableRowGroup();
        for (var row = 0; row < rows; row++)
        {
            var item = new TableRow();
            for (var column = 0; column < columns; column++) item.Cells.Add(CreateCell(border));
            group.Rows.Add(item);
        }
        table.RowGroups.Add(group);
        return table;
    }

    internal static bool TryGetRange(TextPointer start, TextPointer end, out Table? table, out CellRange range)
    {
        table = Ancestor<Table>(start.GetInsertionPosition(LogicalDirection.Forward)?.Parent);
        range = default;
        // Native rectangular selections end at the next cell's first insertion position.
        // The preceding insertion belongs to the actual last selected cell.
        var lastPosition = end.GetNextInsertionPosition(LogicalDirection.Backward);
        if (start.CompareTo(end) >= 0 || table is null || !IsRectangular(table)
            || !ReferenceEquals(table, Ancestor<Table>(lastPosition?.Parent)))
            return false;
        var first = Ancestor<TableCell>(start.GetInsertionPosition(LogicalDirection.Forward)?.Parent);
        var last = Ancestor<TableCell>(lastPosition?.Parent);
        var rows = Rows(table);
        if (first is null && last is null && start.CompareTo(table.ContentStart) <= 0
            && end.CompareTo(table.ContentEnd) >= 0)
        {
            range = new CellRange(0, rows.Length - 1, 0, table.Columns.Count - 1);
            return true;
        }
        if (first is null || last is null) return false;
        var firstRow = Array.FindIndex(rows, row => row.Cells.Contains(first));
        var lastRow = Array.FindIndex(rows, row => row.Cells.Contains(last));
        if (firstRow < 0 || lastRow < 0) return false;
        var firstColumn = rows[firstRow].Cells.IndexOf(first);
        var lastColumn = rows[lastRow].Cells.IndexOf(last);
        // A partial text selection within one cell remains an ordinary rich-text selection.
        if (ReferenceEquals(first, last)
            && (start.CompareTo(first.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!) > 0
                || end.CompareTo(first.ContentEnd) < 0))
            return false;
        range = new CellRange(Math.Min(firstRow, lastRow), Math.Max(firstRow, lastRow),
            Math.Min(firstColumn, lastColumn), Math.Max(firstColumn, lastColumn));
        return true;
    }

    internal static IEnumerable<TableCell> Cells(Table table, CellRange range)
    {
        var rows = Rows(table);
        for (var row = range.FirstRow; row <= range.LastRow; row++)
            for (var column = range.FirstColumn; column <= range.LastColumn; column++)
                yield return rows[row].Cells[column];
    }

    internal static TableCell[] SelectedCells(TextPointer start, TextPointer end, Table table, TableCell fallback)
        => TryGetRange(start, end, out var selectedTable, out var range) && ReferenceEquals(table, selectedTable)
            ? Cells(table, range).ToArray() : [fallback];

    internal static void Align(IEnumerable<TableCell> cells, TextAlignment alignment)
    {
        foreach (var cell in cells)
        {
            cell.TextAlignment = alignment;
            AlignBlocks(cell.Blocks, alignment);
        }
    }

    private static void AlignBlocks(BlockCollection blocks, TextAlignment alignment)
    {
        foreach (Block block in blocks)
        {
            if (block is Paragraph paragraph) paragraph.TextAlignment = alignment;
            else if (block is Section section) AlignBlocks(section.Blocks, alignment);
            else if (block is List list)
                foreach (ListItem item in list.ListItems) AlignBlocks(item.Blocks, alignment);
        }
    }

    internal static void Clear(IEnumerable<TableCell> cells)
    {
        foreach (var cell in cells)
        {
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph());
        }
    }

    internal static string CellText(TableCell cell)
    {
        var text = new TextRange(cell.ContentStart, cell.ContentEnd).Text;
        // Remove the synthetic final paragraph break, retaining user-authored blank lines.
        return text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text;
    }

    internal static string CopyGrid(Table table, CellRange range)
    {
        var rows = Rows(table);
        var output = new StringBuilder();
        for (var row = range.FirstRow; row <= range.LastRow; row++)
        {
            if (row > range.FirstRow) output.Append("\r\n");
            for (var column = range.FirstColumn; column <= range.LastColumn; column++)
            {
                if (column > range.FirstColumn) output.Append('\t');
                var text = CellText(rows[row].Cells[column]);
                if (text.IndexOfAny(['\t', '\r', '\n', '"']) >= 0)
                    output.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else output.Append(text);
            }
        }
        return output.ToString();
    }

    internal static bool TryReadGrid(IDataObject data, out string[][] grid)
    {
        var ownGrid = data.GetData(ClipboardGridFormat) as string;
        var text = ownGrid ?? data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        var tableSource = ownGrid is not null
            || data.GetData(DataFormats.Rtf) is string rtf && rtf.Contains("\\trowd", StringComparison.Ordinal)
            || data.GetData(DataFormats.Html) is string html && html.Contains("<table", StringComparison.OrdinalIgnoreCase);
        return TryParseGrid(text, out grid, tableSource);
    }

    internal static bool TryParseGrid(string? text, out string[][] grid, bool allowSingleColumn = false)
    {
        grid = [];
        if (text is null || text.Length == 0 && !allowSingleColumn) return false;
        var rows = new System.Collections.Generic.List<string[]>();
        var fields = new System.Collections.Generic.List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var hasSeparator = false;
        var atRowEnd = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"') field.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closedQuote = true; }
                atRowEnd = false;
                continue;
            }
            if (ch == '"' && field.Length == 0 && !closedQuote) { quoted = true; atRowEnd = false; continue; }
            if (ch is '\t' or '\r' or '\n')
            {
                fields.Add(field.ToString()); field.Clear(); closedQuote = false;
                if (ch == '\t') { hasSeparator = true; atRowEnd = false; }
                else
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    rows.Add(fields.ToArray()); fields.Clear(); atRowEnd = true;
                }
            }
            else
            {
                if (closedQuote) return false;
                field.Append(ch); atRowEnd = false;
            }
        }
        if (quoted) return false;
        if (!atRowEnd) { fields.Add(field.ToString()); rows.Add(fields.ToArray()); }
        if ((!hasSeparator && !allowSingleColumn) || rows.Count == 0 || (rows[0].Length < 2 && !allowSingleColumn)
            || rows.Any(row => row.Length != rows[0].Length)) return false;
        grid = rows.ToArray();
        return true;
    }

    internal static void PasteGrid(Table table, int startRow, int startColumn, string[][] grid, Brush? border = null)
    {
        if (!IsRectangular(table) || startRow < 0 || startColumn < 0 || grid.Length == 0
            || grid.Any(row => row.Length != grid[0].Length))
            throw new ArgumentException("A rectangular table and grid are required.");
        var requiredColumns = startColumn + grid[0].Length;
        while (table.Columns.Count < requiredColumns)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(DefaultColumnWidth) });
            foreach (var row in Rows(table)) row.Cells.Add(CreateCell(border));
        }
        var group = table.RowGroups[^1];
        while (Rows(table).Length < startRow + grid.Length)
        {
            var row = new TableRow();
            for (var column = 0; column < table.Columns.Count; column++) row.Cells.Add(CreateCell(border));
            group.Rows.Add(row);
        }
        var rows = Rows(table);
        for (var row = 0; row < grid.Length; row++)
            for (var column = 0; column < grid[row].Length; column++)
            {
                var cell = rows[startRow + row].Cells[startColumn + column];
                cell.Blocks.Clear();
                cell.Blocks.Add(new Paragraph(new Run(grid[row][column])));
            }
    }

    internal static void MoveColumn(Table table, int column, int delta)
    {
        if (!IsRectangular(table) || column < 0 || column + delta < 0 || column + delta >= table.Columns.Count)
            return;
        var item = table.Columns[column];
        table.Columns.RemoveAt(column);
        table.Columns.Insert(column + delta, item);
        foreach (var row in Rows(table))
        {
            var cell = row.Cells[column];
            row.Cells.RemoveAt(column);
            row.Cells.Insert(column + delta, cell);
        }
    }

    internal static bool HasHeader(Table table)
        => Rows(table).FirstOrDefault() is { Background: not null } row && row.FontWeight == FontWeights.Bold;

    internal static void ToggleHeader(Table table)
    {
        var row = Rows(table).FirstOrDefault();
        if (row is null) return;
        if (HasHeader(table)) { row.ClearValue(TextElement.FontWeightProperty); row.ClearValue(TextElement.BackgroundProperty); }
        else
        {
            row.FontWeight = FontWeights.Bold;
            // A translucent tint remains legible with the note's foreground in either theme.
            row.Background = new SolidColorBrush(Color.FromArgb(32, 96, 128, 192));
        }
    }

    internal static BlockCollection? ParentBlocks(Table table) => table.Parent switch
    {
        FlowDocument document => document.Blocks,
        Section section => section.Blocks,
        ListItem item => item.Blocks,
        TableCell cell => cell.Blocks,
        _ => null
    };

    /// <summary>
    /// Removing and reinserting the native fragment records its column properties as well
    /// as its text. Changing TableColumn.Width directly has no WPF undo unit. The same live
    /// elements are reinserted, so inline image controls are not cloned or disconnected.
    /// </summary>
    internal static void Edit(RichTextBox editor, Table table, Action action, Func<bool>? delete = null, Action? afterAttach = null)
    {
        var blocks = ParentBlocks(table) ?? throw new InvalidOperationException("The table is detached.");
        var next = table.NextBlock;
        var failed = false;
        // Duplication/formatting must see the note's inherited typography even while
        // the fragment is detached. These temporary values never enter the document.
        var inherited = new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty,
            TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.FontStretchProperty,
            TextElement.ForegroundProperty, Block.TextAlignmentProperty, Block.FlowDirectionProperty }
            .Where(property => table.ReadLocalValue(property) == DependencyProperty.UnsetValue)
            .Select(property => (Property: property, Value: table.GetValue(property))).ToArray();
        editor.BeginChange();
        try
        {
            blocks.Remove(table);
            foreach (var (property, value) in inherited) table.SetValue(property, value);
            try { action(); }
            catch { failed = true; throw; }
            finally
            {
                foreach (var (property, _) in inherited) table.ClearValue(property);
                if (failed || delete?.Invoke() != true)
                {
                    if (next is null) blocks.Add(table);
                    else blocks.InsertBefore(next, table);
                }
            }
            afterAttach?.Invoke();
        }
        catch { failed = true; throw; }
        finally
        {
            editor.EndChange();
            if (failed && editor.CanUndo) editor.Undo();
        }
    }
}
