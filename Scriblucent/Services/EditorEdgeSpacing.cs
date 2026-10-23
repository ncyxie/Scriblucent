using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scriblucent.Services;

/// <summary>
/// Keeps the editor's top and bottom breathing room inside the scrolling flow without
/// adding selectable document blocks. Only the two edge blocks need to be inspected.
/// </summary>
internal sealed class EditorEdgeSpacing
{
    internal const double PaddingDip = 12;

    private sealed class AppliedPadding
    {
        internal required Thickness Original { get; init; }
        internal required object OriginalLocal { get; init; }
        internal required Thickness Applied { get; init; }
        internal required bool Top { get; init; }
        internal required bool Bottom { get; init; }
        internal Block? Previous { get; set; }
        internal Block? Next { get; set; }
    }

    private readonly Dictionary<Block, AppliedPadding> _applied = new();
    private RichTextBox? _editor;
    private Thickness _baseEditorPadding;

    internal bool UpdateEditor(RichTextBox editor, bool useEmptyViewportPadding = true)
    {
        if (!ReferenceEquals(_editor, editor))
        {
            _editor = editor;
            _baseEditorPadding = editor.Padding;
        }

        var emptyViewport = useEmptyViewportPadding && IsEmpty(editor.Document);
        if (emptyViewport)
            Suspend();
        else
            Update(editor.Document);

        // WPF can paint the caret of an empty paragraph before its block padding has a
        // visible text line. The viewport inset places that caret where the first glyph
        // will appear; once text exists, block padding takes over and scrolls with it.
        var desired = emptyViewport
            ? new Thickness(_baseEditorPadding.Left,
                _baseEditorPadding.Top + PaddingDip,
                _baseEditorPadding.Right,
                _baseEditorPadding.Bottom + PaddingDip)
            : _baseEditorPadding;
        if (editor.Padding == desired)
            return false;

        editor.Padding = desired;
        return true;
    }

    internal void Update(FlowDocument document)
    {
        var first = ContentEdge(document.Blocks.FirstBlock, forward: true);
        var last = ContentEdge(document.Blocks.LastBlock, forward: false);

        // WPF copies the source paragraph's local Padding when Enter or a multi-line paste
        // creates blocks. Remove our temporary value from only the newly adjacent blocks.
        // Ordinary keystrokes leave the neighboring identities unchanged, so this is O(1).
        foreach (var (block, state) in _applied.ToArray())
            NormalizeCopiedPadding(document, block, state);

        foreach (var (block, state) in _applied.ToArray())
        {
            var top = ReferenceEquals(block, first);
            var bottom = ReferenceEquals(block, last);
            if (top == state.Top && bottom == state.Bottom)
            {
                state.Previous = block.PreviousBlock;
                state.Next = block.NextBlock;
                continue;
            }

            Restore(block, state);
            _applied.Remove(block);
        }

        if (first is not null)
            Apply(first, top: true, bottom: ReferenceEquals(first, last));
        if (last is not null && !ReferenceEquals(last, first))
            Apply(last, top: false, bottom: true);
    }

    internal void Suspend()
    {
        foreach (var (block, state) in _applied)
            Restore(block, state);
        _applied.Clear();
    }

    internal static bool IsEmpty(FlowDocument document)
    {
        var first = ContentEdge(document.Blocks.FirstBlock, forward: true);
        if (first is null)
            return true;
        if (!ReferenceEquals(first, ContentEdge(document.Blocks.LastBlock, forward: false))
            || first is not Paragraph paragraph)
            return false;

        // A whole-document TextRange includes WPF's synthetic final paragraph break.
        // The paragraph's inner range excludes it, but includes real text, line breaks,
        // and inline objects.
        return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).IsEmpty;
    }

    internal static bool CollapseEmptySelection(RichTextBox editor)
    {
        if (editor.Selection.IsEmpty || !IsEmpty(editor.Document))
            return false;

        var caret = editor.CaretPosition;
        editor.Selection.Select(caret, caret);
        return true;
    }

    internal static void RemoveLegacyPads(FlowDocument document)
    {
        for (var block = document.Blocks.FirstBlock; block is not null;)
        {
            var next = block.NextBlock;
            if (Equals(block.Tag, FloatingImageCoordinator.EditorEndPadTag))
                document.Blocks.Remove(block);
            block = next;
        }
    }

    private void Apply(Block block, bool top, bool bottom)
    {
        if (_applied.ContainsKey(block))
            return;

        var original = block.Padding;
        var applied = new Thickness(
            original.Left,
            original.Top + (top ? PaddingDip : 0),
            original.Right,
            original.Bottom + (bottom ? PaddingDip : 0));
        _applied.Add(block, new AppliedPadding
        {
            Original = original,
            OriginalLocal = block.ReadLocalValue(Block.PaddingProperty),
            Applied = applied,
            Top = top,
            Bottom = bottom,
            Previous = block.PreviousBlock,
            Next = block.NextBlock
        });
        block.SetCurrentValue(Block.PaddingProperty, applied);
    }

    private void NormalizeCopiedPadding(FlowDocument document, Block source, AppliedPadding state)
    {
        if (!ReferenceEquals(source.Parent, document))
            return;

        NormalizeNewNeighbors(source.NextBlock, state.Next, forward: true, state);
        NormalizeNewNeighbors(source.PreviousBlock, state.Previous, forward: false, state);
    }

    private void NormalizeNewNeighbors(Block? block, Block? oldNeighbor, bool forward,
        AppliedPadding state)
    {
        while (block is not null && !ReferenceEquals(block, oldNeighbor)
            && !_applied.ContainsKey(block) && block.Padding == state.Applied)
        {
            Restore(block, state);
            block = forward ? block.NextBlock : block.PreviousBlock;
        }
    }

    private static void Restore(Block block, AppliedPadding state)
    {
        // Only the edge insets belong to this service. List marker sizing can change
        // the horizontal gutter while spacing is applied; never restore its old width.
        var current = block.Padding;
        var restored = new Thickness(current.Left,
            state.Top && current.Top == state.Applied.Top ? state.Original.Top : current.Top,
            current.Right,
            state.Bottom && current.Bottom == state.Applied.Bottom ? state.Original.Bottom : current.Bottom);
        if (ReferenceEquals(state.OriginalLocal, DependencyProperty.UnsetValue) && restored == state.Original)
            block.ClearValue(Block.PaddingProperty);
        else
            block.SetCurrentValue(Block.PaddingProperty, restored);
    }

    private static Block? ContentEdge(Block? block, bool forward)
    {
        while (block is not null && (Equals(block.Tag, FloatingImageCoordinator.ExtentPadTag)
            || Equals(block.Tag, FloatingImageCoordinator.EditorEndPadTag)))
        {
            block = forward ? block.NextBlock : block.PreviousBlock;
        }

        return block;
    }
}
