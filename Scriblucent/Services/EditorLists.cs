using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DocumentList = System.Windows.Documents.List;

namespace Scriblucent.Services;

/// <summary>One set of structural editing rules for every list marker. Checkbox glyphs remain
/// ordinary text for interoperable serialization, but their items use the same tree as bullets.</summary>
internal sealed class EditorLists
{
    internal const string UncheckedPrefix = "☐ ";
    internal const string CheckedPrefix = "☑ ";
    private readonly RichTextBox _editor;
    private readonly Func<double> _indent;
    private bool _editing;
    private bool _restoring;
    private MetadataEntry[] _lastState = [];
    private readonly Stack<MetadataEntry[]> _undo = new();
    private readonly Stack<MetadataEntry[]> _redo = new();
    private readonly Dictionary<DocumentList, ListState> _states = new();
    private static readonly Uri MetadataUri = new("/Scriblucent/ListMetadata.json", UriKind.Relative);
    internal Action? BeforeChange { get; set; }
    internal Action? AfterChange { get; set; }
    private sealed record ListState(Guid Id, Guid? ContinuesFrom = null, bool ExplicitStyle = false, bool Checklist = false, Guid? TypographyGroup = null);
    internal sealed record MetadataEntry(int Ordinal, Guid Id, Guid? ContinuesFromId, bool ExplicitStyle, bool Checklist = false, Guid? TypographyGroup = null);
    internal sealed record Metadata(int Version, MetadataEntry[] Lists);

    internal EditorLists(RichTextBox editor, Func<double> indent)
    {
        _editor = editor;
        _indent = indent;
    }

