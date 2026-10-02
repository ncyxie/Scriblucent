using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

/// <summary>
/// Headless integration tests for text wrap: run the real RichTextBox +
/// FloatingImageCoordinator + NoteWrapLayoutEngine on an off-screen STA window, so
/// paragraph geometry is measured exactly like in the editor.
/// </summary>
public class NoteWrapLayoutEngineTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Images", "pixel.png");

    [Fact]
    public void Engine_AppliesSquareWrapLanes_AndClearsThemExactly()
        => RunOnSta(RunSquareScenario);

    [Fact]
    public void Engine_SquareLane_Hysteresis_KeepsSideUntilClearlyBetter()
        => RunOnSta(RunLaneHysteresisScenario);

    [Fact]
    public void Engine_Square_PhotoBetweenWords_SplitsTheLine()
        => RunOnSta(RunBetweenWordsSplitScenario);

    [Fact]
    public void Engine_SquareLiveApply_AdjustsWithoutResettingMargins()
        => RunOnSta(RunSquareLiveStabilityScenario);

    [Fact]
    public void Engine_TopAndBottom_PushesFirstBandParagraphBelowBand()
        => RunOnSta(RunTopAndBottomScenario);

    [Fact]
    public void Engine_SquareAndTopAndBottom_Coexist()
        => RunOnSta(RunCoexistenceScenario);

    [Fact]
    public void Engine_TopAndBottom_TallSingleParagraph_PushesBelowBand()
        => RunOnSta(() => RunTallParagraphScenario());

    [Fact]
    public void Engine_TopAndBottom_TallSingleParagraph_WithDocumentEnabled()
        => RunOnSta(() => RunTallParagraphScenario(documentEnabled: true));

    [Fact]
    public void Engine_TopAndBottom_TallSingleParagraph_Scrolled()
        => RunOnSta(() => RunTallParagraphScenario(scrollY: 300));

    [Fact]
    public void Engine_Square_TallSingleParagraph_Scrolled()
        => RunOnSta(RunSquareTallScrolledScenario);

    [Fact]
    public void Engine_TopAndBottom_TallSingleParagraph_WithSurfaceLayoutTransform()
        => RunOnSta(() => RunTallParagraphScenario(surfaceTransform: true));

    [Fact]
    public void Engine_TopAndBottom_TallParagraph_XamlLoadedDocument()
        => RunOnSta(() => RunTallParagraphScenario(xamlRoundTrip: true));

    [Fact]
    public void Engine_TopAndBottom_TallParagraph_FixedLineHeight()
        => RunOnSta(() => RunTallParagraphScenario(fixedLineHeight: 72));

    [Fact]
    public void Engine_TopAndBottom_BandMovedWithinSameLine_UpdatesSpacerInPlace()
        => RunOnSta(RunInPlaceUpdateScenario);

    [Fact]
    public void Engine_TopAndBottom_BandMovedPastSplit_SplitFollows()
        => RunOnSta(RunBandMovedPastSplitScenario);

    [Fact]
    public void Engine_TopAndBottom_TwoBandsShareOneSpacer()
        => RunOnSta(RunTwoBandsShareOneSpacerScenario);

    [Fact]
    public void Engine_ApplyLive_LeadingEdgeThenThrottled()
        => RunOnSta(RunLiveApplyScenario);

    [Fact]
    public void Engine_CaptureRestore_ReappliesExactWrapState()
        => RunOnSta(RunCaptureRestoreScenario);

    [Fact]
    public void Engine_Square_IdleReapply_FiresNoEvents()
        => RunOnSta(() => RunIdleReapplyStabilityScenario(NoteImageWrapMode.Square));

    [Fact]
    public void Engine_TopAndBottom_IdleReapply_FiresNoEvents()
        => RunOnSta(() => RunIdleReapplyStabilityScenario(NoteImageWrapMode.TopAndBottom));

    [Fact]
    public void Engine_TopAndBottom_FirstBlockPush_IdleReapply_FiresNoEvents()
        => RunOnSta(RunFirstBlockPushIdleReapplyScenario);

    [Fact]
    public void InlineSpacer_ReservesBandInsidePlainParagraph()
        => RunOnSta(() => RunInlineSpacerProbe(BuildPlainParagraph));

    [Fact]
    public void InlineSpacer_ReservesBandInsidePlainParagraph_WithDocumentEnabled()
        => RunOnSta(() => RunInlineSpacerProbe(BuildPlainParagraph, documentEnabled: true));

    [Fact]
    public void InlineSpacer_ReservesBandInsideListItem()
        => RunOnSta(() => RunInlineSpacerProbe(BuildListItemParagraph));

    [Fact]
    public void InlineSpacer_ReservesBandInsideTableCell()
        => RunOnSta(() => RunInlineSpacerProbe(BuildTableCellParagraph));

    [Fact]
    public void Engine_TopAndBottom_SplitsInsideListItem()
        => RunOnSta(RunListItemSplitScenario);

    [Fact]
    public void Engine_TopAndBottom_TableCellFallsBackToWholeParagraphPush()
        => RunOnSta(RunTableCellFallbackScenario);

    private static void RunInlineSpacerProbe(Action<RichTextBox> build, bool documentEnabled = false)
    {
        using var harness = CreateHarness(paragraphCount: 1);

        if (documentEnabled)
            harness.Editor.IsDocumentEnabled = true;

        harness.Editor.Document.Blocks.Clear();
        build(harness.Editor);
        harness.Editor.UpdateLayout();

        var paragraph = FindFirstParagraph(harness.Editor.Document.Blocks);
        Assert.NotNull(paragraph);

        var runs = EnumerateRuns(paragraph.Inlines).ToList();
        var run1 = runs[0];
        var run2 = runs[1];

        var beforeBottom = run1.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom;
        var afterTopBefore = run2.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

        var spacer = new Border
        {
            Width = 2000,
            Height = 100,
            Background = Brushes.Transparent
        };
        var container = new InlineUIContainer(spacer);
        paragraph.Inlines.InsertBefore(run2, container);
        harness.Editor.UpdateLayout();
        harness.Editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        harness.Editor.UpdateLayout();

        var afterBottom = run1.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom;
        var afterTop = run2.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

        // Still one paragraph (the harness's extent pad is a BlockUIContainer, not a split).
        Assert.Equal(1, CountParagraphs(harness.Editor.Document.Blocks));
        Assert.True(paragraph.Inlines.Contains(container), "container must live inside the paragraph");

        // The container reserves its height between the text above and below.
        Assert.True(
            afterTop - afterBottom >= 99,
            $"container should reserve ~100px: above bottom {afterBottom:F1}, below top {afterTop:F1}");

        // The text above barely moved (the container sits between run1 and run2).
        Assert.True(
            Math.Abs(beforeBottom - afterBottom) < 5,
            $"above text moved: {beforeBottom:F1}->{afterBottom:F1}");

        // Removal restores the original gap exactly.
        paragraph.Inlines.Remove(container);
        harness.Editor.UpdateLayout();
        harness.Editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        harness.Editor.UpdateLayout();
        var restoredTop = run2.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(
            Math.Abs(restoredTop - afterTopBefore) < 5,
            $"removal did not restore: {afterTopBefore:F1}->{restoredTop:F1}");
    }

    private static void BuildPlainParagraph(RichTextBox editor)
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run(RepeatText("Head text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Mid text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Tail text ", 40)));
        editor.Document.Blocks.Add(paragraph);
    }

    private static void BuildListItemParagraph(RichTextBox editor)
    {
        var list = new List();
        var item = new ListItem();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run(RepeatText("Head text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Mid text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Tail text ", 40)));
        item.Blocks.Add(paragraph);
        list.ListItems.Add(item);
        editor.Document.Blocks.Add(list);
    }

    private static void BuildTableCellParagraph(RichTextBox editor)
    {
        var table = new Table();
        var rowGroup = new TableRowGroup();
        var row = new TableRow();
        var cell = new TableCell();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run(RepeatText("Head text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Mid text ", 40)));
        paragraph.Inlines.Add(new Run(RepeatText("Tail text ", 40)));
        cell.Blocks.Add(paragraph);
        row.Cells.Add(cell);
        rowGroup.Rows.Add(row);
        table.RowGroups.Add(rowGroup);
        editor.Document.Blocks.Add(table);
    }

    private static string RepeatText(string text, int count)
        => string.Concat(Enumerable.Repeat(text, count));

    private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return run;
                    break;
                case Span span:
                    foreach (var nested in EnumerateRuns(span.Inlines))
                        yield return nested;
                    break;
            }
        }
    }

    private static int CountParagraphs(BlockCollection blocks)
    {
        var count = 0;
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph:
                    count++;
                    break;
                case Section section:
                    count += CountParagraphs(section.Blocks);
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                        count += CountParagraphs(item.Blocks);
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                                count += CountParagraphs(cell.Blocks);
                        }
                    }
                    break;
            }
        }

        return count;
    }

    private static IEnumerable<Paragraph> EnumerateAllParagraphs(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case Section section:
                    foreach (var nested in EnumerateAllParagraphs(section.Blocks))
                        yield return nested;
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        foreach (var nested in EnumerateAllParagraphs(item.Blocks))
                            yield return nested;
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                foreach (var nested in EnumerateAllParagraphs(cell.Blocks))
                                    yield return nested;
                            }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>The engine's transient inline spacer stack inside a paragraph, if present.
    /// (Height > 2 distinguishes the TopAndBottom band spacers from the 1px Square lane spacers.)</summary>
    private static List<InlineUIContainer> FindSpacers(Paragraph paragraph)
    {
        var found = new List<InlineUIContainer>();
        foreach (Inline inline in paragraph.Inlines)
        {
            if (inline is InlineUIContainer { Child: Border { Height: > 2 } })
                found.Add((InlineUIContainer)inline);
        }

        return found;
    }

    private static InlineUIContainer? FindSpacer(Paragraph paragraph)
        => FindSpacers(paragraph).LastOrDefault();

    /// <summary>Gets the inline directly after the last container of the spacer stack.</summary>
    private static Inline GetInlineAfterStack(InlineCollection host, List<InlineUIContainer> containers)
    {
        var last = containers[^1];
        var found = false;
        foreach (Inline child in host)
        {
            if (found)
                return child;
            if (ReferenceEquals(child, last))
                found = true;
        }

        throw new InvalidOperationException("spacer stack has no following inline");
    }

    private static Inline GetInlineBefore(Inline inline)
    {
        var host = GetInlineHost(inline);
        Inline? previous = null;
        foreach (Inline child in host)
        {
            if (ReferenceEquals(child, inline))
            {
                if (previous is null)
                    throw new InvalidOperationException("spacer has no preceding inline");
                return previous;
            }

            previous = child;
        }

        throw new InvalidOperationException("spacer not found in host");
    }

    private static Inline GetInlineAfter(Inline inline)
    {
        var host = GetInlineHost(inline);
        var found = false;
        foreach (Inline child in host)
        {
            if (found)
                return child;
            if (ReferenceEquals(child, inline))
                found = true;
        }

        throw new InvalidOperationException("spacer has no following inline");
    }

    private static InlineCollection GetInlineHost(Inline inline)
        => inline.Parent switch
        {
            Span span => span.Inlines,
            Paragraph p => p.Inlines,
            _ => throw new InvalidOperationException("inline host not found")
        };

    private static string GetRunsText(FlowDocument document)
        => string.Concat(EnumerateAllParagraphs(document.Blocks).SelectMany(p => EnumerateRuns(p.Inlines)).Select(r => r.Text));

    private static string GetRunsTextBeforeStack(Paragraph paragraph, List<InlineUIContainer> containers)
    {
        var sb = new StringBuilder();
        foreach (Inline child in paragraph.Inlines)
        {
            if (ReferenceEquals(child, containers[0]))
                break;
            if (child is Run run)
                sb.Append(run.Text);
        }

        return sb.ToString();
    }

    private static Paragraph? FindFirstParagraph(BlockCollection blocks)    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    return paragraph;
                case Section section:
                    return FindFirstParagraph(section.Blocks);
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        var found = FindFirstParagraph(item.Blocks);
                        if (found is not null)
                            return found;
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                var found = FindFirstParagraph(cell.Blocks);
                                if (found is not null)
                                    return found;
                            }
                        }
                    }
                    break;
            }
        }

        return null;
    }

    private static void RunOnSta(Action scenario)
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

    private static List<InlineUIContainer> FindSquareSpacers(Paragraph paragraph)
    {
        var result = new List<InlineUIContainer>();
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Child: Border { Height: <= 2 } } container:
                    result.Add(container);
                    break;
                case Span span:
                    foreach (var nested in span.Inlines)
                    {
                        if (nested is InlineUIContainer { Child: Border { Height: <= 2 } } c)
                            result.Add(c);
                    }
                    break;
            }
        }

        return result;
    }

    /// <summary>Groups a paragraph's characters into laid-out lines (top, min char x, max char x)
    /// so tests can assert the Word-style wrap geometry around the photo.</summary>
    private static List<(double Top, double MinLeft, double MaxRight)> MeasureLines(Paragraph paragraph)
    {
        var groups = new SortedDictionary<double, (double Min, double Max)>();
        var nav = paragraph.ContentStart;
        while (nav is not null && nav.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = nav.GetTextInRun(LogicalDirection.Forward);
                for (var i = 0; i < text.Length; i++)
                {
                    var position = nav.GetPositionAtOffset(i, LogicalDirection.Forward);
                    if (position is null)
                        continue;

                    var rect = position.GetCharacterRect(LogicalDirection.Forward);
                    if (rect.IsEmpty)
                        continue;

                    var key = Math.Round(rect.Top);
                    if (!groups.TryGetValue(key, out var extent))
                        extent = (rect.Left, rect.Right);
                    else
                        extent = (Math.Min(extent.Min, rect.Left), Math.Max(extent.Max, rect.Right));
                    groups[key] = extent;
                }
            }

            nav = nav.GetNextContextPosition(LogicalDirection.Forward);
        }

        return groups.Select(kv => (kv.Key, kv.Value.Min, kv.Value.Max)).ToList();
    }

    private static void AssertBothSidesClearThePhoto(
        Paragraph paragraph,
        double photoLeft,
        double photoRight,
        double gap)
    {
        var spacerTops = FindSquareSpacers(paragraph)
            .Select(container => Math.Round(container.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top))
            .ToHashSet();
        double? nearestLeft = null;
        double? nearestRight = null;
        var nav = paragraph.ContentStart;
        while (nav is not null && nav.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = nav.GetTextInRun(LogicalDirection.Forward);
                for (var i = 0; i < text.Length; i++)
                {
                    var position = nav.GetPositionAtOffset(i, LogicalDirection.Forward);
                    var rect = position?.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
                    if (rect.IsEmpty || !spacerTops.Contains(Math.Round(rect.Top)))
                        continue;
                    if (rect.Right <= photoLeft)
                        nearestLeft = nearestLeft is null ? rect.Right : Math.Max(nearestLeft.Value, rect.Right);
                    if (rect.Left >= photoRight)
                        nearestRight = nearestRight is null ? rect.Left : Math.Min(nearestRight.Value, rect.Left);
                }
            }

            nav = nav.GetNextContextPosition(LogicalDirection.Forward);
        }

        Assert.True(nearestLeft is not null && nearestLeft <= photoLeft - gap + 1,
            $"text on the left should stop {gap}px before the photo, got {nearestLeft:F1} vs photo {photoLeft:F1}");
        Assert.True(nearestRight is not null && nearestRight >= photoRight + gap - 1,
            $"text on the right should start {gap}px after the photo, got {nearestRight:F1} vs photo {photoRight:F1}");
    }

    private static void RunSquareScenario()
    {
        using var harness = CreateHarness(paragraphCount: 14);

        // No wrap: nothing may change.
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        var marginsBefore = AllMargins(harness.Editor.Document);
        Assert.All(marginsBefore.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsBefore.Values, m => Assert.Equal(0, m.Right, precision: 3));
        Assert.All(EnumerateAllParagraphs(harness.Editor.Document.Blocks), p => Assert.Empty(FindSquareSpacers(p)));

        // Square wrap: the lines inside the photo's band get transient spacers that split the
        // words around the photo (Word-style "word word [photo] word word") - the paragraphs
        // themselves keep their margins (lines above/below the band stay full-width).
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var paragraphs = EnumerateAllParagraphs(harness.Editor.Document.Blocks).ToList();
        var wrapped = paragraphs.Where(p => FindSquareSpacers(p).Count > 0).ToList();
        Assert.True(wrapped.Count >= 2, $"expected >= 2 wrapped paragraphs, got {wrapped.Count}");
        Assert.All(wrapped, p =>
        {
            var widths = FindSquareSpacers(p).Select(c => ((Border)c.Child).Width).ToList();
            Assert.All(widths, w => Assert.True(w > 0 && w <= 272, $"spacer width {w:F1} out of range"));
        });
        Assert.All(wrapped, p => Assert.Equal(0, Normalize(p.Margin).Left, precision: 3));

        // Paragraphs far above and below the image keep full width (no spacers, no margins).
        Assert.Empty(FindSquareSpacers(paragraphs.First()));
        Assert.Equal(0, Normalize(paragraphs.First().Margin).Left);
        Assert.Empty(FindSquareSpacers(paragraphs.Last()));
        Assert.Equal(0, Normalize(paragraphs.Last().Margin).Right);

        // Clear restores every margin and removes every spacer.
        harness.Engine.Clear();
        var marginsCleared = AllMargins(harness.Editor.Document);
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Right, precision: 3));
        Assert.All(EnumerateAllParagraphs(harness.Editor.Document.Blocks), p => Assert.Empty(FindSquareSpacers(p)));

        // Disabling wrap also clears.
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.None);
        harness.Engine.Apply();
        var marginsOff = AllMargins(harness.Editor.Document);
        Assert.All(marginsOff.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsOff.Values, m => Assert.Equal(0, m.Right, precision: 3));
        Assert.All(EnumerateAllParagraphs(harness.Editor.Document.Blocks), p => Assert.Empty(FindSquareSpacers(p)));
    }

    private static void RunBetweenWordsSplitScenario()
    {
        // The office-suite behavior: a Square-wrapped photo pushed into a line splits the words
        // around it - "word word [photo] word word". Words before the photo's left edge stay in
        // place; the words after it flow on the same line beside the photo; the lines below in
        // the band start after the photo; lines above and below the band keep full width.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();

        // Photo 200 wide at x=200. The keep-out box is the photo plus DefaultWrapGap on each side.
        var gap = NoteWrapMath.DefaultWrapGap;
        var wrapLeft = 200 - gap;
        var wrapRight = 400 + gap;
        var session = harness.Coordinator.GetWrapSessions().First();
        session.DocumentLeft = 200;
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var lines = MeasureLines(tall);
        Assert.True(lines.Count >= 6, $"expected a tall wrapped paragraph, got {lines.Count} lines");

        // The straddling line has words on BOTH sides of the photo.
        var splitLines = lines.Where(l => l.MinLeft < wrapLeft && l.MaxRight > wrapRight).ToList();
        Assert.NotEmpty(splitLines);
        AssertBothSidesClearThePhoto(
            tall,
            session.DocumentLeft + harness.Editor.Padding.Left,
            session.DocumentRight + harness.Editor.Padding.Left,
            gap);

        // Lines deeper in the band start after the photo's right edge.
        var belowBandStart = lines.Where(l => l.MinLeft >= wrapRight - 1).ToList();
        Assert.NotEmpty(belowBandStart);

        // Lines above and below the band keep full width (start at the text edge, reach the end).
        var fullWidth = lines.Where(l => l.MinLeft < 100 && l.MaxRight > 700).ToList();
        Assert.True(fullWidth.Count >= 2, $"expected full-width lines above/below the band, got {fullWidth.Count}");

        // The wrap is transient - clearing removes every spacer.
        harness.Engine.Clear();
        Assert.All(EnumerateAllParagraphs(harness.Editor.Document.Blocks), p => Assert.Empty(FindSquareSpacers(p)));
    }

    private static void RunLaneHysteresisScenario()
    {
        // The "text keeps running away" regression: as a photo moves across a line's center,
        // the straddling paragraph must not flip left/right on every small move - it keeps its
        // current lane until the other side is clearly better (1.25x).
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();

        // Photo at x=40, 200 wide: the right lane (764-256=508) dwarfs the left (24), so the
        // tall straddling paragraph wraps to the photo's right - the band lines get split/leading
        // spacers; the paragraph itself keeps full-width margins.
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var spacers = FindSquareSpacers(tall);
        Assert.NotEmpty(spacers);

        var session = harness.Coordinator.GetWrapSessions().First();

        // Move the photo right until the lanes nearly tie (x=250: left=234, right=244; the tie
        // point is x~255). The bigger-side rule alone would flip the text to the left lane;
        // hysteresis must keep it right (244*1.25 >= 234).
        session.DocumentLeft = 250;
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        spacers = FindSquareSpacers(tall);
        Assert.NotEmpty(spacers);

        // Past the hysteresis band the text flips to the left lane.
        session.DocumentLeft = 340;
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        var margins = Normalize(tall.Margin);
        Assert.True(margins.Right > 300 && margins.Left < 1,
            $"the text should flip to the left lane past the hysteresis band, got left={margins.Left:F1} right={margins.Right:F1}");
        Assert.Empty(FindSquareSpacers(tall));

        // Moving back within the band must keep the left lane (no thrash).
        session.DocumentLeft = 260;
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        margins = Normalize(tall.Margin);
        Assert.True(margins.Right > 300 && margins.Left < 1,
            $"moving back within the band must keep the current side, got left={margins.Left:F1} right={margins.Right:F1}");
        Assert.Empty(FindSquareSpacers(tall));
    }

    private static void RunSquareLiveStabilityScenario()
    {
        // During an image drag the Square lanes reflow in REAL TIME (the words are pushed
        // around the moving photo, like TopAndBottom): the first band line's split spacer
        // follows the photo's left edge. The gesture-end full apply settles exactly.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var session = harness.Coordinator.GetWrapSessions().First();

        for (var x = 40; x <= 240; x += 40)
        {
            session.DocumentLeft = x;
            harness.Editor.UpdateLayout();
            harness.Engine.Apply(live: true);
            harness.Editor.UpdateLayout();

            var spacers = FindSquareSpacers(tall);
            Assert.NotEmpty(spacers);
            Assert.Equal(0, Normalize(tall.Margin).Left, precision: 3);
            Assert.Equal(0, Normalize(tall.Margin).Right, precision: 3);
        }

        // The live reflow moved the first band line's split spacer right of its original
        // position (it started at ~24 with the photo at x=40; the photo ended at x=240).
        var liveSpacers = FindSquareSpacers(tall);
        var liveLeft = liveSpacers.First().ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
        Assert.True(liveLeft > 60, $"the live reflow should have pushed the split right (left={liveLeft:F1})");

        // The gesture-end full apply settles exactly at the final position (x=240): the first
        // band line splits at the photo's left edge (224).
        harness.Engine.ScheduleApply();
        PumpDispatcher(harness.Editor, 150);
        harness.Editor.UpdateLayout();
        var settled = FindSquareSpacers(tall);
        Assert.NotEmpty(settled);
        var settledLeft = settled.First().ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
        Assert.True(Math.Abs(settledLeft - 224) < 25,
            $"the settled split should sit at the photo's left edge (left={settledLeft:F1}, expected ~224)");
    }

    private static void RunTopAndBottomScenario()
    {
        using var harness = CreateHarness(paragraphCount: 14);

        // Image at (40, 300) size 200x150 -> wrap band [290, 460]. The first paragraph
        // intersecting the band straddles its top, so the band must be reserved between
        // that paragraph's own lines with an inline spacer.
        const double wrapTop = 300 - 10;
        const double expectedBandBottom = 300 + 150 + 10;

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        // No paragraph carries a margin - the straddling paragraph is handled inline.
        var margins = AllMargins(harness.Editor.Document);

        // The band must be empty: no paragraph's text starts inside [wrapTop, bandBottom).
        // Depending on where the band top lands relative to paragraph boundaries, the engine
        // uses either the between-lines spacer (straddling paragraph) or the whole-paragraph
        // margin push (paragraph starting inside the band) - both are valid.
        var paragraphList = EnumerateAllParagraphs(harness.Editor.Document.Blocks).ToList();
        Assert.DoesNotContain(
            paragraphList,
            p =>
            {
                var top = p.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
                return top > wrapTop - 1 && top < expectedBandBottom - 1;
            });

        var hasSpacer = paragraphList.Any(p => FindSpacer(p) is not null);
        var hasPush = margins.Values.Any(m => m.Top > 0);
        Assert.True(hasSpacer || hasPush, "expected either a between-lines spacer or a top margin");

        // No side lanes.
        Assert.All(margins.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(margins.Values, m => Assert.Equal(0, m.Right, precision: 3));

        // Clear removes the spacer / margins and restores the flow.
        harness.Engine.Clear();
        Assert.DoesNotContain(paragraphList, p => FindSpacer(p) is not null);
        Assert.All(AllMargins(harness.Editor.Document).Values, m => Assert.Equal(0, m.Top, precision: 3));
    }

    private static void RunSquareTallScrolledScenario()
    {
        // Regression: the square per-line spacer enumeration compared layout-space line rects
        // against the document-space band - scrolled down, the band's own lines failed the
        // intersection check and the wrap silently stopped below the fold. TopAndBottom was
        // immune (its split search is scroll-corrected), which is why only Square broke at
        // depth. The tall paragraph's band sits well below the initial viewport here.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        // The harness's tall paragraph is only ~500px - extend it so a real scroll is possible
        // (the bug only bites below the fold).
        var tallParagraph = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        tallParagraph.Inlines.Add(new Run(
            string.Join(
                " ",
                Enumerable.Repeat(
                    "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
                    80))));
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Editor.UpdateLayout();
        ScrollEditorTo(harness.Editor, 300);
        harness.Editor.UpdateLayout();

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var spacers = FindSquareSpacers(tall);
        Assert.NotEmpty(spacers);
        // The spacers must sit on the band's own lines. The band is [290, 460] in document
        // space; with the 300px scroll the band's lines sit at layout tops roughly -10..160.
        // The pre-fix coordinate mismatch placed the spacers on the lines BELOW the band
        // (layout tops 290+), leaving the photo's own text unwrapped.
        foreach (var container in spacers)
        {
            var top = container.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
            Assert.True(
                top >= -50 && top <= 180,
                $"spacer at layout top {top:F1} should sit on the band's lines (band scrolled by 300)");
        }
    }

    private static void RunTallParagraphScenario(
        bool documentEnabled = false,
        double scrollY = 0,
        bool surfaceTransform = false,
        bool xamlRoundTrip = false,
        double fixedLineHeight = 0)
    {
        // Mirrors the user's "long text with no gaps" note: ONE tall full-width paragraph
        // (the image band falls inside its middle) followed by a short snippet paragraph.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);

        if (documentEnabled)
            harness.Editor.IsDocumentEnabled = true;
        if (surfaceTransform)
            harness.Surface.LayoutTransform = new ScaleTransform(1, 1);
        if (xamlRoundTrip)
        {
            var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.XamlPackage);
            harness.Editor.Document.Blocks.Clear();
            ms.Position = 0;
            range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            range.Load(ms, DataFormats.XamlPackage);
        }
        if (fixedLineHeight > 0)
        {
            // The editor's line-spacing feature fixes paragraph line heights; a fixed LineHeight
            // with BlockLineHeight stacking caps inline-element height in the line layout (the
            // app-only "one line" shortfall).
            foreach (var paragraph in EnumerateAllParagraphs(harness.Editor.Document.Blocks))
            {
                paragraph.LineHeight = fixedLineHeight;
                paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
        }
        if (scrollY > 0)
        {
            // The harness editor starts with scrolling disabled and the tall paragraph is only
            // ~500px - neither can actually scroll, so the scrolled variants used to run at
            // scroll 0 without exercising the offset math. Make the scroll real.
            harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var scrollTall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
            scrollTall.Inlines.Add(new Run(
                string.Join(
                    " ",
                    Enumerable.Repeat(
                        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
                        80))));
            harness.Editor.UpdateLayout();
            ScrollEditorTo(harness.Editor, scrollY);
        }

        harness.Editor.UpdateLayout();

        // Image at (40, 300) size 200x150 -> band [290, 460], entirely inside the tall paragraph.
        const double expectedBandBottom = 300 + 150 + 10;
        var originalText = GetRunsText(harness.Editor.Document);

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        // The tall paragraph stays a single paragraph. Depending on the line structure the band
        // is reserved either between lines (inline spacer) or by pushing the whole paragraph
        // below the band (single huge line with word wrap off).
        var paragraphs = EnumerateAllParagraphs(harness.Editor.Document.Blocks).ToList();
        Assert.Equal(2, paragraphs.Count); // tall paragraph + snippet
        var tall = paragraphs[0];
        var stack = FindSpacers(tall);

        var top = tall.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        if (stack.Count > 0)
        {
            // Between lines: the head stays at the top and the tail continues below the band.
            // Character rects are layout space - add the scroll back for the document-space band.
            Assert.True(top < 100, $"head should stay at the top, got {top:F1}");
            var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
            Assert.True(
                tailTop + scrollY >= expectedBandBottom - 2,
                $"tail top {tailTop:F1} should be at/after band bottom {expectedBandBottom}");
        }
        else
        {
            // Whole-paragraph fallback: the paragraph itself starts below the band.
            Assert.True(
                top + scrollY >= expectedBandBottom - 2,
                $"paragraph top {top:F1} should be at/after band bottom {expectedBandBottom}");
        }

        // The text is fully preserved (split + tail runs, no loss or duplication). Run-text
        // concatenation is used because TextRange.Text emits a separator space for the inline
        // UI container - the document itself is unchanged.
        Assert.Equal(originalText, GetRunsText(harness.Editor.Document));

        // Clear removes the spacer / margins and restores the original layout.
        harness.Engine.Clear();
        Assert.Null(FindSpacer(tall));
        Assert.Equal(originalText, GetRunsText(harness.Editor.Document));
        var restoredTop = tall.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(restoredTop < 100, $"head should return to the top after clear, got {restoredTop:F1}");
    }

    private static void RunInPlaceUpdateScenario()
    {
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var stack = FindSpacers(tall);
        Assert.NotEmpty(stack);

        // Center the band top inside its split line so the small moves below stay on the
        // same line (in-place update path) instead of crossing a line boundary.
        var session = harness.Coordinator.GetWrapSessions().First();
        var splitLine = stack[0].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        session.DocumentTop = (splitLine.Top + splitLine.Bottom) / 2 + 10; // wrapTop = DocumentTop - 10
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        stack = FindSpacers(tall);
        var headText = GetRunsTextBeforeStack(tall, stack);
        var totalHeight = stack.Sum(c => ((Border)c.Child).Height);
        var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        var stackTop = stack[0].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        var delta = Math.Min(5, ((Border)stack[0].Child).Height / 2);
        Assert.True(delta >= 2, $"move delta too small: {delta:F1}");

        // Move the image down a few px on the same line: the split must NOT move, only the
        // reserved height (the in-place update that stops the text from flashing).
        session.DocumentTop += delta;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack2 = FindSpacers(tall);
        Assert.True(stack2.Count >= stack.Count);
        var stackTop2 = stack2[0].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(
            Math.Abs(stackTop2 - stackTop) < 1,
            $"spacer position must not move on a within-line adjustment, {stackTop2:F1} vs {stackTop:F1}");
        Assert.Equal(headText, GetRunsTextBeforeStack(tall, stack2));
        var totalHeight2 = stack2.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            totalHeight2 > totalHeight + delta - 2,
            $"reserved height should grow by ~{delta:F1}, got {totalHeight2:F1} vs {totalHeight:F1}");
        var tailTop2 = GetInlineAfterStack(tall.Inlines, stack2).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(tailTop2 >= tailTop + delta - 2, $"tail should drop by ~{delta:F1}, got {tailTop2:F1} vs {tailTop:F1}");

        // Move it back up: the spacer shrinks back to its original height.
        session.DocumentTop -= delta;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack3 = FindSpacers(tall);
        var totalHeight3 = stack3.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            Math.Abs(totalHeight3 - totalHeight) < 2,
            $"reserved height should return to {totalHeight:F1}, got {totalHeight3:F1}");
        var tailTop3 = GetInlineAfterStack(tall.Inlines, stack3).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(Math.Abs(tailTop3 - tailTop) < 2, $"tail should return to {tailTop:F1}, got {tailTop3:F1}");
        Assert.Equal(headText, GetRunsTextBeforeStack(tall, stack3));
    }

    private static void RunBandMovedPastSplitScenario()
    {
        // The "intolerant near the beginning" case: the split must follow the band top when the
        // band crosses a line boundary - the head grows and the push does not keep ballooning.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var session = harness.Coordinator.GetWrapSessions().First();

        // Center the band top inside the paragraph's FIRST line - the split lands at the
        // paragraph start, so the head is empty.
        var stack = FindSpacers(tall);
        Assert.NotEmpty(stack);
        var firstLine = tall.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        session.DocumentTop = firstLine.Top + NoteWrapMath.DefaultWrapGap + 4;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        stack = FindSpacers(tall);
        Assert.Equal(string.Empty, GetRunsTextBeforeStack(tall, stack));
        var linePitch = ((Border)stack[0].Child).Height;
        var stackTop = stack[0].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        var totalHeight = stack.Sum(c => ((Border)c.Child).Height);
        var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

        // Move the band down by ~1.2 lines: the split must follow (line 1 becomes the head)
        // instead of freezing and pushing the whole paragraph further down.
        session.DocumentTop += linePitch * 1.2;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack2 = FindSpacers(tall);
        var headText2 = GetRunsTextBeforeStack(tall, stack2);
        Assert.NotEqual(string.Empty, headText2);
        var stackTop2 = stack2[0].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(
            stackTop2 > stackTop + linePitch * 0.8,
            $"spacer should follow the band down a line, {stackTop2:F1} vs {stackTop:F1}");
        var totalHeight2 = stack2.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            totalHeight2 - totalHeight < linePitch * 0.8,
            $"reserved height must not grow a full line when the head grows, {totalHeight2:F1} vs {totalHeight:F1}");
        var tailTop2 = GetInlineAfterStack(tall.Inlines, stack2).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(tailTop2 > tailTop + linePitch * 0.8, $"tail should follow the band down, {tailTop2:F1} vs {tailTop:F1}");

        // Moving back up returns to the empty head.
        session.DocumentTop -= linePitch * 1.2;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        var stack3 = FindSpacers(tall);
        Assert.Equal(string.Empty, GetRunsTextBeforeStack(tall, stack3));
    }

    private static void RunTwoBandsShareOneSpacerScenario()
    {
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        // Both bands must fall inside the tall paragraph's natural extent (~17..500).
        var secondId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 350));
        Assert.NotNull(secondId);
        harness.Coordinator.SetWrapMode(secondId!.Value, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var stack = FindSpacers(tall);
        Assert.NotEmpty(stack);
        Assert.Single(stack.Select(c => c.Parent).Distinct()); // one shared stack

        // The stack covers the deepest band bottom (350 + 150 + wrap gap).
        var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(tailTop >= 350 + 150 + NoteWrapMath.DefaultWrapGap - 2, $"tail should clear the deepest band bottom, got {tailTop:F1}");

        // Moving only the deeper band keeps the shared split (the head) untouched and just
        // extends the stack.
        var deep = harness.Coordinator.GetWrapSessions().First(s => s.DocumentTop > 320);
        var headBefore = GetRunsTextBeforeStack(tall, stack);
        var heightBefore = stack.Sum(c => ((Border)c.Child).Height);
        deep.DocumentTop += 5;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack2 = FindSpacers(tall);
        Assert.Single(stack2.Select(c => c.Parent).Distinct());
        Assert.Equal(headBefore, GetRunsTextBeforeStack(tall, stack2));
        Assert.True(
            stack2.Sum(c => ((Border)c.Child).Height) > heightBefore + 3,
            "the shared stack should grow when the deeper band moves down");
    }

    private static void RunLiveApplyScenario()
    {
        // Word-style: while an image is dragged, the wrap refreshes in real time - the first
        // signal applies immediately (leading edge) and later signals are throttled so the
        // layout does not re-run on every mouse move.
        using var harness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();

        harness.Engine.ScheduleApplyLive();
        harness.Editor.UpdateLayout();
        var tall = EnumerateAllParagraphs(harness.Editor.Document.Blocks).First();
        var stack = FindSpacers(tall);
        Assert.NotEmpty(stack);
        var heightBefore = stack.Sum(c => ((Border)c.Child).Height);

        // Continuous geometry signals are throttled: nothing applies until the interval elapses.
        var session = harness.Coordinator.GetWrapSessions().First();
        session.DocumentTop += 8;
        harness.Engine.ScheduleApplyLive();
        harness.Editor.UpdateLayout();
        var stackNow = FindSpacers(tall);
        Assert.Equal(heightBefore, stackNow.Sum(c => ((Border)c.Child).Height));

        // After the throttle interval the layout catches up with the new geometry.
        PumpDispatcher(harness.Editor, 80);
        harness.Editor.UpdateLayout();
        var stackAfter = FindSpacers(tall);
        var heightAfter = stackAfter.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            Math.Abs(heightAfter - heightBefore) > 3,
            $"the live throttle should refresh the layout to the new band, got {heightAfter:F1} vs {heightBefore:F1}");

        // The gesture end (ScheduleApply) still delivers the final state.
        session.DocumentTop -= 8;
        harness.Engine.ScheduleApply();
        PumpDispatcher(harness.Editor, 150);
        harness.Editor.UpdateLayout();
        var stackEnd = FindSpacers(tall);
        var stackEndHeight = stackEnd.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            Math.Abs(stackEndHeight - heightBefore) < 2,
            $"final layout should match the original band, got {stackEndHeight:F1} vs {heightBefore:F1}");

        // The live path (throttled ticks) settles toward the moved band over a couple of
        // convergence passes without the full render flush - drive it directly and verify
        // it still moves the wrap. (stackEndHeight is captured before the move: the live
        // resize mutates the same container objects in place.)
        session.DocumentTop += 5;
        harness.Engine.Apply(live: true);
        harness.Engine.Apply(live: true);
        PumpDispatcher(harness.Editor, 60);
        harness.Editor.UpdateLayout();
        var stackLive = FindSpacers(tall);
        var liveHeight = stackLive.Sum(c => ((Border)c.Child).Height);
        Assert.True(
            Math.Abs(liveHeight - stackEndHeight) > 3,
            $"the live path should move the wrap toward the moved band, got {liveHeight:F1} vs {stackEndHeight:F1}");

        // The gesture end settles the final state exactly.
        session.DocumentTop -= 5;
        harness.Engine.ScheduleApply();
        PumpDispatcher(harness.Editor, 150);
        harness.Editor.UpdateLayout();
        var stackRestored = FindSpacers(tall);
        Assert.True(
            Math.Abs(stackRestored.Sum(c => ((Border)c.Child).Height) - stackEndHeight) < 2,
            $"the final full pass should restore the band, got {stackRestored.Sum(c => ((Border)c.Child).Height):F1} vs {stackEndHeight:F1}");
    }

    private static void RunFirstBlockPushIdleReapplyScenario()
    {
        // The push-spacer geometry: the band covers the document's first block start, so the
        // wrap reserves the push with a transient block spacer. Every full pass used to remove
        // and re-insert that spacer (block mutations firing TextChanged), keeping the note
        // perpetually "edited" - the settled re-apply must be a no-op.
        using var harness = CreateHarness(paragraphCount: 14);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Coordinator.GetWrapSessions().First().DocumentTop = 0;
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var eventCount = 0;
        harness.Editor.TextChanged += (_, _) => eventCount++;

        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        Assert.Equal(0, eventCount);
    }

    private static void RunIdleReapplyStabilityScenario(NoteImageWrapMode mode)
    {
        // Regression: with the wrap settled, a re-apply must be a no-op. Before the fix the
        // spacer rebuild (Square) or the push-spacer reset cycle (TopAndBottom) fired TextChanged
        // per event on every pass - which re-scheduled the pass forever, keeping the note
        // perpetually marked "edited" and churning CPU at rest.
        using var harness = CreateHarness(paragraphCount: 14);
        harness.Coordinator.SetWrapMode(harness.ImageId, mode);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var eventCount = 0;
        harness.Editor.TextChanged += (_, _) => eventCount++;

        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        Assert.Equal(0, eventCount);
    }

    private static void RunCaptureRestoreScenario()
    {
        // Square per-line spacers: capture -> Clear -> restore must reproduce the exact wrap
        // (spacer counts and widths) without a recompute.
        using var harness = CreateHarness(paragraphCount: 14);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var paragraphs = EnumerateAllParagraphs(harness.Editor.Document.Blocks).ToList();
        var marginsBefore = AllMargins(harness.Editor.Document);
        var spacersBefore = paragraphs.ToDictionary(
            p => p,
            p => FindSquareSpacers(p).Select(c => ((Border)c.Child).Width).ToList());
        Assert.True(spacersBefore.Values.Any(l => l.Count > 0), "square wrap should place line spacers");

        var snapshot = harness.Engine.CaptureWrapState();
        Assert.NotNull(snapshot);

        harness.Engine.Clear();
        var marginsCleared = AllMargins(harness.Editor.Document);
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Right, precision: 3));
        Assert.All(paragraphs, p => Assert.Empty(FindSquareSpacers(p)));

        Assert.True(harness.Engine.RestoreWrapState(snapshot), "restore should report restored state");
        harness.Editor.UpdateLayout();

        foreach (var paragraph in paragraphs)
        {
            var beforeWidths = spacersBefore[paragraph];
            var afterWidths = FindSquareSpacers(paragraph).Select(c => ((Border)c.Child).Width).ToList();
            Assert.Equal(beforeWidths.Count, afterWidths.Count);
            for (var i = 0; i < beforeWidths.Count; i++)
            {
                Assert.True(Math.Abs(beforeWidths[i] - afterWidths[i]) < 0.5,
                    $"spacer {i} width {afterWidths[i]:F1} should match captured {beforeWidths[i]:F1}");
            }
        }

        var marginsAfter = AllMargins(harness.Editor.Document);
        foreach (var (paragraph, before) in marginsBefore)
        {
            Assert.True(
                Math.Abs(Normalize(before).Left - Normalize(marginsAfter[paragraph]).Left) < 0.5
                && Math.Abs(Normalize(before).Right - Normalize(marginsAfter[paragraph]).Right) < 0.5,
                $"margin of a wrapped paragraph should match the captured state");
        }

        // TopAndBottom straddling inline stacks restore the same way (anchor re-insertion).
        using var straddleHarness = CreateHarness(paragraphCount: 0, tallParagraph: true);
        straddleHarness.Coordinator.SetWrapMode(straddleHarness.ImageId, NoteImageWrapMode.TopAndBottom);
        straddleHarness.Editor.UpdateLayout();
        straddleHarness.Engine.Apply();
        straddleHarness.Editor.UpdateLayout();

        var straddler = EnumerateAllParagraphs(straddleHarness.Editor.Document.Blocks).First();
        var stack = FindSpacers(straddler);
        Assert.NotEmpty(stack);
        var stackBefore = stack.Sum(c => ((Border)c.Child).Height);

        var straddleSnapshot = straddleHarness.Engine.CaptureWrapState();
        Assert.NotNull(straddleSnapshot);

        straddleHarness.Engine.Clear();
        straddleHarness.Editor.UpdateLayout();
        Assert.Empty(FindSpacers(straddler));

        Assert.True(straddleHarness.Engine.RestoreWrapState(straddleSnapshot), "inline stack restore should report restored");
        straddleHarness.Editor.UpdateLayout();
        var stackAfter = FindSpacers(straddler).Sum(c => ((Border)c.Child).Height);
        Assert.True(Math.Abs(stackBefore - stackAfter) < 2,
            $"inline stack {stackAfter:F1} should match captured {stackBefore:F1}");
    }

    private static void PumpDispatcher(RichTextBox editor, double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunListItemSplitScenario()
    {
        using var harness = CreateHarness(paragraphCount: 1);
        harness.Editor.Document.Blocks.Clear();
        var list = new List();
        var item = new ListItem();
        var paragraph = new Paragraph(new Run(RepeatText("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 60)));
        item.Blocks.Add(paragraph);
        list.ListItems.Add(item);
        harness.Editor.Document.Blocks.Add(list);
        harness.Editor.UpdateLayout();

        // Image at (40, 300) size 200x150 -> band [290, 460] inside the list paragraph.
        const double expectedBandBottom = 300 + 150 + 10;

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack = FindSpacers(paragraph);
        Assert.NotEmpty(stack);

        var tailTop = GetInlineAfterStack(paragraph.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(
            tailTop >= expectedBandBottom - 2,
            $"tail top {tailTop:F1} should be at/after band bottom {expectedBandBottom}");

        // Still a single list item with a single paragraph.
        Assert.Single(list.ListItems);
        Assert.Single(item.Blocks);

        harness.Engine.Clear();
        Assert.Null(FindSpacer(paragraph));
    }

    private static void RunTableCellFallbackScenario()
    {
        using var harness = CreateHarness(paragraphCount: 1);
        harness.Editor.Document.Blocks.Clear();
        var table = new Table();
        var rowGroup = new TableRowGroup();
        var row = new TableRow();
        var cell = new TableCell();
        var paragraph = new Paragraph(new Run(RepeatText("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 60)));
        cell.Blocks.Add(paragraph);
        row.Cells.Add(cell);
        rowGroup.Rows.Add(row);
        table.RowGroups.Add(rowGroup);
        harness.Editor.Document.Blocks.Add(table);
        harness.Editor.UpdateLayout();

        // Image at (40, 300) size 200x150 -> band [290, 460] inside the cell paragraph.
        // Word does not split tables, so the whole cell paragraph is pushed instead.
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        Assert.Null(FindSpacer(paragraph));
        Assert.True(
            paragraph.Margin.Top > 0,
            $"cell paragraph should be pushed with a top margin, got {paragraph.Margin.Top:F1}");

        harness.Engine.Clear();
        Assert.Equal(0, Normalize(paragraph.Margin).Top);
    }

    private static void RunCoexistenceScenario()
    {
        using var harness = CreateHarness(paragraphCount: 18);

        // Image A: Square at (40, 300) 200x150 -> band [290, 460], right lane inset 250.
        var squareId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 300));
        Assert.NotNull(squareId);
        harness.Coordinator.SetWrapMode(squareId!.Value, NoteImageWrapMode.Square);

        // Image B: TopAndBottom at (40, 600) 200x150 -> band [590, 760].
        var topBottomId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 600));
        Assert.NotNull(topBottomId);
        harness.Coordinator.SetWrapMode(topBottomId!.Value, NoteImageWrapMode.TopAndBottom);

        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var margins = AllMargins(harness.Editor.Document);

        // Image A (Square): its band's lines carry transient split/leading spacers, not margins.
        var squareWrapped = EnumerateAllParagraphs(harness.Editor.Document.Blocks)
            .Where(p => FindSquareSpacers(p).Count > 0)
            .ToList();
        Assert.True(squareWrapped.Count >= 2, $"expected >= 2 square-wrapped paragraphs, got {squareWrapped.Count}");
        Assert.All(
            squareWrapped.SelectMany(FindSquareSpacers).Select(c => ((Border)c.Child).Width),
            w => Assert.True(w > 0 && w <= 272, $"spacer width {w:F1} out of range"));
        Assert.DoesNotContain(margins.Values, m => Math.Abs(m.Left - 256) < 0.5);

        // Image B's straddling paragraph is handled by the between-lines inline spacer,
        // so no paragraph carries a top margin.
        Assert.DoesNotContain(margins.Values, m => m.Top > 0);

        // The straddling paragraph hosts the spacer; its tail continues below B's band.
        var spacerHost = EnumerateAllParagraphs(harness.Editor.Document.Blocks)
            .FirstOrDefault(p => FindSpacer(p) is not null);
        Assert.NotNull(spacerHost);
        var stack = FindSpacers(spacerHost!);
        Assert.NotEmpty(stack);
        var tailTop = GetInlineAfterStack(spacerHost!.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(tailTop >= 764, $"spacer tail top {tailTop:F1} should be at/after band bottom 766");

        // The spacer's paragraph must not carry a side lane.
        Assert.Equal(0, margins[spacerHost!].Left);

        // Clear restores everything.
        harness.Engine.Clear();
        var marginsCleared = AllMargins(harness.Editor.Document);
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Top, precision: 3));
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Right, precision: 3));
    }

    private sealed class EditorHarness : IDisposable
    {
        public required Window Window { get; init; }
        public required RichTextBox Editor { get; init; }
        public required FrameworkElement Surface { get; init; }
        public required FloatingImageCoordinator Coordinator { get; init; }
        public required NoteWrapLayoutEngine Engine { get; init; }
        public required NoteImageLoader.LoadResult Load { get; init; }
        public required Guid ImageId { get; init; }

        public void Dispose()
        {
            Engine.Dispose();
            Coordinator.Detach();
            Window.Close();
        }
    }

    private static EditorHarness CreateHarness(int paragraphCount, bool tallParagraph = false)
    {
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Left = -10000,
            Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        var host = new Grid();
        window.Content = host;

        var editor = new RichTextBox
        {
            Padding = new Thickness(18, 16, 18, 16),
            FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        editor.Document.Blocks.Clear();
        if (tallParagraph)
        {
            // One tall full-width paragraph (many wrapped lines) + a short snippet below.
            var longText = string.Join(
                " ",
                Enumerable.Repeat(
                    "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
                    24));
            editor.Document.Blocks.Add(new Paragraph(new Run(longText)));
            editor.Document.Blocks.Add(new Paragraph(new Run("Short snippet at the bottom.")));
        }
        else
        {
            for (var i = 0; i < paragraphCount; i++)
            {
                editor.Document.Blocks.Add(new Paragraph(
                    new Run(
                        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod " +
                        "tempor incididunt ut labore et dolore magna aliqua. Paragraph " + i + ".")));
            }
        }

        var overlay = new Canvas();
        host.Children.Add(editor);
        host.Children.Add(overlay);

        var coordinator = new FloatingImageCoordinator();
        coordinator.Attach(overlay, editor, host);
        var engine = new NoteWrapLayoutEngine(editor, host, coordinator);

        window.Show();
        editor.UpdateLayout();

        var bytes = File.ReadAllBytes(FixturePath);
        Assert.True(NoteImageLoader.TryLoadFromBytes(bytes, 200, 1, out var load, out var error), error);
        Assert.NotNull(load);

        var imageId = coordinator.Insert(load!, 200, 150, new Point(40, 300));
        Assert.NotNull(imageId);

        return new EditorHarness
        {
            Window = window,
            Editor = editor,
            Surface = host,
            Coordinator = coordinator,
            Engine = engine,
            Load = load!,
            ImageId = imageId!.Value
        };
    }

    private static void ScrollEditorTo(RichTextBox editor, double offsetY)
    {
        var scrollViewer = FindScrollViewer(editor);
        Assert.NotNull(scrollViewer);
        scrollViewer!.ScrollToVerticalOffset(offsetY);
        editor.UpdateLayout();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private static Dictionary<Paragraph, Thickness> AllMargins(FlowDocument document)
    {
        var result = new Dictionary<Paragraph, Thickness>();
        foreach (Block block in document.Blocks)
        {
            if (block is Paragraph paragraph)
                result[paragraph] = Normalize(paragraph.Margin);
        }

        return result;
    }

    private static Thickness Normalize(Thickness margin)
        => new(
            double.IsNaN(margin.Left) ? 0 : margin.Left,
            double.IsNaN(margin.Top) ? 0 : margin.Top,
            double.IsNaN(margin.Right) ? 0 : margin.Right,
            double.IsNaN(margin.Bottom) ? 0 : margin.Bottom);
}
