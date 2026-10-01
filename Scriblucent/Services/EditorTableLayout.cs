using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scriblucent.Services;

/// <summary>Fits native columns to the viewport without persisting temporary display widths.</summary>
internal sealed class EditorTableLayout
{
    private sealed class Preferred(double width) { internal double Width = width; }
    private ConditionalWeakTable<TableColumn, Preferred> _preferred = new();

    internal void Clear() => _preferred = new();
    internal void Forget(Table table)
    {
        foreach (var nested in new[] { table }.Concat(EditorTables.EnumerateCells(table)))
            foreach (var column in nested.Columns) _preferred.Remove(column);
    }

    internal double[] PreferredWidths(Table table)
        => table.Columns.Select(column => _preferred.GetValue(column, item => new Preferred(
            item.Width.IsAbsolute && item.Width.Value > 0 ? item.Width.Value : EditorTables.DefaultColumnWidth)).Width).ToArray();

    internal void RestorePreferred(Table table)
    {
        var widths = PreferredWidths(table);
        SetWidths(table, widths);
        foreach (var nested in EditorTables.EnumerateCells(table))
            SetWidths(nested, PreferredWidths(nested));
    }

    internal void RestorePreferred(FlowDocument document)
    {
        foreach (var table in EditorTables.Enumerate(document.Blocks)) RestorePreferred(table);
    }

    internal IDisposable UsePreferredWidths(FlowDocument document)
    {
        var saved = EditorTables.Enumerate(document.Blocks)
            .Select(table => (Table: table, Widths: table.Columns.Select(column => column.Width).ToArray())).ToArray();
        RestorePreferred(document);
        return new RestoreScope(() =>
        {
            foreach (var (table, widths) in saved)
                if (table.Parent is not null && table.Columns.Count == widths.Length)
                    for (var i = 0; i < widths.Length; i++) table.Columns[i].Width = widths[i];
        });
    }

    internal static void SetWidths(Table table, IReadOnlyList<double> widths)
    {
        if (widths.Count != table.Columns.Count || widths.Any(width => !double.IsFinite(width) || width <= 0))
            throw new ArgumentException("Each column requires a positive finite width.");
        for (var i = 0; i < widths.Count; i++) table.Columns[i].Width = new GridLength(widths[i]);
    }

    internal bool Apply(FlowDocument document, double availableWidth, IReadOnlyList<Table>? tables = null)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return false;
        var changed = false;
        foreach (var table in tables ?? EditorTables.Enumerate(document.Blocks))
        {
            if (table.Parent is null) continue;
            if (table.Columns.Count == 0)
            {
                var count = EditorTables.Rows(table).Select(row => row.Cells.Cast<TableCell>().Sum(cell => cell.ColumnSpan)).DefaultIfEmpty(0).Max();
                for (var i = 0; i < count; i++) table.Columns.Add(new TableColumn { Width = new GridLength(EditorTables.DefaultColumnWidth) });
            }
            var width = availableWidth;
            if (EditorTables.Ancestor<TableCell>(table.Parent) is { } parentCell
                && EditorTables.Ancestor<Table>(parentCell.Parent) is { } parentTable)
            {
                var parentRow = (TableRow)parentCell.Parent;
                var column = 0;
                foreach (TableCell cell in parentRow.Cells)
                {
                    if (ReferenceEquals(cell, parentCell)) break;
                    column += cell.ColumnSpan;
                }
                width = parentTable.Columns.Skip(column).Take(parentCell.ColumnSpan).Sum(item => item.Width.Value)
                    - parentCell.Padding.Left - parentCell.Padding.Right
                    - parentCell.BorderThickness.Left - parentCell.BorderThickness.Right;
            }
            var widths = PreferredWidths(table);
            var space = ColumnSpace(table, width);
            var total = widths.Sum();
            var factor = total > space ? space / total : 1;
            for (var i = 0; i < widths.Length; i++)
            {
                var desired = Math.Max(0.1, widths[i] * factor);
                if (table.Columns[i].Width.IsAbsolute && Math.Abs(table.Columns[i].Width.Value - desired) < 0.01) continue;
                table.Columns[i].Width = new GridLength(desired);
                changed = true;
            }
        }
        return changed;
    }

    internal static double ColumnSpace(Table table, double availableWidth)
        => Math.Max(1, availableWidth - FiniteInset(table.Margin.Left) - FiniteInset(table.Margin.Right)
            - FiniteInset(table.Padding.Left) - FiniteInset(table.Padding.Right)
            - table.CellSpacing * (table.Columns.Count + 1));

    private static double FiniteInset(double value) => double.IsFinite(value) ? value : 0;

    internal static double[] Resize(double[] original, int divider, double delta, double maximum)
    {
        var widths = (double[])original.Clone();
        if (widths.Length == 0) return widths;
        if (divider >= widths.Length)
        {
            var total = widths.Sum();
            var minimum = Math.Min(EditorTables.MinimumColumnWidth, widths.Min());
            var minimumTotal = minimum / widths.Min() * total;
            var desired = Math.Clamp(total + delta, minimumTotal, Math.Max(minimumTotal, maximum));
            var factor = desired / total;
            for (var i = 0; i < widths.Length; i++) widths[i] *= factor;
        }
        else if (divider > 0)
        {
            var left = divider - 1;
            var right = divider;
            var minimum = Math.Min(EditorTables.MinimumColumnWidth, Math.Min(widths[left], widths[right]));
            delta = Math.Clamp(delta, minimum - widths[left], widths[right] - minimum);
            widths[left] += delta;
            widths[right] -= delta;
        }
        return widths;
    }

    internal static Rect Bounds(Table table, RichTextBox editor)
    {
        return WpfEditorGeometry.For(editor).ElementBounds(table);
    }

    internal static Rect CellBounds(Table table, int rowIndex, int columnIndex, RichTextBox editor)
        => WpfEditorGeometry.For(editor).ElementBounds(EditorTables.Rows(table)[rowIndex].Cells[columnIndex]);

    internal static double DividerPosition(Table table, RichTextBox editor, int divider)
    {
        var geometry = WpfEditorGeometry.For(editor);
        if (divider == table.Columns.Count) return geometry.ElementBounds(table).Right;
        if (divider <= 0 || divider > table.Columns.Count) return double.NaN;
        foreach (var row in EditorTables.Rows(table))
        {
            var column = 0;
            for (var i = 0; i + 1 < row.Cells.Count; i++)
            {
                column += row.Cells[i].ColumnSpan;
                if (column != divider) continue;
                var left = geometry.ElementBounds(row.Cells[i]);
                var right = geometry.ElementBounds(row.Cells[i + 1]);
                if (!left.IsEmpty && !right.IsEmpty) return (left.Right + right.Left) / 2;
            }
        }
        return double.NaN;
    }

    internal static int HitDivider(Table table, RichTextBox editor, Point point)
    {
        var bounds = Bounds(table, editor);
        if (bounds.IsEmpty || point.Y < bounds.Top || point.Y > bounds.Bottom) return 0;
        for (var divider = 1; divider <= table.Columns.Count; divider++)
            if (Math.Abs(point.X - DividerPosition(table, editor, divider)) <= 5) return divider;
        return 0;
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