    internal static IEnumerable<DocumentList> Lists(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            if (block is DocumentList list)
            {
                yield return list;
                foreach (ListItem item in list.ListItems)
                    foreach (var child in Lists(item.Blocks)) yield return child;
            }
            else if (block is Section section)
            {
                foreach (var child in Lists(section.Blocks)) yield return child;
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                foreach (TableRow row in group.Rows)
                foreach (TableCell cell in row.Cells)
                    foreach (var child in Lists(cell.Blocks)) yield return child;
            }
        }
    }

    internal static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is DocumentList list)
            {
                foreach (ListItem item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
            }
            else if (block is Section section)
            {
                foreach (var child in Paragraphs(section.Blocks)) yield return child;
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                foreach (TableRow row in group.Rows)
                foreach (TableCell cell in row.Cells)
                    foreach (var child in Paragraphs(cell.Blocks)) yield return child;
            }
        }
    }

    internal static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T value) return value;
            element = (element as FrameworkContentElement)?.Parent;
        }
        return null;
    }

    internal static BlockCollection? ParentBlocks(Block block) => block.Parent switch
    {
        FlowDocument document => document.Blocks,
        Section section => section.Blocks,
        ListItem item => item.Blocks,
        TableCell cell => cell.Blocks,
        _ => null
    };

    internal static string InlineText(Paragraph paragraph)
    {
        var result = new StringBuilder();
        var pointer = paragraph.ContentStart;
        while (pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = pointer.GetTextInRun(LogicalDirection.Forward);
                result.Append(text);
                pointer = pointer.GetPositionAtOffset(text.Length)!;
            }
            else
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
                    && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) result.Append('\n');
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement)
                    result.Append('\uFFFC');
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (next is null) break;
                pointer = next;
            }
        }
        return result.ToString();
    }

    internal static TextPointer AdvanceCharacters(TextPointer pointer, int count)
    {
        while (count > 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int take = Math.Min(count, pointer.GetTextInRun(LogicalDirection.Forward).Length);
                if (take > 0) { pointer = pointer.GetPositionAtOffset(take)!; count -= take; continue; }
            }
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null) break;
            pointer = next;
        }
        return pointer;
    }

    internal static bool Checkbox(Paragraph paragraph, out TextPointer textStart, out bool isChecked)
    {
        // TextRange.Text synthesizes a marker and tab for its ancestor list. Read real characters.
        isChecked = false;
        textStart = paragraph.ContentStart;
        var pointer = paragraph.ContentStart;
        var prefix = new StringBuilder(2);
        while (prefix.Length < 2 && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                int take = Math.Min(2 - prefix.Length, run.Length);
                prefix.Append(run, 0, take);
                pointer = pointer.GetPositionAtOffset(take)!;
            }
            else
            {
                // A split marker is not a prefix, even if the next line starts with
                // its missing space. Never let deletion consume another line or item.
                if (context == TextPointerContext.EmbeddedElement
                    || pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) return false;
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (next is null) break;
                pointer = next;
            }
        }
        var text = prefix.ToString();
        isChecked = text == CheckedPrefix;
        if (!isChecked && text != UncheckedPrefix) return false;
        textStart = pointer;
        return true;
    }

    private static Paragraph? FirstParagraph(ListItem item) => item.Blocks.OfType<Paragraph>().FirstOrDefault();
    private static bool IsChecklist(DocumentList list) => list.MarkerStyle == TextMarkerStyle.None
        && list.ListItems.Cast<ListItem>().Any(item => FirstParagraph(item) is { } p && Checkbox(p, out _, out _));
    internal static bool Numbered(TextMarkerStyle style) => style is TextMarkerStyle.Decimal
        or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
    private int Depth(DocumentList list)
    {
        int result = 0;
        for (DocumentList? cursor = list; cursor is not null;
             cursor = Ancestor<DocumentList>((cursor.Parent as ListItem)?.Parent))
        {
            double left = double.IsFinite(cursor.Margin.Left) ? Math.Max(0, cursor.Margin.Left) : 0;
            result += (int)Math.Clamp(Math.Round(left / Math.Max(24, _indent())), 0, 8);
            if (!ReferenceEquals(cursor, list)) result++;
        }
        return result;
    }
    internal int NestingDepth(DocumentList list) => Depth(list);

    internal List<Paragraph> Targets()
    {
        var selection = _editor.Selection;
        if (selection.IsEmpty)
            return ParagraphAt(selection.Start) is { } paragraph ? [paragraph] : [];
        return Paragraphs(_editor.Document.Blocks).Where(p =>
            p.ElementEnd.CompareTo(selection.Start) > 0 && p.ContentStart.CompareTo(selection.End) < 0).ToList();
    }

    private static Paragraph? ParagraphAt(TextPointer pointer) => pointer.Paragraph
        ?? pointer.GetInsertionPosition(LogicalDirection.Forward)?.Paragraph
        ?? pointer.GetInsertionPosition(LogicalDirection.Backward)?.Paragraph;

    internal bool HasList => Targets().Any(p => Ancestor<ListItem>(p) is not null || Checkbox(p, out _, out _));
    internal bool CanIndent => TargetItems(true).Any(item => item.Parent is DocumentList list && Depth(list) < 8);
    internal bool MixedSelection => Targets().Select(p => Ancestor<DocumentList>(p)?.MarkerStyle).Distinct().Take(2).Count() > 1;
    internal TextMarkerStyle? SelectionStyle
    {
        get
        {
            var styles = Targets().Select(p => Ancestor<DocumentList>(p)?.MarkerStyle
                ?? (Checkbox(p, out _, out _) ? TextMarkerStyle.None : (TextMarkerStyle?)null)).Distinct().ToArray();
            return styles.Length == 1 ? styles[0] : null;
        }
    }

    private ListState State(DocumentList list)
    {
        if (!_states.TryGetValue(list, out var state)) _states[list] = state = new(Guid.NewGuid(), Checklist: IsChecklist(list));
        return state;
    }

    private DocumentList NewList(TextMarkerStyle style, int start = 1)
    {
        var list = new DocumentList { MarkerStyle = style, StartIndex = Math.Max(1, start),
            Margin = new Thickness(0), Padding = new Thickness(Math.Max(24, _indent()), 0, 0, 0) };
        _states[list] = new(Guid.NewGuid(), Checklist: style == TextMarkerStyle.None);
        return list;
    }

    private readonly record struct Endpoint(Paragraph Paragraph, int Characters);
    private Endpoint? CaptureEndpoint(TextPointer pointer)
    {
        var paragraph = ParagraphAt(pointer);
        if (paragraph is null) return null;
        int prefix = Checkbox(paragraph, out _, out _) ? 2 : 0;
        return new Endpoint(paragraph, Math.Max(0, RawText(paragraph.ContentStart, pointer).Length - prefix));
    }

    private TextPointer? RestoreEndpoint(Endpoint? endpoint)
    {
        if (endpoint is not { } value || !ReferenceEquals(Ancestor<FlowDocument>(value.Paragraph), _editor.Document)) return null;
        int prefix = Checkbox(value.Paragraph, out _, out _) ? 2 : 0;
        return AdvanceCharacters(value.Paragraph.ContentStart,
            Math.Min(InlineText(value.Paragraph).Length, value.Characters + prefix)).GetInsertionPosition(LogicalDirection.Forward);
    }

    private void RestoreSelection(Endpoint? start, Endpoint? end)
    {
        if (RestoreEndpoint(start) is { } restoredStart && RestoreEndpoint(end) is { } restoredEnd
            && (_editor.Selection.Start.CompareTo(restoredStart) != 0 || _editor.Selection.End.CompareTo(restoredEnd) != 0))
            _editor.Selection.Select(restoredStart, restoredEnd);
    }

    private void Edit(Action action, bool preserveSelection = true)
    {
        if (_editing) { action(); return; }
        WpfEditorInput.Flush(_editor);
        var start = preserveSelection ? CaptureEndpoint(_editor.Selection.Start) : null;
        var end = preserveSelection ? CaptureEndpoint(_editor.Selection.End) : null;
        _editing = true;
        try
        {
            BeforeChange?.Invoke();
            _editor.BeginChange();
            try
            {
                CoalesceTypographyGroups();
                action(); RefreshNumbering(); RefreshLayout(all: true);
                RestoreSelection(start, end);
            }
            finally { _editor.EndChange(); }
        }
        finally { _editing = false; AfterChange?.Invoke(); }
    }

    /// <summary>Metadata shares the editor's undo units, including units created by ordinary typing.</summary>
    internal void TextChanged(TextChangedEventArgs e)
    {
        if (_restoring) return;
        if (e.UndoAction == UndoAction.Undo && _undo.TryPop(out var previous))
        {
            _redo.Push(_lastState); RestoreState(previous);
        }
        else if (e.UndoAction == UndoAction.Redo && _redo.TryPop(out var next))
        {
            _undo.Push(_lastState); RestoreState(next);
        }
        else if (e.UndoAction == UndoAction.Create)
        {
            _undo.Push(_lastState); _redo.Clear();
            if (_editor.UndoLimit >= 0 && _undo.Count > _editor.UndoLimit)
            {
                var retained = _undo.Take(_editor.UndoLimit).Reverse().ToArray();
                _undo.Clear(); foreach (var snapshot in retained) _undo.Push(snapshot);
            }
        }
        else if (e.UndoAction == UndoAction.Clear) { _undo.Clear(); _redo.Clear(); }
        // WPF has applied the typed character's formatting by this point, but its
        // typing undo unit is still open. Refresh only when marker typography differs.
        bool layoutChanged = !_editing && e.UndoAction is UndoAction.Create or UndoAction.Merge
            && e.Changes.Any(change => change.AddedLength > 0) && NeedsMarkerLayout(_editor.CaretPosition.Paragraph);
        if (layoutChanged) RefreshLayout();
        // Ordinary typing keeps its snapshot without scanning a long note on each key.
        bool structureChanged = _editing || layoutChanged || e.UndoAction is UndoAction.Undo or UndoAction.Redo or UndoAction.Clear
            || e.Changes.Any(change => change.AddedLength > 1 || change.RemovedLength > 1);
        if (structureChanged && !_editing)
        {
            _restoring = true;
            try { WpfNonUndoChange.Run(_editor, RefreshNumbering); }
            finally { _restoring = false; }
        }
        if (structureChanged)
            _lastState = Snapshot();
    }

    private MetadataEntry[] Snapshot() => Lists(_editor.Document.Blocks).Select((list, ordinal) =>
    {
        var state = State(list);
        return new MetadataEntry(ordinal, state.Id, state.ContinuesFrom, state.ExplicitStyle, state.Checklist, state.TypographyGroup);
    }).ToArray();

    private void RestoreState(MetadataEntry[] snapshot)
    {
        _states.Clear();
        var lists = Lists(_editor.Document.Blocks).ToArray();
        foreach (var entry in snapshot)
            if (entry.Ordinal >= 0 && entry.Ordinal < lists.Length)
                _states[lists[entry.Ordinal]] = new(entry.Id, entry.ContinuesFromId, entry.ExplicitStyle, entry.Checklist, entry.TypographyGroup);
    }

    internal void ResetHistory()
    {
        _undo.Clear(); _redo.Clear();
        var attached = Lists(_editor.Document.Blocks).ToHashSet();
        foreach (var key in _states.Keys.Where(list => !attached.Contains(list)).ToArray()) _states.Remove(key);
        _lastState = Snapshot();
    }

    internal void NormalizeLegacy(bool resetHistory = true)
    {
        _restoring = true;
        try
        {
            // Snapshot first: a new list changes the traversal tree during normalization.
            foreach (var paragraph in Paragraphs(_editor.Document.Blocks).ToArray())
            {
                if (!Checkbox(paragraph, out _, out _)) continue;
                if (Ancestor<ListItem>(paragraph) is null)
                {
                    var blocks = ParentBlocks(paragraph);
                    if (blocks is null) continue;
                    double oldIndent = double.IsFinite(paragraph.Margin.Left) ? Math.Max(0, paragraph.Margin.Left) : 0;
                    var list = NewList(TextMarkerStyle.None);
                    blocks.InsertBefore(paragraph, list);
                    blocks.Remove(paragraph);
                    list.ListItems.Add(new ListItem(paragraph));
                    MergeAdjacent(list);
                    int levels = Math.Clamp((int)Math.Round(oldIndent / Math.Max(24, _indent())), 0, 8);
                    for (int level = 0; level < levels; level++) IndentItems([Ancestor<ListItem>(paragraph)!]);
                }
                else if (Ancestor<DocumentList>(paragraph) is { } list && list.MarkerStyle != TextMarkerStyle.None)
                {
                    SetStyle(Isolate([Ancestor<ListItem>(paragraph)!]), TextMarkerStyle.None, false);
                }
            }
            foreach (var list in Lists(_editor.Document.Blocks)) State(list);
            RefreshNumbering();
            RefreshLayout(all: true);
        }
        finally { _restoring = false; if (resetHistory) ResetHistory(); }
    }

    internal void LoadContent(Stream stream, string format)
    {
        Edit(() =>
        {
            _restoring = true;
            try
            {
                new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd).Load(stream, format);
                _states.Clear();
                if (format == DataFormats.XamlPackage) { stream.Position = 0; ReadMetadata(stream, false); }
                NormalizeLegacy(false);
            }
            finally { _restoring = false; }
        }, false);
    }

    internal void ReplaceContent(string text)
    {
        Edit(() =>
        {
            _restoring = true;
            try
            {
                new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd).Text = text;
                _states.Clear(); NormalizeLegacy(false);
            }
            finally { _restoring = false; }
        }, false);
    }

    private readonly record struct CheckboxMetricsKey(Typeface Typeface, double Size, double PixelsPerDip);

    private double AlignCheckbox(Paragraph paragraph, Dictionary<CheckboxMetricsKey, (double Unchecked, double Checked)> metrics)
    {
        if (!Checkbox(paragraph, out _, out var isChecked)) return 0;
        var prefixPosition = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var prefixRange = new TextRange(prefixPosition, AdvanceCharacters(paragraph.ContentStart, 1));
        var typography = MarkerTypography(paragraph);
        if ((double)prefixRange.GetPropertyValue(TextElement.FontSizeProperty) != typography.Size)
            prefixRange.ApplyPropertyValue(TextElement.FontSizeProperty, typography.Size);
        if (((FontFamily)prefixRange.GetPropertyValue(TextElement.FontFamilyProperty)).Source != typography.Family.Source)
            prefixRange.ApplyPropertyValue(TextElement.FontFamilyProperty, typography.Family);
        var typeface = new Typeface(typography.Family, (FontStyle)prefixRange.GetPropertyValue(TextElement.FontStyleProperty),
            (FontWeight)prefixRange.GetPropertyValue(TextElement.FontWeightProperty), paragraph.FontStretch);
        double Width(string text) => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, typography.Size, Brushes.Black, VisualTreeHelper.GetDpi(_editor).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var key = new CheckboxMetricsKey(typeface, typography.Size, VisualTreeHelper.GetDpi(_editor).PixelsPerDip);
        if (!metrics.TryGetValue(key, out var widths))
            metrics[key] = widths = (Width(UncheckedPrefix), Width(CheckedPrefix));
        // The list already reserves the text column. Hang the glyph into its marker area,
        // so bullets, numbers and checkbox text all start at the same column.
        paragraph.Margin = new Thickness(0, paragraph.Margin.Top, paragraph.Margin.Right, paragraph.Margin.Bottom);
        paragraph.TextIndent = -(isChecked ? widths.Checked : widths.Unchecked);
        // Reserve both states, so checking an item never shifts the shared text column.
        return Math.Max(widths.Unchecked, widths.Checked) + 2;
    }

    private static void RemovePrefix(Paragraph paragraph)
    {
        if (!Checkbox(paragraph, out var end, out _)) return;
        new TextRange(paragraph.ContentStart, end).Text = string.Empty;
        paragraph.Margin = new Thickness(0, paragraph.Margin.Top, paragraph.Margin.Right, paragraph.Margin.Bottom);
        paragraph.TextIndent = 0;
    }

    private void SetStyle(DocumentList list, TextMarkerStyle style, bool explicitStyle)
    {
        var oldStyle = list.MarkerStyle;
        list.MarkerStyle = style;
        _states[list] = State(list) with { ExplicitStyle = explicitStyle,
            ContinuesFrom = style == oldStyle ? State(list).ContinuesFrom : null, Checklist = style == TextMarkerStyle.None };
        foreach (ListItem item in list.ListItems.Cast<ListItem>().ToArray())
        {
            if (FirstParagraph(item) is not { } paragraph) continue;
            if (style == TextMarkerStyle.None)
            {
                if (!Checkbox(paragraph, out _, out _))
                    new TextRange(paragraph.ContentStart, paragraph.ContentStart).Text = UncheckedPrefix;
            }
            else RemovePrefix(paragraph);
        }
        RefreshListLayout(list);
    }

    private static (double Size, FontFamily Family) MarkerTypography(Paragraph paragraph)
    {
        var start = Checkbox(paragraph, out var textStart, out _) ? textStart : paragraph.ContentStart;
        start = start.GetInsertionPosition(LogicalDirection.Forward) ?? paragraph.ContentStart;
        if (start.CompareTo(paragraph.ContentEnd) > 0) start = paragraph.ContentEnd;
        var end = AdvanceCharacters(start, 1);
        if (end.CompareTo(paragraph.ContentEnd) > 0) end = paragraph.ContentEnd;
        var range = new TextRange(start, end);
        var size = range.GetPropertyValue(TextElement.FontSizeProperty);
        var family = range.GetPropertyValue(TextElement.FontFamilyProperty);
        return (size is double fontSize ? fontSize : paragraph.FontSize,
            family is FontFamily fontFamily ? fontFamily : paragraph.FontFamily);
    }

    private void RefreshListLayout(DocumentList list)
    {
        var paragraphs = list.ListItems.Cast<ListItem>().Select(FirstParagraph).OfType<Paragraph>().ToArray();
        // Marker typography belongs to the container; body text must retain its own
        // inherited defaults when that container changes or adjacent lists merge.
        foreach (ListItem item in list.ListItems)
        {
            if (item.ReadLocalValue(TextElement.FontSizeProperty) == DependencyProperty.UnsetValue)
                item.SetValue(TextElement.FontSizeProperty, item.FontSize);
            if (item.ReadLocalValue(TextElement.FontFamilyProperty) == DependencyProperty.UnsetValue)
                item.SetValue(TextElement.FontFamilyProperty, item.FontFamily);
        }
        double gutter = Math.Max(24, _indent());
        if (list.MarkerStyle != TextMarkerStyle.None && paragraphs.Length > 0)
        {
            // WPF draws native markers using List typography, not ListItem typography.
            var typography = MarkerTypography(paragraphs[0]);
            if (Math.Abs(list.FontSize - typography.Size) > .01 || list.FontFamily.Source != typography.Family.Source)
            {
                list.FontSize = typography.Size;
                list.FontFamily = typography.Family;
            }
            var lineHeight = double.IsNaN(list.LineHeight) ? list.FontFamily.LineSpacing * list.FontSize : list.LineHeight;
            var offset = double.IsNaN(list.MarkerOffset) ? lineHeight / 2 : Math.Max(0, list.MarkerOffset);
            double markerWidth = list.FontSize;
            if (Numbered(list.MarkerStyle))
            {
                var widths = new Dictionary<char, double>();
                var typeface = new Typeface(list.FontFamily, list.FontStyle, list.FontWeight, list.FontStretch);
                double Width(char c)
                {
                    if (!widths.TryGetValue(c, out var width))
                        widths[c] = width = new FormattedText(c.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            typeface, list.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(_editor).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    return width;
                }
                markerWidth = Enumerable.Range(0, list.ListItems.Count)
                    .Max(index => Marker(list.MarkerStyle, SafeStart(list.StartIndex, index)).TrimEnd().Sum(Width));
            }
            gutter = Math.Max(gutter, Math.Ceiling(markerWidth + offset + 2));
        }
        var metrics = new Dictionary<CheckboxMetricsKey, (double Unchecked, double Checked)>();
        foreach (var paragraph in paragraphs)
            gutter = Math.Max(gutter, AlignCheckbox(paragraph, metrics));
        list.Padding = new Thickness(Math.Ceiling(gutter), list.Padding.Top, list.Padding.Right, list.Padding.Bottom);
    }

    private bool SameLogicalList(DocumentList first, DocumentList second) => ReferenceEquals(first, second)
        || State(first).TypographyGroup is { } group && group == State(second).TypographyGroup;

    private void CoalesceTypographyGroups(HashSet<Guid>? onlyGroups = null)
    {
        if (!_states.Values.Any(state => state.TypographyGroup is not null)) return;
        var start = CaptureEndpoint(_editor.Selection.Start);
        var end = CaptureEndpoint(_editor.Selection.End);
        foreach (var list in Lists(_editor.Document.Blocks).ToArray())
        {
            if (list.Parent is null || State(list).TypographyGroup is not { } group
                || onlyGroups is not null && !onlyGroups.Contains(group)) continue;
            while (list.NextBlock is DocumentList next && State(next).TypographyGroup == group
                && list.MarkerStyle == next.MarkerStyle && list.Margin == next.Margin)
            {
                var removedId = State(next).Id;
                foreach (var item in next.ListItems.Cast<ListItem>().ToArray())
                { next.ListItems.Remove(item); list.ListItems.Add(item); }
                foreach (var follower in _states.Where(pair => pair.Value.ContinuesFrom == removedId).Select(pair => pair.Key).ToArray())
                    _states[follower] = State(follower) with { ContinuesFrom = State(list).Id };
                ParentBlocks(next)!.Remove(next);
                _states.Remove(next);
            }
            _states[list] = State(list) with { TypographyGroup = null };
        }
        RestoreSelection(start, end);
    }

    private void RefreshTypographyGroups(DocumentList list)
    {
        // WPF supports one marker font per List. Typography segments keep their own
        // fonts and share a gutter, while their metadata preserves one logical list.
        if (list.MarkerStyle == TextMarkerStyle.None) { RefreshListLayout(list); return; }
        List<List<ListItem>> groups = [];
        (double Size, FontFamily Family)? previous = null;
        foreach (ListItem item in list.ListItems)
        {
            var typography = FirstParagraph(item) is { } paragraph ? MarkerTypography(paragraph) : (list.FontSize, list.FontFamily);
            if (previous is null || Math.Abs(previous.Value.Size - typography.Item1) > .01
                || previous.Value.Family.Source != typography.Item2.Source) groups.Add([]);
            groups[^1].Add(item); previous = typography;
        }
        if (groups.Count <= 1) { RefreshListLayout(list); return; }

        var original = State(list);
        var followers = _states.Where(pair => !ReferenceEquals(pair.Key, list) && pair.Value.ContinuesFrom == original.Id)
            .Select(pair => pair.Key).ToArray();
        DocumentList? preceding = null;
        List<DocumentList> segments = [];
        foreach (var items in groups)
        {
            var segment = Isolate(items);
            _states[segment] = State(segment) with { TypographyGroup = original.Id,
                ContinuesFrom = Numbered(segment.MarkerStyle) && preceding is not null ? State(preceding).Id : State(segment).ContinuesFrom };
            RefreshListLayout(segment);
            segments.Add(segment);
            preceding = segment;
        }
        var gutter = segments.Max(segment => segment.Padding.Left);
        foreach (var segment in segments)
            segment.Padding = new Thickness(gutter, segment.Padding.Top, segment.Padding.Right, segment.Padding.Bottom);
        foreach (var follower in followers)
            _states[follower] = State(follower) with { ContinuesFrom = State(preceding!).Id };
    }

    /// <summary>Split the parent so operations affect only the selected contiguous items.</summary>
    private DocumentList Isolate(IReadOnlyList<ListItem> items)
    {
        var list = (DocumentList)items[0].Parent;
        var all = list.ListItems.Cast<ListItem>().ToArray();
        int first = Array.IndexOf(all, items[0]);
        int last = Array.IndexOf(all, items[^1]);
        if (first == 0 && last == all.Length - 1) return list;
        var blocks = ParentBlocks(list)!;
        var selected = NewList(list.MarkerStyle, SafeStart(list.StartIndex, first));
        _states[selected] = State(selected) with { ExplicitStyle = State(list).ExplicitStyle };
        if (first == 0)
            _states[selected] = State(selected) with { Id = State(list).Id, ContinuesFrom = State(list).ContinuesFrom,
                Checklist = State(list).Checklist };
        CopyListAppearance(list, selected);
        blocks.InsertAfter(list, selected);
        foreach (var item in all[first..(last + 1)]) { list.ListItems.Remove(item); selected.ListItems.Add(item); }
        if (last + 1 < all.Length)
        {
            var suffix = NewList(list.MarkerStyle, SafeStart(list.StartIndex, last + 1));
            CopyListAppearance(list, suffix);
            _states[suffix] = State(suffix) with { ExplicitStyle = State(list).ExplicitStyle,
                ContinuesFrom = Numbered(list.MarkerStyle) ? State(selected).Id : null };
            blocks.InsertAfter(selected, suffix);
            foreach (var item in all[(last + 1)..]) { list.ListItems.Remove(item); suffix.ListItems.Add(item); }
        }
        if (first > 0 && Numbered(list.MarkerStyle))
            _states[selected] = State(selected) with { ContinuesFrom = State(list).Id };
        if (list.ListItems.Count == 0)
        {
            _states[selected] = State(selected) with { Id = State(list).Id, ContinuesFrom = State(list).ContinuesFrom };
            blocks.Remove(list);
        }
        return selected;
    }

    private static int SafeStart(int start, int offset) => (int)Math.Clamp((long)start + offset, 1, int.MaxValue);
    private static void CopyListAppearance(DocumentList source, DocumentList target)
    {
        target.Margin = source.Margin; target.Padding = source.Padding;
        target.FontFamily = source.FontFamily; target.FontSize = source.FontSize; target.Foreground = source.Foreground;
        target.MarkerOffset = source.MarkerOffset;
    }

    private static IEnumerable<List<ListItem>> Groups(IEnumerable<ListItem> items)
    {
        foreach (var parentGroup in items.Distinct().GroupBy(item => item.Parent).ToArray())
        {
            if (parentGroup.Key is not DocumentList list) continue;
            var selected = parentGroup.ToHashSet();
            List<ListItem> run = [];
            foreach (ListItem item in list.ListItems)
            {
                if (selected.Contains(item)) run.Add(item);
                else if (run.Count > 0) { yield return run; run = []; }
            }
            if (run.Count > 0) yield return run;
        }
    }

    internal void ApplyStyle(TextMarkerStyle style, bool toggle = false, bool explicitStyle = true)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        var paragraphs = Targets();
        if (paragraphs.Count == 0)
        {
            if (!_editor.Selection.IsEmpty || _editor.Document.Blocks.Count != 0) return;
            // A cleared document may have no paragraph at all. Create the first
            // item in the same undo unit as its marker, without requiring typing.
            Edit(() =>
            {
                var paragraph = new Paragraph();
                _editor.Document.Blocks.Add(paragraph);
                _editor.CaretPosition = paragraph.ContentStart;
                ApplyStyle(style, toggle, explicitStyle);
            }, preserveSelection: false);
            return;
        }
        if (toggle && paragraphs.All(p => Ancestor<DocumentList>(p) is { } list &&
            (style == TextMarkerStyle.None ? IsChecklist(list) : Numbered(style)
                ? Numbered(list.MarkerStyle) : list.MarkerStyle is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square)))
        { Remove(); return; }
        Edit(() =>
        {
            var items = paragraphs.Select(p => Ancestor<ListItem>(p)).OfType<ListItem>().Distinct().ToArray();
            foreach (var group in Groups(items).ToArray()) SetStyle(Isolate(group), style, explicitStyle);
            foreach (var paragraph in paragraphs.Where(p => Ancestor<ListItem>(p) is null).ToArray())
            {
                var blocks = ParentBlocks(paragraph);
                if (blocks is null) continue;
                var list = NewList(style);
                double left = double.IsFinite(paragraph.Margin.Left) ? Math.Max(0, paragraph.Margin.Left) : 0;
                list.Margin = new Thickness(left, 0, 0, 0);
                paragraph.Margin = new Thickness(0, paragraph.Margin.Top, paragraph.Margin.Right, paragraph.Margin.Bottom);
                blocks.InsertBefore(paragraph, list); blocks.Remove(paragraph);
                list.ListItems.Add(new ListItem(paragraph));
                SetStyle(list, style, explicitStyle);
                MergeAdjacent(list);
            }
        });
    }

    private void MergeAdjacent(DocumentList list)
    {
        if (list.Parent is null) return;
        if (list.PreviousBlock is DocumentList previous && previous.MarkerStyle == list.MarkerStyle
            && NormalizeMargin(previous.Margin.Left) == NormalizeMargin(list.Margin.Left)
            && (!Numbered(list.MarkerStyle) || State(list).ContinuesFrom is null && list.StartIndex == 1))
        {
            foreach (var item in list.ListItems.Cast<ListItem>().ToArray())
            { list.ListItems.Remove(item); previous.ListItems.Add(item); }
            ParentBlocks(list)!.Remove(list); list = previous;
        }
        if (list.NextBlock is DocumentList next && next.MarkerStyle == list.MarkerStyle
            && NormalizeMargin(next.Margin.Left) == NormalizeMargin(list.Margin.Left)
            && (!Numbered(next.MarkerStyle) || State(next).ContinuesFrom is null && next.StartIndex == 1))
        {
            foreach (var item in next.ListItems.Cast<ListItem>().ToArray())
            { next.ListItems.Remove(item); list.ListItems.Add(item); }
            ParentBlocks(next)!.Remove(next);
        }
    }

    private static double NormalizeMargin(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    internal void Remove()
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        var paragraphs = Targets();
        Edit(() =>
        {
            foreach (var group in Groups(paragraphs.Select(p => Ancestor<ListItem>(p)).OfType<ListItem>()).ToArray())
                Unwrap(Isolate(group));
            foreach (var paragraph in paragraphs) RemovePrefix(paragraph);
        });
    }

    internal void Mutate(Action action) => Edit(() => { action(); RepairChecklists(); }, false);

    internal void ReplaceSelection(string text) => Mutate(() => InsertText(text));
    private void InsertText(string text)
    {
        _editor.Selection.Text = text;
        _editor.CaretPosition = _editor.Selection.End;
    }

    internal void PasteText(string text, Action? afterPaste = null)
    {
        Mutate(() =>
        {
            if (Ancestor<ListItem>(_editor.Selection.Start.Parent) is null)
                InsertText(text);
            else
            {
                string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                // Copying an item often includes its paragraph terminator; do not paste an extra empty item.
                if (normalized.EndsWith('\n')) normalized = normalized[..^1];
                var lines = normalized.Split('\n');
                var destination = Ancestor<DocumentList>(_editor.Selection.Start.Parent)!;
                var parsed = ParsePastedItems(lines, Numbered(destination.MarkerStyle));
                int baseDepth = Depth(destination);
                bool replaceFirstState = _editor.Selection.Start.Paragraph is { } first && AtTextStart(first);
                for (int index = 0; index < lines.Length; index++)
                {
                    if (index > 0) PasteItemBreak();
                    if (parsed is { } items)
                    {
                        int targetDepth = Math.Min(8, baseDepth + items[index].Depth);
                        for (int attempt = 0; attempt < 8; attempt++)
                        {
                            var active = Ancestor<DocumentList>(_editor.CaretPosition.Parent);
                            var item = Ancestor<ListItem>(_editor.CaretPosition.Parent);
                            if (active is null || item is null || Depth(active) == targetDepth) break;
                            var caret = CaptureEndpoint(_editor.CaretPosition);
                            if (Depth(active) < targetDepth) IndentItems([item]);
                            else OutdentItems([item]);
                            // WPF moves its selection when an item changes parent. Keep the
                            // paste attached to that item's text rather than its former owner.
                            if (RestoreEndpoint(caret) is { } position) _editor.CaretPosition = position;
                        }
                        InsertText(items[index].Text);
                        if ((index > 0 || replaceFirstState) && items[index].Checked is { } done
                            && _editor.CaretPosition.Paragraph is { } paragraph && Checkbox(paragraph, out var end, out _))
                            new TextRange(paragraph.ContentStart, end).Text = done ? CheckedPrefix : UncheckedPrefix;
                    }
                    else InsertText(lines[index]);
                }
            }
            afterPaste?.Invoke();
        });
    }

    // A pasted blank line is an item, not the keyboard gesture that ends a list.
    private void PasteItemBreak()
    {
        bool checklist = _editor.CaretPosition.Paragraph is { } current && Checkbox(current, out _, out _);
        EditingCommands.EnterParagraphBreak.Execute(null, _editor);
        if (checklist && _editor.CaretPosition.Paragraph is { } paragraph)
        {
            if (!Checkbox(paragraph, out _, out _))
                new TextRange(paragraph.ContentStart, paragraph.ContentStart).Text = UncheckedPrefix;
            else new TextRange(paragraph.ContentStart, AdvanceCharacters(paragraph.ContentStart, 2)).Text = UncheckedPrefix;
            _editor.CaretPosition = AdvanceCharacters(paragraph.ContentStart, 2);
        }
    }

    private sealed record PastedItem(string Text, int Depth, bool? Checked);
    private static readonly Regex PastedMarker = new(
        @"^(?<indent>[ \t]*)(?<marker>[•◦○▪■●*\-]|☐|☑|\[[ xX]\]|\[\]|(?:\d+|[IVXLCDMivxlcdm]+|[a-zA-Z]{1,3})[.)]|\((?:\d+|[IVXLCDMivxlcdm]+|[a-zA-Z]{1,3})\))(?<gap>[ \t]+|$)(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static PastedItem[]? ParsePastedItems(string[] lines, bool numberedDestination)
    {
        var matches = lines.Select(line => PastedMarker.Match(line)).ToArray();
        var nonblank = Enumerable.Range(0, lines.Length).Where(i => !string.IsNullOrWhiteSpace(lines[i])).ToArray();
        if (nonblank.Length == 0 || nonblank.Any(i => !matches[i].Success)) return null;
        if (nonblank.Length == 1 && !numberedDestination
            && char.IsLetterOrDigit(matches[nonblank[0]].Groups["marker"].Value.TrimStart('(')[0])) return null;
        int baseline = nonblank.Min(i => IndentColumns(matches[i].Groups["indent"].Value));
        var levels = new List<int> { baseline };
        var result = new PastedItem[lines.Length];
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!matches[i].Success) { result[i] = new(string.Empty, depth, null); continue; }
            int columns = IndentColumns(matches[i].Groups["indent"].Value);
            while (levels.Count > 1 && columns < levels[^1]) levels.RemoveAt(levels.Count - 1);
            if (columns > levels[^1]) levels.Add(columns);
            depth = levels.Count - 1;
            string marker = matches[i].Groups["marker"].Value;
            bool? done = marker switch { "☑" or "[x]" or "[X]" => true, "☐" or "[]" or "[ ]" => false, _ => null };
            result[i] = new(matches[i].Groups["text"].Value, depth, done);
        }
        return result;
    }

    private static int IndentColumns(string indent)
    {
        int columns = 0;
        foreach (char character in indent) columns += character == '\t' ? 4 - columns % 4 : 1;
        return columns;
    }

    private void RepairChecklists()
    {
        foreach (var list in Lists(_editor.Document.Blocks).Where(list => list.MarkerStyle == TextMarkerStyle.None).ToArray())
        {
            if (!State(list).Checklist && !IsChecklist(list)) continue;
            foreach (var item in list.ListItems.Cast<ListItem>().ToArray())
            {
                if (FirstParagraph(item) is not { } p) continue;
                if (!Checkbox(p, out _, out _))
                {
                    var text = InlineText(p);
                    if (text.StartsWith('☐') || text.StartsWith('☑'))
                        new TextRange(AdvanceCharacters(p.ContentStart, 1), AdvanceCharacters(p.ContentStart, 1)).Text = " ";
                    else new TextRange(p.ContentStart, p.ContentStart).Text = UncheckedPrefix;
                }
            }
        }
    }

    internal void RefreshLayout(bool all = false)
    {
        var start = CaptureEndpoint(_editor.Selection.Start);
        var end = CaptureEndpoint(_editor.Selection.End);
        var targets = all ? [] : Targets().ToArray();
        var groups = targets.Select(p => Ancestor<DocumentList>(p)).OfType<DocumentList>()
            .Select(list => State(list).TypographyGroup).OfType<Guid>().ToHashSet();
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            CoalesceTypographyGroups(all ? null : groups);
            var lists = all ? Lists(_editor.Document.Blocks).ToArray()
                : targets.Select(p => Ancestor<DocumentList>(p)).OfType<DocumentList>().Distinct().ToArray();
            foreach (var list in lists) if (list.Parent is not null) RefreshTypographyGroups(list);
            RefreshNumbering();
        }
        finally
        {
            try { RestoreSelection(start, end); }
            finally { _restoring = wasRestoring; }
        }
    }

    private bool NeedsMarkerLayout(Paragraph? paragraph)
    {
        if (paragraph is null || Ancestor<DocumentList>(paragraph) is not { } list) return false;
        var typography = MarkerTypography(paragraph);
        if (Checkbox(paragraph, out _, out _))
        {
            var prefix = new TextRange(paragraph.ContentStart, AdvanceCharacters(paragraph.ContentStart, 1));
            return !Equals(prefix.GetPropertyValue(TextElement.FontSizeProperty), typography.Size)
                || prefix.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily family && family.Source != typography.Family.Source;
        }
        return list.MarkerStyle != TextMarkerStyle.None && (Math.Abs(list.FontSize - typography.Size) > .01
            || list.FontFamily.Source != typography.Family.Source);
    }

    internal void ChangeIndentStep(double previousStep)
    {
        Edit(() =>
        {
            foreach (var list in Lists(_editor.Document.Blocks).ToArray())
            {
                double left = double.IsFinite(list.Margin.Left) ? Math.Max(0, list.Margin.Left) : 0;
                double levels = Math.Round(left / Math.Max(24, previousStep));
                list.Margin = new Thickness(levels * Math.Max(24, _indent()), list.Margin.Top, list.Margin.Right, list.Margin.Bottom);
            }
            RefreshLayout(true);
        });
    }

    private TextMarkerStyle NestedStyle(DocumentList list, int depth) => IsChecklist(list) ? TextMarkerStyle.None
        : Numbered(list.MarkerStyle)
            ? new[] { TextMarkerStyle.Decimal, TextMarkerStyle.LowerLatin, TextMarkerStyle.LowerRoman }[depth % 3]
            : new[] { TextMarkerStyle.Disc, TextMarkerStyle.Circle, TextMarkerStyle.Square }[depth % 3];

    private void Unwrap(DocumentList list)
    {
        var blocks = ParentBlocks(list);
        if (blocks is null) return;
        Block cursor = list;
        foreach (ListItem item in list.ListItems.Cast<ListItem>().ToArray())
        {
            foreach (var block in item.Blocks.Cast<Block>().ToArray())
            {
                item.Blocks.Remove(block);
                if (block is Paragraph p) RemovePrefix(p);
                if (block is DocumentList child)
                    child.Margin = new Thickness(Math.Max(24, _indent()) + Math.Max(0, child.Margin.Left),
                        child.Margin.Top, child.Margin.Right, child.Margin.Bottom);
                blocks.InsertAfter(cursor, block); cursor = block;
            }
            list.ListItems.Remove(item);
        }
        blocks.Remove(list);
    }

    private List<ListItem> TargetItems(bool rootsOnly)
    {
        var items = Targets().Select(p => Ancestor<ListItem>(p)).OfType<ListItem>().Distinct().ToList();
        if (rootsOnly) items.RemoveAll(item => items.Any(other => !ReferenceEquals(item, other)
            && Ancestor<ListItem>((item.Parent as DocumentList)?.Parent) is { } parent && IsDescendant(parent, other)));
        return items;
    }

    private static bool IsDescendant(ListItem item, ListItem ancestor)
    {
        for (ListItem? cursor = item; cursor is not null; cursor = Ancestor<ListItem>((cursor.Parent as DocumentList)?.Parent))
            if (ReferenceEquals(cursor, ancestor)) return true;
        return false;
    }

    internal void Indent(bool decrease)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        var items = TargetItems(true);
        if (items.Count == 0) return;
        Edit(() =>
        {
            if (decrease) foreach (var group in Groups(items).ToArray()) OutdentItems(group);
            else IndentItems(items);
        });
    }

    private void IndentItems(IReadOnlyList<ListItem> items)
    {
        foreach (var group in Groups(items).ToArray())
        {
            var list = (DocumentList)group[0].Parent;
            var previous = group[0].PreviousListItem;
            if (Depth(list) >= 8) continue;
            if (previous is null)
            {
                int originalStart = list.StartIndex;
                var isolated = Isolate(group);
                var suffix = isolated.NextBlock as DocumentList;
                double left = double.IsFinite(isolated.Margin.Left) ? Math.Max(0, isolated.Margin.Left) : 0;
                isolated.Margin = new Thickness(left + Math.Max(24, _indent()), isolated.Margin.Top, isolated.Margin.Right, isolated.Margin.Bottom);
                if (!State(isolated).ExplicitStyle) SetStyle(isolated, NestedStyle(isolated, Depth(isolated)), false);
                if (suffix is not null && Numbered(suffix.MarkerStyle)
                    && State(suffix).ContinuesFrom == State(isolated).Id) suffix.StartIndex = originalStart;
                continue;
            }
            var style = list.MarkerStyle;
            if (!State(list).ExplicitStyle)
                style = NestedStyle(list, Depth(list) + 1);
            var nested = previous.Blocks.LastBlock as DocumentList;
            if (nested is null || nested.MarkerStyle != style)
            {
                nested = NewList(style);
                _states[nested] = State(nested) with { ExplicitStyle = State(list).ExplicitStyle };
                previous.Blocks.Add(nested);
            }
            foreach (var item in group) { list.ListItems.Remove(item); nested.ListItems.Add(item); }
            if (list.ListItems.Count == 0) ParentBlocks(list)!.Remove(list);
        }
    }

    private void OutdentItems(IReadOnlyList<ListItem> items)
    {
        var list = (DocumentList)items[0].Parent;
        if (double.IsFinite(list.Margin.Left) && list.Margin.Left > .1)
        {
            var isolated = Isolate(items);
            isolated.Margin = new Thickness(Math.Max(0, isolated.Margin.Left - Math.Max(24, _indent())),
                isolated.Margin.Top, isolated.Margin.Right, isolated.Margin.Bottom);
            if (!State(isolated).ExplicitStyle) SetStyle(isolated, NestedStyle(isolated, Depth(isolated)), false);
            MergeAdjacent(isolated);
            return;
        }
        if (list.Parent is not ListItem owner || owner.Parent is not DocumentList outer)
        { Unwrap(Isolate(items)); return; }
        // Following unselected siblings become children of the last promoted item.
        var following = new List<ListItem>();
        for (var next = items[^1].NextListItem; next is not null; next = next.NextListItem) following.Add(next);
        if (following.Count > 0)
        {
            var tail = NewList(list.MarkerStyle, SafeStart(list.StartIndex, list.ListItems.Cast<ListItem>().ToList().IndexOf(following[0])));
            foreach (var item in following) { list.ListItems.Remove(item); tail.ListItems.Add(item); }
            items[^1].Blocks.Add(tail);
        }
        ListItem cursor = owner;
        foreach (var item in items) { list.ListItems.Remove(item); outer.ListItems.InsertAfter(cursor, item); cursor = item; }
        if (list.ListItems.Count == 0) owner.Blocks.Remove(list);
        if (outer.MarkerStyle != TextMarkerStyle.None)
        {
            foreach (var item in items) if (FirstParagraph(item) is { } p) RemovePrefix(p);
        }
        else foreach (var item in items) if (FirstParagraph(item) is { } p)
        {
            if (!Checkbox(p, out _, out _)) new TextRange(p.ContentStart, p.ContentStart).Text = UncheckedPrefix;
        }
    }

    internal bool Move(bool down)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        if (!_editing && _states.Values.Any(state => state.TypographyGroup is not null))
        {
            bool moved = false;
            Edit(() => moved = Move(down), preserveSelection: false);
            return moved;
        }
        var groups = Groups(TargetItems(true)).ToArray();
        if (groups.Length != 1) return false;
        var items = groups[0];
        var list = (DocumentList)items[0].Parent;
        var neighbor = down ? items[^1].NextListItem : items[0].PreviousListItem;
        if (neighbor is null) return true;
        Edit(() =>
        {
            foreach (var item in items) list.ListItems.Remove(item);
            if (down)
            {
                var cursor = neighbor;
                foreach (var item in items) { list.ListItems.InsertAfter(cursor, item); cursor = item; }
            }
            else foreach (var item in items) list.ListItems.InsertBefore(neighbor, item);
            var first = FirstParagraph(items[0]);
            if (first is not null) _editor.Selection.Select(first.ContentStart, items[^1].ContentEnd);
        }, preserveSelection: false);
        return true;
    }

    internal bool ToggleCheckbox(Paragraph? paragraph = null)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        paragraph ??= _editor.CaretPosition.Paragraph;
        if (paragraph is null || !Checkbox(paragraph, out var end, out bool done)) return false;
        Edit(() => new TextRange(paragraph.ContentStart, end).Text = done ? UncheckedPrefix : CheckedPrefix);
        return true;
    }

    internal bool ToggleAt(Point point)
    {
        // Hanging markers sit outside the paragraph's text hit region. Snap to its
        // nearest position, then restrict the action to the actual glyph rectangle.
        var pointer = _editor.GetPositionFromPoint(point, true);
        var paragraph = pointer?.Paragraph;
        if (paragraph is null || !Checkbox(paragraph, out _, out _)) return false;
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var end = AdvanceCharacters(paragraph.ContentStart, 1);
        var left = start.GetCharacterRect(LogicalDirection.Forward);
        var right = end.GetCharacterRect(LogicalDirection.Backward);
        var bounds = Rect.Union(left, right);
        bounds.Inflate(2, 2);
        return bounds.Contains(point) && ToggleCheckbox(paragraph);
    }

    internal bool AutoStart(string input)
    {
        if (input != " ") return false;
        if (!_editing) WpfEditorInput.Flush(_editor);
        if (!_editor.Selection.IsEmpty || _editor.CaretPosition.Paragraph is not { } paragraph
            || Ancestor<ListItem>(paragraph) is not null) return false;
        string before = RawText(paragraph.ContentStart, _editor.CaretPosition);
        TextMarkerStyle style;
        switch (before)
        {
            case "*": case "-": style = TextMarkerStyle.Disc; break;
            case "1.": style = TextMarkerStyle.Decimal; break;
            case "[]": case "[ ]": style = TextMarkerStyle.None; break;
            default: return false;
        }
        var triggerPosition = _editor.CaretPosition;
        new TextRange(triggerPosition, triggerPosition).Text = " ";
        Edit(() =>
        {
            // Undo the conversion independently from the user's literal triggering space.
            var prefixEnd = AdvanceCharacters(paragraph.ContentStart, before.Length + 1);
            new TextRange(paragraph.ContentStart, prefixEnd).Text = string.Empty;
            _editor.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
            ApplyStyle(style, explicitStyle: false);
            var caret = Checkbox(paragraph, out var textStart, out _) ? textStart : paragraph.ContentStart;
            _editor.Selection.Select(caret, caret);
        }, preserveSelection: false);
        return true;
    }

    internal static string RawText(TextPointer start, TextPointer end)
    {
        var builder = new StringBuilder();
        var pointer = start;
        while (pointer.CompareTo(end) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int count = Math.Min(pointer.GetTextInRun(LogicalDirection.Forward).Length, pointer.GetOffsetToPosition(end));
                builder.Append(pointer.GetTextInRun(LogicalDirection.Forward).AsSpan(0, count));
                pointer = pointer.GetPositionAtOffset(count)!;
            }
            else
            {
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (next is null) break;
                pointer = next;
            }
        }
        return builder.ToString();
    }

    private bool AtTextStart(Paragraph paragraph)
    {
        var start = Checkbox(paragraph, out var textStart, out _) ? textStart : paragraph.ContentStart;
        return string.IsNullOrEmpty(RawText(start, _editor.CaretPosition));
    }

    internal bool HandleKey(Key key, ModifierKeys modifiers, bool inTable)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.L)
        { ApplyStyle(TextMarkerStyle.Disc, toggle: true, explicitStyle: false); return true; }
        if (modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M && HasList)
        { Indent(modifiers.HasFlag(ModifierKeys.Shift)); return true; }
        if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && key is Key.Up or Key.Down)
            return Move(key == Key.Down);
        var paragraph = _editor.CaretPosition.Paragraph;
        var item = paragraph is null ? null : Ancestor<ListItem>(paragraph);
        if (item is null || paragraph is null) return false;
        if (key == Key.Tab && !inTable && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (!_editor.Selection.IsEmpty && TargetItems(false).Count > 1 || _editor.Selection.IsEmpty && AtTextStart(paragraph))
            { Indent(modifiers.HasFlag(ModifierKeys.Shift)); return true; }
            return false;
        }
        if (key == Key.Home && modifiers == ModifierKeys.None && Checkbox(paragraph, out var checkboxText, out _))
        {
            var firstLine = checkboxText.GetCharacterRect(LogicalDirection.Forward);
            var caretLine = _editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            if (Math.Abs(firstLine.Top - caretLine.Top) < 1)
            { _editor.Selection.Select(checkboxText, checkboxText); return true; }
            return false;
        }
        if (key == Key.Left && modifiers == ModifierKeys.None && _editor.Selection.IsEmpty
            && Checkbox(paragraph, out _, out _) && AtTextStart(paragraph))
        {
            var previous = paragraph.ElementStart.GetNextInsertionPosition(LogicalDirection.Backward);
            if (previous is not null) _editor.CaretPosition = previous;
            return true;
        }
        if (key == Key.Back && modifiers == ModifierKeys.None && _editor.Selection.IsEmpty && AtTextStart(paragraph))
        { Edit(() => OutdentItems([item])); return true; }
        if (key == Key.Return && modifiers == ModifierKeys.Shift && _editor.Selection.IsEmpty
            && Checkbox(paragraph, out var bodyStart, out _) && _editor.CaretPosition.CompareTo(bodyStart) < 0)
            _editor.CaretPosition = bodyStart;
        if (key == Key.Return && modifiers == ModifierKeys.None)
        { Enter(); return true; }
        // Native deletion knows how to merge the rich tree. Keep renumbering in its undo unit.
        if (key is Key.Back or Key.Delete && modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            Mutate(() =>
            {
                if (Checkbox(paragraph, out var contentStart, out _) && _editor.Selection.IsEmpty
                    && _editor.CaretPosition.CompareTo(contentStart) < 0) _editor.CaretPosition = contentStart;
                var command = modifiers == ModifierKeys.Control
                    ? key == Key.Back ? EditingCommands.DeletePreviousWord : EditingCommands.DeleteNextWord
                    : key == Key.Back ? EditingCommands.Backspace : EditingCommands.Delete;
                command.Execute(null, _editor);
            });
            return true;
        }
        return false;
    }

    internal void Enter()
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        var paragraph = _editor.CaretPosition.Paragraph;
        var item = paragraph is null ? null : Ancestor<ListItem>(paragraph);
        if (paragraph is null || item is null) return;
        Edit(() =>
        {
            bool checklist = Checkbox(paragraph, out var textStart, out _);
            string ownText = InlineText(paragraph);
            if (_editor.Selection.IsEmpty && string.IsNullOrWhiteSpace(checklist ? ownText[2..] : ownText)
                && item.Blocks.Count == 1)
            {
                OutdentItems([item]);
                _editor.CaretPosition = (Checkbox(paragraph, out var remainingStart, out _) ? remainingStart : paragraph.ContentStart)
                    .GetInsertionPosition(LogicalDirection.Forward);
                return;
            }
            if (!_editor.Selection.IsEmpty) _editor.Selection.Text = string.Empty;
            if (checklist && _editor.CaretPosition.CompareTo(textStart) < 0) _editor.CaretPosition = textStart;
            // Move actual inlines rather than flattening the selected item's rich text.
            // WPF's paragraph-break command handles run splitting, links and inline images.
            EditingCommands.EnterParagraphBreak.Execute(null, _editor);
            var nextParagraph = _editor.CaretPosition.Paragraph;
            if (nextParagraph is null || ReferenceEquals(nextParagraph, paragraph)) return;
            if (checklist)
            {
                if (!Checkbox(nextParagraph, out _, out _))
                    new TextRange(nextParagraph.ContentStart, nextParagraph.ContentStart).Text = UncheckedPrefix;
                else new TextRange(nextParagraph.ContentStart, AdvanceCharacters(nextParagraph.ContentStart, 2)).Text = UncheckedPrefix;
                _editor.CaretPosition = AdvanceCharacters(nextParagraph.ContentStart, 2);
            }
        }, preserveSelection: false);
    }

    private ListItem? NumberingTarget()
    {
        var paragraphs = Targets();
        if (paragraphs.Count == 0) return null;
        var item = Ancestor<ListItem>(paragraphs[0]);
        if (item?.Parent is not DocumentList list || !Numbered(list.MarkerStyle)
            || paragraphs.Any(p => Ancestor<DocumentList>(p) is not { } other || !SameLogicalList(list, other))) return null;
        return item;
    }

    internal bool CanSetNumbering => NumberingTarget() is not null;
    internal int? CurrentNumber
    {
        get
        {
            if (NumberingTarget() is not { Parent: DocumentList list } item) return null;
            int index = list.ListItems.Cast<ListItem>().TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count();
            return SafeStart(list.StartIndex, index);
        }
    }
    internal bool CanRestartNumbering => NumberingTarget() is { Parent: DocumentList list }
        && (CurrentNumber != 1 || State(list).ContinuesFrom is not null);
    internal bool CanContinue => NumberingTarget() is { Parent: DocumentList list } item
        && ContinuationCandidate() is { } candidate
        && (long)candidate.StartIndex + candidate.ListItems.Count <= int.MaxValue
        && (!ReferenceEquals(item, list.ListItems.FirstListItem) || State(list).ContinuesFrom != State(candidate).Id);
    private DocumentList? ActiveNumberedList() => NumberingTarget()?.Parent as DocumentList;
    private DocumentList? ContinuationCandidate()
    {
        if (ActiveNumberedList() is not { } active) return null;
        return Lists(_editor.Document.Blocks).TakeWhile(list => !ReferenceEquals(list, active)).LastOrDefault(list =>
            Numbered(list.MarkerStyle) && list.MarkerStyle == active.MarkerStyle && Depth(list) == Depth(active)
            && !SameLogicalList(list, active)
            && ReferenceEquals(Container(list), Container(active)));
    }
    private static DependencyObject? Container(DocumentList list)
        => Ancestor<TableCell>(list) ?? (DependencyObject?)Ancestor<Section>(list) ?? Ancestor<FlowDocument>(list);

    internal void SetNumbering(int? start)
    {
        if (!_editing) WpfEditorInput.Flush(_editor);
        if (!_editing && _states.Values.Any(state => state.TypographyGroup is not null))
        { Edit(() => SetNumbering(start)); return; }
        if (NumberingTarget() is not { Parent: DocumentList list } item) return;
        if (start is null && !CanContinue || start == 1 && !CanRestartNumbering) return;
        var candidate = start is null ? ContinuationCandidate() : null;
        if (start is null && candidate is null) return;
        if (start is { } requestedValue && requestedValue == CurrentNumber && State(list).ContinuesFrom is null) return;
        Edit(() =>
        {
            var tail = new List<ListItem>();
            for (var cursor = item; cursor is not null; cursor = cursor.NextListItem) tail.Add(cursor);
            var isolated = Isolate(tail);
            _states[isolated] = State(isolated) with { ContinuesFrom = candidate is null ? null : State(candidate).Id };
            int value = start ?? SafeStart(candidate!.StartIndex, candidate.ListItems.Count);
            // A link can change while its displayed number stays the same. Give that change
            // a native undo unit so the metadata snapshot follows Undo/Redo as well.
            if (isolated.StartIndex == value) isolated.StartIndex = value == int.MaxValue ? value - 1 : value + 1;
            isolated.StartIndex = value;
        });
    }

    internal void RefreshNumbering()
    {
        var preceding = new Dictionary<Guid, DocumentList>();
        foreach (var list in Lists(_editor.Document.Blocks).ToArray())
        {
            var state = State(list);
            if (state.ContinuesFrom is { } sourceId)
            {
                if (Numbered(list.MarkerStyle) && preceding.TryGetValue(sourceId, out var source)
                    && source.MarkerStyle == list.MarkerStyle && Depth(source) == Depth(list)
                    && ReferenceEquals(Container(source), Container(list)))
                {
                    int value = SafeStart(source.StartIndex, source.ListItems.Count);
                    if (list.StartIndex != value) list.StartIndex = value;
                }
                else _states[list] = state with { ContinuesFrom = null };
            }
            preceding[state.Id] = list;
        }
        var attached = preceding.Values.ToHashSet();
        foreach (var list in _states.Keys.Where(list => !attached.Contains(list)).ToArray()) _states.Remove(list);
    }

    internal void WriteMetadata(Stream stream)
    {
        var entries = Snapshot();
        stream.Position = 0;
        using var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite);
        if (package.PartExists(MetadataUri)) package.DeletePart(MetadataUri);
        using var part = package.CreatePart(MetadataUri, "application/json").GetStream();
        JsonSerializer.Serialize(part, new Metadata(1, entries));
    }

    internal void ReadMetadata(Stream stream, bool resetHistory = true)
    {
        _states.Clear();
        try
        {
            stream.Position = 0;
            using var package = Package.Open(stream, FileMode.Open, FileAccess.Read);
            if (package.PartExists(MetadataUri))
            {
                using var part = package.GetPart(MetadataUri).GetStream();
                var metadata = JsonSerializer.Deserialize<Metadata>(part);
                var lists = Lists(_editor.Document.Blocks).ToArray();
                var ids = new HashSet<Guid>();
                var ordinals = new HashSet<int>();
                if (metadata is { Version: 1, Lists: not null })
                    foreach (var entry in metadata.Lists)
                        if (entry is not null && entry.Ordinal >= 0 && entry.Ordinal < lists.Length
                            && entry.Id != Guid.Empty && ids.Add(entry.Id) && ordinals.Add(entry.Ordinal))
                            _states[lists[entry.Ordinal]] = new(entry.Id, entry.ContinuesFromId, entry.ExplicitStyle, entry.Checklist, entry.TypographyGroup);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException)
        { _states.Clear(); }
        if (resetHistory) ResetHistory();
    }

    internal static string Marker(TextMarkerStyle style, int number) => style switch
    {
        TextMarkerStyle.None => string.Empty,
        TextMarkerStyle.Disc => "• ", TextMarkerStyle.Circle => "○ ", TextMarkerStyle.Square => "■ ",
        TextMarkerStyle.LowerLatin => Letters(number).ToLowerInvariant() + ". ",
        TextMarkerStyle.UpperLatin => Letters(number) + ". ",
        TextMarkerStyle.LowerRoman => Roman(number).ToLowerInvariant() + ". ",
        TextMarkerStyle.UpperRoman => Roman(number) + ". ",
        _ => number.ToString(CultureInfo.InvariantCulture) + ". "
    };
    private static string Letters(int number)
    {
        var result = string.Empty;
        for (long value = number; value > 0; value = (value - 1) / 26)
            result = (char)('A' + (value - 1) % 26) + result;
        return result;
    }
    private static string Roman(int number)
    {
        if (number < 1 || number > 3999) return number.ToString(CultureInfo.InvariantCulture);
        var result = new StringBuilder();
        int[] values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        for (int i = 0; i < values.Length; i++) while (number >= values[i]) { result.Append(symbols[i]); number -= values[i]; }
        return result.ToString();
    }
}
