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
    {
        // Base size matches NoteCardControl: 220x140 + 12 margin.
        var height = GridMinHeight;

        if (note.HasTags)
            height += 28;

        // note.Content lazily decodes+caches the RTF/Base64 preview on first access. During a bulk
        // load, sizing dozens of not-yet-warmed cards here would force that decode synchronously on
        // the UI thread for every one of them - exactly the stutter this estimate exists to avoid.
        // Fall back to the tag-only estimate until WarmContentPreview has actually run for this card;
        // the real measured extent corrects any difference once containers realize.
        if (note.IsContentPreviewCached)
        {
            var contentLines = EstimateLineCount(note.Content, charsPerLine: 36, maxLines: 6);
            var extraContentLines = Math.Max(0, contentLines - 2);
            height += extraContentLines * 15;
        }

        return new Size(GridWidth, Math.Clamp(height, GridMinHeight, GridMinHeight + 118));
    }

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
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0)
            return 0;

        var lines = (int)Math.Ceiling(normalized.Length / (double)charsPerLine);
        return Math.Clamp(lines, 1, maxLines);
    }
}