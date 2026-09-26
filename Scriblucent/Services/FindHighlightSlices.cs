using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scriblucent.Services;

internal readonly struct MatchSpan
{
    public MatchSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
}

/// <summary>
/// Maps a find hit onto one indexed text run with two offset jumps. The run index
/// must be rebuilt after document edits or formatting changes that split runs.
/// </summary>
internal static class FindHighlightSlices
{
    internal static bool TryGetHorizontalLineSlice(
        RichTextBox editor, double leftX, double rightX, double lineY,
        out TextPointer start, out TextPointer end)
    {
        start = null!;
        end = null!;
        var left = editor.GetPositionFromPoint(new Point(leftX, lineY), snapToText: true);
        var right = editor.GetPositionFromPoint(new Point(rightX, lineY), snapToText: true);
        if (left is null || right is null)
            return false;
        if (left.CompareTo(right) > 0)
            (left, right) = (right, left);

        start = left;
        end = right;
        return start.CompareTo(end) < 0;
    }

    internal static bool TryGetSlice(
        TextPointer runStart,
        int startInRun,
        string expected,
        out TextPointer start,
        out TextPointer end)
        => TryGetSlice(runStart, startInRun, expected?.Length ?? 0, out start, out end);

    internal static bool TryGetSlice(
        TextPointer runStart,
        int startInRun,
        int length,
        out TextPointer start,
        out TextPointer end)
    {
        start = null!;
        end = null!;
        if (runStart is null || length <= 0 || startInRun < 0)
            return false;

        if (runStart.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            return false;

        start = startInRun == 0
            ? runStart
            : runStart.GetPositionAtOffset(startInRun, LogicalDirection.Forward);
        end = runStart.GetPositionAtOffset(startInRun + length, LogicalDirection.Forward);
        return start is not null && end is not null && start.CompareTo(end) < 0;
    }

    internal static TextPointer? Advance(TextPointer? origin, int characterCount)
    {
        if (origin is null || characterCount < 0)
            return null;

        if (characterCount == 0)
            return origin;

        if (origin.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            return null;

        return origin.GetPositionAtOffset(characterCount, LogicalDirection.Forward);
    }

    /// <summary>
    /// Matches are sorted and non-overlapping. Returns the slice whose spans overlap
    /// <paramref name="viewStart"/> inclusive through <paramref name="viewEnd"/> exclusive.
    /// </summary>
    internal static (int First, int Count) OverlappingRange(
        ReadOnlySpan<MatchSpan> matches,
        int viewStart,
        int viewEnd)
    {
        if (matches.Length == 0 || viewEnd <= viewStart)
            return (0, 0);

        var lo = 0;
        var hi = matches.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (matches[mid].End <= viewStart)
                lo = mid + 1;
            else
                hi = mid;
        }

        var first = lo;
        hi = matches.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (matches[mid].Start < viewEnd)
                lo = mid + 1;
            else
                hi = mid;
        }

        return (first, lo - first);
    }

    /// <summary>Collapse touching hits before resolving WPF character rectangles.
    /// The active result remains separate so its stronger color is retained.</summary>
    internal static (int LastExclusive, int End) ContiguousGroup(
        ReadOnlySpan<MatchSpan> matches, int first, int lastExclusive, int activeIndex)
    {
        var last = first + 1;
        var end = matches[first].End;
        var active = first == activeIndex;
        while (last < lastExclusive && last - first < 256
            && (last == activeIndex) == active
            && matches[last].Start <= end)
        {
            end = Math.Max(end, matches[last].End);
            last++;
        }

        return (last, end);
    }
}
