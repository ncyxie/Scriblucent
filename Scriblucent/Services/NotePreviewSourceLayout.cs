using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Formats only the extracted prefix. No hidden control, window or image decoding.</summary>
internal static class NotePreviewSourceLayout
{
    [ThreadStatic] private static TextFormatter? _formatter;

    internal static List<(int Offset, Rect Rect)> Measure(FlowDocument document, string text,
        IReadOnlyList<NotePreviewService.Position> positions, IReadOnlyList<NoteImageAttachment> images,
        double width, bool wrap)
    {
        var result = new List<(int, Rect)>();
        if (positions.Count == 0) return result;
        var formatter = _formatter ??= TextFormatter.Create(TextFormattingMode.Ideal);
        var y = 0d;
        var rowTops = new Dictionary<TableRow, double>();
        var rowBottoms = new Dictionary<TableRow, double>();
        var cellBottoms = new Dictionary<TableCell, double>();
        using var paragraphs = Paragraphs(document.Blocks).GetEnumerator();
        Paragraph? previousParagraph = null;
        int start = 0;
        while (start < positions.Count)
        {
            var paragraph = positions[start].Pointer.Paragraph;
            if (!ReferenceEquals(previousParagraph, paragraph))
            {
                // Empty paragraphs have no normalized preview characters, but still occupy
                // source space. Their height cannot disappear from image-range decisions.
                while (paragraphs.MoveNext() && !ReferenceEquals(paragraphs.Current, paragraph))
                    y += paragraphs.Current.FontSize * 1.2 + Finite(paragraphs.Current.Margin.Top) + Finite(paragraphs.Current.Margin.Bottom);
                previousParagraph = paragraph;
            }
            var end = start + 1;
            while (end < positions.Count && ReferenceEquals(positions[end].Pointer.Paragraph, paragraph)) end++;
            var margin = paragraph?.Margin ?? new Thickness(0);
            var cell = Ancestor<TableCell>(paragraph);
            var row = cell?.Parent as TableRow;
            var table = Ancestor<Table>(cell);
            double cellLeft = 0, cellWidth = width;
            if (cell is not null && row is not null && table is not null)
            {
                if (!rowTops.TryGetValue(row, out var rowTop)) rowTops[row] = rowTop = y;
                y = cellBottoms.GetValueOrDefault(cell, rowTop + Finite(cell.Padding.Top));
                var columns = row.Cells.Sum(c => c.ColumnSpan);
                var widths = Enumerable.Range(0, columns).Select(i => i < table.Columns.Count && table.Columns[i].Width.IsAbsolute
                    ? table.Columns[i].Width.Value : width / Math.Max(1, columns)).ToArray();
                var column = 0;
                foreach (var sibling in row.Cells) { if (ReferenceEquals(sibling, cell)) break; column += sibling.ColumnSpan; }
                cellLeft = widths.Take(column).Sum() + Finite(cell.Padding.Left);
                cellWidth = Math.Max(24, widths.Skip(column).Take(cell.ColumnSpan).Sum() - Finite(cell.Padding.Left) - Finite(cell.Padding.Right));
            }
            y += Finite(margin.Top);
            var source = new Source(text.Substring(start, end - start), positions.Skip(start).Take(end - start).ToArray());
            var properties = new ParagraphProperties(source.Props(0), wrap, paragraph?.FlowDirection ?? FlowDirection.LeftToRight,
                paragraph is not null && double.IsFinite(paragraph.LineHeight) ? paragraph.LineHeight : 0);
            var left = cellLeft + Finite(margin.Left);
            var available = Math.Max(24, cellWidth - Finite(margin.Left) - Finite(margin.Right));
            TextLineBreak? previous = null;
            int offset = 0;
            try
            {
                while (offset < source.Text.Length)
                {
                    var lineHeight = source.Props(offset).FontRenderingEmSize * 1.2;
                    double laneLeft = left, laneWidth = available;
                    foreach (var image in images)
                    {
                        if (image.WrapMode is not (NoteImageWrapMode.Square or NoteImageWrapMode.TopAndBottom)
                            || !NotePreviewService.ValidGeometry(image)
                            || image.Top - NoteWrapMath.DefaultWrapGap >= y + lineHeight
                            || image.Top + image.Height + NoteWrapMath.DefaultWrapGap <= y) continue;
                        if (image.WrapMode == NoteImageWrapMode.TopAndBottom)
                            y = image.Top + image.Height + NoteWrapMath.DefaultWrapGap;
                        else
                        {
                            var insets = NoteWrapMath.ComputeInsets(image.Left - NoteWrapMath.DefaultWrapGap,
                                image.Left + image.Width + NoteWrapMath.DefaultWrapGap, available, NoteWrapMath.MinimumLaneWidth);
                            if (insets.Applies)
                            { laneLeft = left + insets.LeftInset; laneWidth = Math.Max(24, available - insets.LeftInset - insets.RightInset); }
                        }
                    }
                    using var line = formatter.FormatLine(source, offset, wrap ? laneWidth : 10000, properties, previous);
                    result.Add((positions[start + offset].Offset, new Rect(laneLeft, y, Math.Max(1, line.Width), line.Height)));
                    y += line.Height;
                    offset += Math.Max(1, line.Length);
                    previous?.Dispose(); previous = line.GetTextLineBreak();
                }
            }
            finally { previous?.Dispose(); }
            y += Finite(margin.Bottom);
            if (cell is not null && row is not null)
            {
                cellBottoms[cell] = y;
                rowBottoms[row] = Math.Max(rowBottoms.GetValueOrDefault(row), y + Finite(cell.Padding.Bottom));
                y = rowBottoms[row];
            }
            start = end;
        }
        return result;
    }

