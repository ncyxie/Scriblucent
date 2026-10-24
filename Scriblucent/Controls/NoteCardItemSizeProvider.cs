using System.Windows;
using Scriblucent.ViewModels;
using WpfToolkit.Controls;

namespace Scriblucent.Controls;

/// <summary>
/// Estimates note card sizes for virtualized layout without realizing UI containers.
/// </summary>
public sealed class NoteCardItemSizeProvider : IItemSizeProvider
{
    private const double GridWidth = 244;
    private const double GridMinHeight = 164;
    private const double ListWidth = double.PositiveInfinity;
    private const double ListMinHeight = 88;
    private const double ListLineHeight = 18;
    private const int ListPreviewCharsPerLine = 72;

    private readonly bool _isListLayout;

    public NoteCardItemSizeProvider(bool isListLayout)
        => _isListLayout = isListLayout;

    public Size GetSizeForItem(object item)
    {
        if (item is not NoteCardViewModel note)
            return _isListLayout ? new Size(800, ListMinHeight) : new Size(GridWidth, GridMinHeight);

        if (_isListLayout)
            return EstimateListSize(note);

        return EstimateGridSize(note);
    }

    private static Size EstimateGridSize(NoteCardViewModel note)
        // Preview line budgets reserve space for the header and optional tags inside
        // the same 140-DIP face used by scrolling tiles. No decoding or size correction.
        => new(GridWidth, GridMinHeight);

    private static Size EstimateListSize(NoteCardViewModel note)
    {
        var lines = 2;
        if (note.IsContentPreviewCached)
            lines += EstimateLineCount(note.Content, ListPreviewCharsPerLine, maxLines: 2);
        if (note.HasTags)
            lines += 1;

        var height = ListMinHeight + Math.Max(0, lines - 3) * ListLineHeight;
        return new Size(ListWidth, Math.Clamp(height, ListMinHeight, ListMinHeight + 72));
    }

    private static int EstimateLineCount(string? text, int charsPerLine, int maxLines)
    {
        // Replacing newline characters does not change length. Trim the original
        // span so a column-count change cannot allocate full preview copies for
        // every estimated card in the library.
        var length = text.AsSpan().Trim().Length;
        if (length == 0)
            return 0;

        var lines = (int)Math.Ceiling(length / (double)charsPerLine);
        return Math.Clamp(lines, 1, maxLines);
    }
}
