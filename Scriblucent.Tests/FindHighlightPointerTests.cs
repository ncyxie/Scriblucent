using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class FindHighlightPointerTests
{
    [Fact]
    public void Partial_line_selection_ends_at_the_last_selected_character()
    {
        RunSta(() =>
        {
            var run = new Run("alpha beta gamma");
            var document = new FlowDocument(new Paragraph(run)) { PagePadding = new Thickness(0) };
            var editor = LayoutEditor(document);
            var start = run.ContentStart.GetPositionAtOffset(6, LogicalDirection.Forward)!;
            var end = run.ContentStart.GetPositionAtOffset(10, LogicalDirection.Forward)!;

            var selected = NoteEditorWindow.SelectionLineRect(start, end);
            var expectedEnd = end.GetCharacterRect(LogicalDirection.Backward);
            editor.Selection.Select(start, end);

            Assert.False(selected.IsEmpty);
            Assert.Equal("beta", editor.Selection.Text);
            Assert.True(Math.Abs(selected.Right - expectedEnd.Right) <= 2,
                $"selected={selected}, startForward={start.GetCharacterRect(LogicalDirection.Forward)}, endBackward={expectedEnd}, endForward={end.GetCharacterRect(LogicalDirection.Forward)}");
            Assert.True(selected.Right < editor.ActualWidth / 2);
        });
    }

    [Fact]
    public void Selection_crossing_a_paragraph_does_not_fill_the_column()
    {
        RunSta(() =>
        {
            var first = new Run("small line");
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            document.Blocks.Add(new Paragraph(first));
            document.Blocks.Add(new Paragraph(new Run("next line")));
            var editor = LayoutEditor(document);
            var start = first.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward)!;
            var nextLine = start.GetLineStartPosition(1)!;

            var selected = NoteEditorWindow.SelectionLineRect(start, nextLine);
            var lastGlyph = first.ContentEnd.GetCharacterRect(LogicalDirection.Backward);

            Assert.False(selected.IsEmpty);
            Assert.True(Math.Abs(selected.Right - lastGlyph.Right) <= 2,
                $"selected={selected}, startForward={start.GetCharacterRect(LogicalDirection.Forward)}, lastGlyph={lastGlyph}, nextBackward={nextLine.GetCharacterRect(LogicalDirection.Backward)}");
            Assert.True(selected.Right < editor.ActualWidth / 2);
        });
    }

    private static RichTextBox LayoutEditor(FlowDocument document)
    {
        var editor = new RichTextBox { Document = document, Width = 400, Height = 180, Padding = new Thickness(0) };
        editor.Measure(new Size(400, 180));
        editor.Arrange(new Rect(0, 0, 400, 180));
        editor.UpdateLayout();
        return editor;
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    public void Composed_highlight_tiles_have_no_translucent_seam(int dpi)
    {
        RunSta(() =>
        {
            const double originY = 100_096;
            var first = new GeometryGroup
            {
                Transform = new TranslateTransform(0, originY),
                FillRule = FillRule.Nonzero
            };
            first.Children.Add(new RectangleGeometry(new Rect(0, 246, 30, 10)));
            var second = new GeometryGroup
            {
                Transform = new TranslateTransform(0, originY + 256),
                FillRule = FillRule.Nonzero
            };
            second.Children.Add(new RectangleGeometry(new Rect(0, 0, 30, 18)));

            var canvas = new Canvas
            {
                Width = 40,
                Height = 280,
                Background = Brushes.White
            };
            var overlay = new Canvas
            {
                RenderTransform = new TranslateTransform(0, -originY + 0.3)
            };
            canvas.Children.Add(overlay);
            var path = new System.Windows.Shapes.Path
            {
                IsHitTestVisible = false,
                Data = NoteEditorWindow.ComposeHighlightTileGeometry([first, second], 0, originY),
                Fill = new SolidColorBrush(Color.FromArgb(102, 70, 130, 220))
            };
            Canvas.SetTop(path, originY);
            overlay.Children.Add(path);
            canvas.Measure(new Size(40, 280));
            canvas.Arrange(new Rect(0, 0, 40, 280));

            var scale = dpi / 96.0;
            var bitmap = new RenderTargetBitmap((int)(40 * scale), (int)(280 * scale),
                dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var x = (int)(10 * scale);
            var interiorY = (int)(252 * scale);
            var interior = (interiorY * bitmap.PixelWidth + x) * 4;
            var seamY = (int)Math.Floor(256.3 * scale);
            for (var y = seamY - 1; y <= seamY + 1; y++)
            {
                var pixel = (y * bitmap.PixelWidth + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs(pixels[pixel + channel] - pixels[interior + channel]),
                        0, 1);
            }
        });
    }

    [Fact]
    public void Highlight_composition_stays_viewport_sized_deep_in_a_document()
    {
        RunSta(() =>
        {
            const double origin = 100_096;
            var tile = new GeometryGroup
            {
                Transform = new TranslateTransform(0, origin),
                FillRule = FillRule.Nonzero
            };
            tile.Children.Add(new RectangleGeometry(new Rect(0, 12, 80, 24)));

            var composed = NoteEditorWindow.ComposeHighlightTileGeometry([tile], 0, origin);

            Assert.InRange(composed.Bounds.Top, 11.99, 12.01);
            Assert.InRange(composed.Bounds.Bottom, 35.99, 36.01);
        });
    }

    [Fact]
    public void Selection_changes_rebuild_only_tiles_covering_the_changed_text()
    {
        RunSta(() =>
        {
            var first = new Paragraph(new Run("first"));
            var middle = new Paragraph(new Run("middle"));
            var last = new Paragraph(new Run("last"));
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            document.Blocks.Add(first);
            document.Blocks.Add(middle);
            document.Blocks.Add(last);
            LayoutEditor(document);

            Assert.True(NoteEditorWindow.SelectionChangeTouchesRange(
                middle.ContentStart, middle.ContentEnd, complete: true,
                first.ContentEnd, last.ContentStart));
            Assert.False(NoteEditorWindow.SelectionChangeTouchesRange(
                middle.ContentStart, middle.ContentEnd, complete: true,
                first.ContentStart, first.ContentEnd));
            Assert.True(NoteEditorWindow.SelectionChangeTouchesRange(
                middle.ContentStart, middle.ContentEnd, complete: false,
                first.ContentStart, first.ContentEnd));
        });
    }

    [Fact]
    public void Selection_growing_or_shrinking_on_the_final_line_rebuilds_its_highlight()
    {
        RunSta(() =>
        {
            var run = new Run("first line selection");
            var document = new FlowDocument(new Paragraph(run)) { PagePadding = new Thickness(0) };
            var editor = LayoutEditor(document);
            var line = run.ContentStart.GetLineStartPosition(0)!;
            var (next, coveredEnd) = NoteEditorWindow.GetSelectionLineCoverage(line, document);
            Assert.Null(next);

            var firstCharacter = run.ContentStart.GetPositionAtOffset(1, LogicalDirection.Forward)!;
            editor.Selection.Select(run.ContentStart, firstCharacter);
            var smallSelection = NoteEditorWindow.SelectionLineRect(editor.Selection.Start, editor.Selection.End);
            editor.Selection.Select(run.ContentStart, run.ContentEnd);
            var largeSelection = NoteEditorWindow.SelectionLineRect(editor.Selection.Start, editor.Selection.End);
            Assert.True(largeSelection.Width > smallSelection.Width * 3);
            Assert.True(NoteEditorWindow.SelectionChangeTouchesRange(line, coveredEnd, complete: true,
                firstCharacter, run.ContentEnd));
            Assert.True(NoteEditorWindow.SelectionChangeTouchesRange(line, coveredEnd, complete: true,
                run.ContentEnd, firstCharacter));
        });
    }

    [Fact]
    public void Normal_find_groups_include_the_separately_painted_active_result()
    {
        MatchSpan[] spans = [new(0, 1), new(1, 1), new(2, 1), new(3, 1)];

        Assert.Equal((4, 4), FindHighlightSlices.ContiguousGroup(
            spans, 0, spans.Length, activeIndex: -1));
    }

    [Fact]
    public void Find_path_composes_only_the_viewport_and_one_tile_halo()
    {
        Assert.Equal((0, 1), NoteEditorWindow.FindComposedTileRange(0, 180));
        Assert.Equal((2, 4), NoteEditorWindow.FindComposedTileRange(800, 180));
    }

    [Fact]
    public void Cached_blue_line_bands_keep_their_document_position_after_offscreen_scroll()
    {
        RunSta(() =>
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            for (var i = 0; i < 20; i++)
                document.Blocks.Add(new Paragraph(new Run($"lead {i}")) { Margin = new Thickness(0) });
            var target = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 40,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            target.Inlines.Add(new Run(string.Concat(Enumerable.Repeat("small text and more ", 30)))
                { FontSize = 12 });
            target.Inlines.Add(new Run("LARGE WORDS") { FontSize = 30 });
            document.Blocks.Add(target);

            var editor = new RichTextBox
            {
                Document = document,
                Width = 150,
                Height = 180,
                Padding = new Thickness(0)
            };
            editor.Measure(new Size(150, 180));
            editor.Arrange(new Rect(0, 0, 150, 180));
            editor.UpdateLayout();

            var line = target.ContentStart;
            for (var i = 0; i < 5; i++)
                line = line.GetLineStartPosition(1)!;
            var next = line.GetLineStartPosition(1)!;
            var origins = new Dictionary<Paragraph, NoteEditorWindow.LineBandOrigin>();
            var before = NoteEditorWindow.BlueSelectionLineRect(line, next, line, next, origins, 0);
            var viewer = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor)!;
            viewer.ScrollToVerticalOffset(before.Top - 50);
            editor.UpdateLayout();
            var after = NoteEditorWindow.BlueSelectionLineRect(line, next, line, next,
                origins, viewer.VerticalOffset);

            Assert.False(before.IsEmpty);
            Assert.InRange(Math.Abs(before.Top - after.Top - viewer.VerticalOffset), 0, 0.5);
            Assert.InRange(Math.Abs(before.Height - after.Height), 0, 0.01);
        });
    }

    [Fact]
    public void Blue_selection_height_is_independent_of_the_selected_font_run()
    {
        RunSta(() =>
        {
            var small = new Run("small ") { FontSize = 12 };
            var large = new Run("LARGE") { FontSize = 24 };
            var paragraph = new Paragraph { LineHeight = 30, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            paragraph.Inlines.Add(small);
            paragraph.Inlines.Add(large);
            var editor = LayoutEditor(new FlowDocument(paragraph) { PagePadding = new Thickness(0) });

            var smallEnd = small.ContentStart.GetPositionAtOffset(3, LogicalDirection.Forward)!;
            var largeEnd = large.ContentStart.GetPositionAtOffset(3, LogicalDirection.Forward)!;
            var smallBlue = NoteEditorWindow.BlueSelectionLineRect(paragraph.ContentStart, null, small.ContentStart, smallEnd);
            var largeBlue = NoteEditorWindow.BlueSelectionLineRect(paragraph.ContentStart, null, large.ContentStart, largeEnd);
            var smallFind = NoteEditorWindow.SelectionLineRect(small.ContentStart, smallEnd);
            var largeFind = NoteEditorWindow.SelectionLineRect(large.ContentStart, largeEnd);

            Assert.False(smallBlue.IsEmpty);
            Assert.False(largeBlue.IsEmpty);
            Assert.InRange(Math.Abs(smallBlue.Top - largeBlue.Top), 0, 0.01);
            Assert.InRange(Math.Abs(smallBlue.Height - largeBlue.Height), 0, 0.01);
            Assert.InRange(smallBlue.Height, 29.99, 30.01);
            Assert.True(largeFind.Height > smallFind.Height + 5);
            Assert.True(editor.ActualWidth > largeBlue.Right);
        });
    }

    [Fact]
    public void Blue_selection_uses_each_paragraphs_layout_height()
    {
        RunSta(() =>
        {
            var first = new Paragraph(new Run("first line"))
            {
                Margin = new Thickness(0), LineHeight = 22, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            var second = new Paragraph(new Run("second line"))
            {
                Margin = new Thickness(0), LineHeight = 30, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            document.Blocks.Add(first);
            document.Blocks.Add(second);
            LayoutEditor(document);

            var firstBlue = NoteEditorWindow.BlueSelectionLineRect(first.ContentStart, second.ContentStart, first.ContentStart, first.ContentEnd);
            var secondBlue = NoteEditorWindow.BlueSelectionLineRect(second.ContentStart, null, second.ContentStart, second.ContentEnd);

            Assert.InRange(firstBlue.Height, 21.99, 22.01);
            Assert.InRange(secondBlue.Height, 29.99, 30.01);
            Assert.True(firstBlue.Top < secondBlue.Top);
        });
    }

    [Theory]
    [InlineData(24.0, 30.0)]
    [InlineData(48.0, 48.0)]
    [InlineData(96.0, 96.0)]
    public void Blue_selection_bands_stay_aligned_when_adjacent_lines_start_with_different_fonts(double largeFontSize, double lineHeight)
    {
        RunSta(() =>
        {
            var first = new Run("small") { FontSize = 12 };
            var middle = new Run("LARGE") { FontSize = largeFontSize };
            var last = new Run("small") { FontSize = 12 };
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = lineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            paragraph.Inlines.Add(first);
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(middle);
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(last);
            LayoutEditor(new FlowDocument(paragraph) { PagePadding = new Thickness(0) });

            var firstBlue = NoteEditorWindow.BlueSelectionLineRect(first.ContentStart, middle.ContentStart, first.ContentStart, first.ContentEnd);
            var middleBlue = NoteEditorWindow.BlueSelectionLineRect(middle.ContentStart, last.ContentStart, middle.ContentStart, middle.ContentEnd);
            var lastBlue = NoteEditorWindow.BlueSelectionLineRect(last.ContentStart, null, last.ContentStart, last.ContentEnd);

            Assert.InRange(Math.Abs(firstBlue.Bottom - middleBlue.Top), 0, 0.5);
            Assert.InRange(Math.Abs(middleBlue.Bottom - lastBlue.Top), 0, 0.5);
            Assert.InRange(Math.Abs(firstBlue.Height - middleBlue.Height), 0, 0.01);
            Assert.InRange(Math.Abs(middleBlue.Height - lastBlue.Height), 0, 0.01);
        });
    }

    [Fact]
    public void Blue_selection_uses_one_height_across_wrapped_lines()
    {
        RunSta(() =>
        {
            var paragraph = new Paragraph(new Run("alpha beta gamma delta epsilon zeta eta theta iota kappa"))
            {
                Margin = new Thickness(0), LineHeight = 26, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            var document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
            var editor = new RichTextBox { Document = document, Width = 110, Height = 180, Padding = new Thickness(0) };
            editor.Measure(new Size(110, 180));
            editor.Arrange(new Rect(0, 0, 110, 180));
            editor.UpdateLayout();

            var first = paragraph.ContentStart;
            var second = first.GetLineStartPosition(1)!;
            var third = second.GetLineStartPosition(1)!;
            var firstBlue = NoteEditorWindow.BlueSelectionLineRect(first, second, first, second);
            var secondBlue = NoteEditorWindow.BlueSelectionLineRect(second, third, second, third);

            Assert.InRange(firstBlue.Height, 25.99, 26.01);
            Assert.InRange(secondBlue.Height, 25.99, 26.01);
            Assert.InRange(secondBlue.Top - firstBlue.Top, 25.99, 26.01);
        });
    }

    [Fact]
    public void Blue_selection_keeps_wrapped_mixed_font_lines_at_their_layout_positions()
    {
        RunSta(() =>
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 60, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            paragraph.Inlines.Add(new Run("one two three four five six seven eight ") { FontSize = 12 });
            paragraph.Inlines.Add(new Run("GIANT") { FontSize = 48 });
            paragraph.Inlines.Add(new Run(" nine ten eleven twelve") { FontSize = 12 });
            var document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
            var editor = new RichTextBox { Document = document, Width = 110, Height = 300, Padding = new Thickness(0) };
            editor.Measure(new Size(110, 300));
            editor.Arrange(new Rect(0, 0, 110, 300));
            editor.UpdateLayout();

            var line = paragraph.ContentStart;
            Rect? previous = null;
            var lineCount = 0;
            while (line is not null && lineCount < 12)
            {
                var next = line.GetLineStartPosition(1);
                var end = next is not null && next.CompareTo(paragraph.ContentEnd) < 0 ? next : paragraph.ContentEnd;
                if (line.CompareTo(end) >= 0)
                    break;

                var blue = NoteEditorWindow.BlueSelectionLineRect(line, next, line, end);
                Assert.InRange(blue.Height, 59.99, 60.01);
                if (previous is Rect prior)
                    Assert.InRange(Math.Abs(prior.Bottom - blue.Top), 0, 0.5);

                previous = blue;
                lineCount++;
                line = next is not null && next.CompareTo(paragraph.ContentEnd) < 0 ? next : null;
            }

            Assert.True(lineCount >= 3);
        });
    }

    [Fact]
    public void Blue_selection_expands_for_an_inline_image_without_expanding_the_next_line()
    {
        RunSta(() =>
        {
            var before = new Run("before ");
            var after = new Run(" after");
            var next = new Run("next line");
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 24, LineStackingStrategy = LineStackingStrategy.MaxHeight };
            paragraph.Inlines.Add(before);
            paragraph.Inlines.Add(new InlineUIContainer(new Border { Width = 30, Height = 80 }));
            paragraph.Inlines.Add(after);
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(next);
            LayoutEditor(new FlowDocument(paragraph) { PagePadding = new Thickness(0) });

            var firstLine = paragraph.ContentStart;
            var secondLine = firstLine.GetLineStartPosition(1)!;
            var imageLineBlue = NoteEditorWindow.BlueSelectionLineRect(firstLine, secondLine, before.ContentStart, after.ContentEnd);
            var nextLineBlue = NoteEditorWindow.BlueSelectionLineRect(secondLine, null, next.ContentStart, next.ContentEnd);

            Assert.True(imageLineBlue.Height >= 80);
            Assert.InRange(nextLineBlue.Height, 23.99, 24.01);
            Assert.True(imageLineBlue.Bottom <= nextLineBlue.Bottom);
        });
    }

    [Fact]
    public void Reversed_native_selection_keeps_the_caret_at_the_start()
    {
        RunSta(() =>
        {
            var run = new Run("alpha beta gamma");
            var editor = LayoutEditor(new FlowDocument(new Paragraph(run)));
            var start = run.ContentStart.GetPositionAtOffset(6, LogicalDirection.Forward)!;
            var end = run.ContentStart.GetPositionAtOffset(10, LogicalDirection.Forward)!;

            editor.Selection.Select(end, start);

            Assert.Equal(0, editor.Selection.Start.CompareTo(start));
            Assert.Equal(0, editor.Selection.End.CompareTo(end));
            Assert.Equal(0, editor.CaretPosition.CompareTo(start));
        });
    }

    [Fact]
    public void Flow_document_exposes_line_rectangles_beyond_the_viewport()
    {
        RunSta(() =>
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            Run? later = null;
            for (var i = 0; i < 40; i++)
            {
                var run = new Run($"line {i}");
                document.Blocks.Add(new Paragraph(run));
                if (i == 12)
                    later = run;
            }

            var editor = LayoutEditor(document);
            var rect = later!.ContentStart.GetCharacterRect(LogicalDirection.Forward);

            Assert.False(rect.IsEmpty);
            Assert.True(rect.Top > editor.ActualHeight);
        });
    }

    [Fact]
    public void Wrapped_selection_stops_at_the_last_character_on_each_visual_line()
    {
        RunSta(() =>
        {
            var run = new Run("alpha beta gamma delta epsilon zeta eta theta");
            var document = new FlowDocument(new Paragraph(run)) { PagePadding = new Thickness(0) };
            var editor = new RichTextBox { Document = document, Width = 110, Height = 180, Padding = new Thickness(0) };
            editor.Measure(new Size(110, 180));
            editor.Arrange(new Rect(0, 0, 110, 180));
            editor.UpdateLayout();

            var start = run.ContentStart;
            var nextLine = start.GetLineStartPosition(1);
            Assert.NotNull(nextLine);

            var selected = NoteEditorWindow.SelectionLineRect(start, nextLine!);
            var previous = nextLine!.GetNextInsertionPosition(LogicalDirection.Backward)!;
            var lastCaret = previous.GetCharacterRect(LogicalDirection.Forward);

            Assert.False(selected.IsEmpty);
            Assert.True(selected.Right <= editor.ActualWidth);
            Assert.InRange(selected.Right, lastCaret.Right - 3, lastCaret.Right + 15);
        });
    }

    [Fact]
    public void Viewport_slice_is_not_the_whole_note()
    {
        const int characters = 145_000;
        var matches = new MatchSpan[characters / 2];
        for (var i = 0; i < matches.Length; i++)
            matches[i] = new MatchSpan(i * 2, 1);

        var (first, count) = FindHighlightSlices.OverlappingRange(matches, 40_000, 42_000);

        Assert.True(count > 0);
        Assert.True(count < matches.Length);
        Assert.True(first > 0);
        Assert.True(first + count < matches.Length);
        Assert.True(matches[first].End > 40_000);
        Assert.True(matches[first + count - 1].Start < 42_000);
    }

    [Fact]
    public void Dense_find_hits_are_grouped_before_measuring_character_rectangles()
    {
        var spans = new MatchSpan[100_000];
        for (var i = 0; i < spans.Length; i++)
            spans[i] = new MatchSpan(i, 1);

        var groups = 0;
        for (var first = 0; first < spans.Length; groups++)
        {
            var (last, end) = FindHighlightSlices.ContiguousGroup(
                spans, first, spans.Length, activeIndex: -1);
            Assert.Equal(last, end);
            first = last;
        }

        Assert.Equal(391, groups);
    }

    [Fact]
    public void Active_find_result_stays_separate_from_touching_hits()
    {
        MatchSpan[] spans = [new(0, 1), new(1, 1), new(2, 1), new(3, 1)];

        Assert.Equal((2, 2), FindHighlightSlices.ContiguousGroup(spans, 0, 4, activeIndex: 2));
        Assert.Equal((3, 3), FindHighlightSlices.ContiguousGroup(spans, 2, 4, activeIndex: 2));
        Assert.Equal((4, 4), FindHighlightSlices.ContiguousGroup(spans, 3, 4, activeIndex: 2));
    }

    [Fact]
    public void Wide_visual_lines_are_sliced_horizontally_before_searching_hits()
    {
        RunSta(() =>
        {
            var run = new Run(string.Concat(Enumerable.Repeat("a b ", 25_000)));
            var document = new FlowDocument(new Paragraph(run))
            {
                PagePadding = new Thickness(0),
                PageWidth = 1_000_000
            };
            var editor = LayoutEditor(document);
            var line = run.ContentStart.GetLineStartPosition(0);
            var lines = 0;
            var charactersConsidered = 0;
            while (line is not null && lines < 100)
            {
                var rect = line.GetCharacterRect(LogicalDirection.Forward);
                if (rect.Top >= 256)
                    break;

                Assert.True(FindHighlightSlices.TryGetHorizontalLineSlice(
                    editor, -400, 800, rect.Top + rect.Height * 0.5,
                    out var start, out var end));
                charactersConsidered += start.GetOffsetToPosition(end);
                lines++;
                line = line.GetLineStartPosition(1);
            }

            Assert.True(lines > 1);
            Assert.InRange(charactersConsidered, 1, 10_000);
            Assert.Equal(100_000, run.Text.Length);
        });
    }

    [Fact]
    public void Visible_hit_lands_on_the_matched_word()
    {
        RunSta(() =>
        {
            var document = new FlowDocument();
            document.Blocks.Add(new Paragraph(new Run("alpha beta")));
            document.Blocks.Add(new Paragraph(new Run("gamma delta")));

            var list = new List();
            list.ListItems.Add(new ListItem(new Paragraph(new Run("beta item"))));
            document.Blocks.Add(list);

            var linked = new Paragraph();
            linked.Inlines.Add(new Run("see "));
            linked.Inlines.Add(new Hyperlink(new Run("beta")) { NavigateUri = new Uri("https://example.com") });
            linked.Inlines.Add(new Run(" now"));
            linked.Inlines.Add(new LineBreak());
            linked.Inlines.Add(new Run("beta again"));
            document.Blocks.Add(linked);

            var bold = new Paragraph();
            bold.Inlines.Add(new Run("be"));
            bold.Inlines.Add(new Bold(new Run("ta")));
            bold.Inlines.Add(new Run(" end"));
            document.Blocks.Add(bold);

            document.Blocks.Add(new Paragraph(new Run("beta beta beta")));

            var index = BuildIndex(document);
            var matches = FindSpans(index.Text, "beta");
            Assert.True(matches.Length > 1);

            var target = matches[2];
            var (first, count) = FindHighlightSlices.OverlappingRange(matches, target.Start, target.End);
            Assert.Equal(1, count);
            Assert.True(count < matches.Length);

            var slice = matches[first];
            var words = ReadResolved(index.Runs, slice);
            Assert.Equal(new[] { "beta" }, words);
        });
    }

    private static MatchSpan[] FindSpans(string text, string query)
    {
        var found = new List<MatchSpan>();
        var searchStart = 0;
        while (searchStart <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, searchStart, StringComparison.Ordinal);
            if (index < 0)
                break;

            found.Add(new MatchSpan(index, query.Length));
            searchStart = index + query.Length;
        }

        return found.ToArray();
    }

    private static List<string> ReadResolved(List<RunSpan> runs, MatchSpan match)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var run in runs)
        {
            var runEnd = run.Start + run.Length;
            if (runEnd <= match.Start || run.Start >= match.End)
                continue;

            var sliceStart = Math.Max(match.Start, run.Start) - run.Start;
            var sliceEnd = Math.Min(match.End, runEnd) - run.Start;
            if (!FindHighlightSlices.TryGetSlice(run.Pointer, sliceStart, sliceEnd - sliceStart, out var start, out _))
                continue;

            var pointer = start;
            for (var i = 0; i < sliceEnd - sliceStart && pointer is not null; i++)
            {
                var text = pointer.GetTextInRun(LogicalDirection.Forward);
                if (text.Length == 0)
                    break;

                current.Append(text[0]);
                pointer = pointer.GetPositionAtOffset(1, LogicalDirection.Forward);
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    private readonly record struct RunSpan(int Start, int Length, TextPointer Pointer);

    private static (string Text, List<RunSpan> Runs) BuildIndex(FlowDocument document)
    {
        var text = new StringBuilder();
        var runs = new List<RunSpan>();
        var traversed = 0;
        var seenText = false;
        var pendingBreak = false;
        var navigator = document.ContentStart;
        var end = document.ContentEnd;

        while (navigator is not null && navigator.CompareTo(end) < 0)
        {
            var context = navigator.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var runText = navigator.GetTextInRun(LogicalDirection.Forward);
                if (runText.Length > 0)
                {
                    if (seenText && pendingBreak)
                    {
                        text.Append('\n');
                        traversed++;
                    }

                    pendingBreak = false;
                    runs.Add(new RunSpan(traversed, runText.Length, navigator));
                    text.Append(runText);
                    traversed += runText.Length;
                    seenText = true;
                }
            }
            else if (context is TextPointerContext.ElementStart or TextPointerContext.ElementEnd
                     && navigator.GetAdjacentElement(LogicalDirection.Forward) is Block or LineBreak or InlineUIContainer)
            {
                pendingBreak = true;
            }

            navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
        }

        return (text.ToString(), runs);
    }

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                scenario();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "STA scenario timed out");
        if (failure is not null)
            throw failure;
    }
}