    private static double Finite(double number) => double.IsFinite(number) ? Math.Max(0, number) : 0;
    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is Section section)
                foreach (var child in Paragraphs(section.Blocks)) yield return child;
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
            else if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var child in Paragraphs(cell.Blocks)) yield return child;
        }
    }

    private sealed class Source(string text, NotePreviewService.Position[] positions) : TextSource
    {
        internal string Text => text;
        private readonly Dictionary<int, RunProperties> _properties = new();
        internal RunProperties Props(int index)
        {
            index = Math.Clamp(index, 0, positions.Length - 1);
            if (_properties.TryGetValue(index, out var existing)) return existing;
            var pointer = positions[index].Pointer;
            var properties = new RunProperties(new Typeface((FontFamily)pointer.Parent.GetValue(TextElement.FontFamilyProperty),
                (FontStyle)pointer.Parent.GetValue(TextElement.FontStyleProperty),
                (FontWeight)pointer.Parent.GetValue(TextElement.FontWeightProperty),
                (FontStretch)pointer.Parent.GetValue(TextElement.FontStretchProperty)),
                (double)pointer.Parent.GetValue(TextElement.FontSizeProperty));
            _properties[index] = properties;
            return properties;
        }
        public override TextRun GetTextRun(int index)
        {
            if (index >= text.Length) return new TextEndOfParagraph(1);
            if (IsLineBreak(index)) return new TextEndOfLine(1);
            if (text[index] == '\uFFFC' && positions[index].Image is { } picture && NotePreviewService.ValidGeometry(picture))
                return new PictureRun(Props(index), picture.Width, picture.Height);
            // Run boundaries preserve the source's formatting without a control/template tree.
            var parent = positions[index].Pointer.Parent;
            int end = index + 1;
            while (end < text.Length && text[end] != '\uFFFC' && !IsLineBreak(end) && ReferenceEquals(positions[end].Pointer.Parent, parent)) end++;
            return new TextCharacters(text, index, end - index, Props(index));
        }
        private bool IsLineBreak(int index) => positions[index].Pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak;
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
            => new(limit, new(CultureInfo.CurrentCulture, new CharacterBufferRange(text, 0, Math.Min(limit, text.Length))));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class PictureRun(TextRunProperties properties, double width, double height) : TextEmbeddedObject
    {
        public override int Length => 1;
        public override TextRunProperties Properties => properties;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(width, height, height);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, -height, width, height);
        public override void Draw(DrawingContext context, Point origin, bool rightToLeft, bool sideways) { }
    }

    private sealed class RunProperties(Typeface typeface, double size) : TextRunProperties
    {
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => Brushes.Black;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
        public override TextEffectCollection? TextEffects => null;
    }

    private sealed class ParagraphProperties(TextRunProperties defaults, bool wrap, FlowDirection direction, double height) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => direction;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => height;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => defaults;
        public override TextWrapping TextWrapping => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
}
