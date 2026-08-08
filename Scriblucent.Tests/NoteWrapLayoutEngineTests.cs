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

    /// <summary>The engine's transient inline spacer stack inside a paragraph, if present.</summary>
    private static List<InlineUIContainer> FindSpacers(Paragraph paragraph)
    {
        var found = new List<InlineUIContainer>();
        foreach (Inline inline in paragraph.Inlines)
        {
            if (inline is InlineUIContainer { Child: Border })
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

    private static void RunSquareScenario()
    {
        using var harness = CreateHarness(paragraphCount: 14);

        // No wrap: nothing may change.
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        var marginsBefore = AllMargins(harness.Editor.Document);
        Assert.All(marginsBefore.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsBefore.Values, m => Assert.Equal(0, m.Right, precision: 3));

        // Square wrap: paragraphs in the image's vertical band get pushed to the larger
        // (right) lane - left inset = image right edge + gap = 40 + 200 + 10.
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var marginsAfter = AllMargins(harness.Editor.Document);
        var wrapped = marginsAfter
            .Where(kv => kv.Value.Left > 0 || kv.Value.Right > 0)
            .ToList();
        Assert.True(wrapped.Count >= 2, $"expected >= 2 wrapped paragraphs, got {wrapped.Count}");
        Assert.All(wrapped, kv => Assert.Equal(250, kv.Value.Left, precision: 3));
        Assert.All(wrapped, kv => Assert.Equal(0, kv.Value.Right, precision: 3));

        // Paragraphs far above and below the image keep full width.
        Assert.Equal(0, marginsAfter.First().Value.Left);
        Assert.Equal(0, marginsAfter.First().Value.Right);
        Assert.Equal(0, marginsAfter.Last().Value.Left);
        Assert.Equal(0, marginsAfter.Last().Value.Right);

        // Clear restores every margin exactly.
        harness.Engine.Clear();
        var marginsCleared = AllMargins(harness.Editor.Document);
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsCleared.Values, m => Assert.Equal(0, m.Right, precision: 3));

        // Disabling wrap also clears.
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.None);
        harness.Engine.Apply();
        var marginsOff = AllMargins(harness.Editor.Document);
        Assert.All(marginsOff.Values, m => Assert.Equal(0, m.Left, precision: 3));
        Assert.All(marginsOff.Values, m => Assert.Equal(0, m.Right, precision: 3));
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
            ScrollEditorTo(harness.Editor, scrollY);

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
            Assert.True(top < 100, $"head should stay at the top, got {top:F1}");
            var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
            Assert.True(
                tailTop >= expectedBandBottom - 2,
                $"tail top {tailTop:F1} should be at/after band bottom {expectedBandBottom}");
        }
        else
        {
            // Whole-paragraph fallback: the paragraph itself starts below the band.
            Assert.True(
                top >= expectedBandBottom - 2,
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
        session.DocumentTop = (firstLine.Top + firstLine.Bottom) / 2 + 10;
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
        Assert.Equal(1, stack.Select(c => c.Parent).Distinct().Count()); // one shared stack

        // The stack covers the deepest band bottom (350 + 150 + 10).
        var tailTop = GetInlineAfterStack(tall.Inlines, stack).ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        Assert.True(tailTop >= 510 - 2, $"tail should clear the deepest band bottom, got {tailTop:F1}");

        // Moving only the deeper band keeps the shared split (the head) untouched and just
        // extends the stack.
        var deep = harness.Coordinator.GetWrapSessions().First(s => s.DocumentTop > 320);
        var headBefore = GetRunsTextBeforeStack(tall, stack);
        var heightBefore = stack.Sum(c => ((Border)c.Child).Height);
        deep.DocumentTop += 5;
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();

        var stack2 = FindSpacers(tall);
        Assert.Equal(1, stack2.Select(c => c.Parent).Distinct().Count());
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
        session.DocumentTop += 5;
        harness.Engine.ScheduleApplyLive();
        harness.Editor.UpdateLayout();
        var stackNow = FindSpacers(tall);
        Assert.Equal(heightBefore, stackNow.Sum(c => ((Border)c.Child).Height));

        // After the throttle interval the layout catches up with the new geometry.
        PumpDispatcher(harness.Editor, 80);
        harness.Editor.UpdateLayout();
        var stackAfter = FindSpacers(tall);
        Assert.True(
            stackAfter.Sum(c => ((Border)c.Child).Height) > heightBefore + 3,
            "the live throttle should refresh the layout to the new band");

        // The gesture end (ScheduleApply) still delivers the final state.
        session.DocumentTop -= 5;
        harness.Engine.ScheduleApply();
        PumpDispatcher(harness.Editor, 150);
        harness.Editor.UpdateLayout();
        var stackEnd = FindSpacers(tall);
        Assert.True(
            Math.Abs(stackEnd.Sum(c => ((Border)c.Child).Height) - heightBefore) < 2,
            $"final layout should match the original band, got {stackEnd.Sum(c => ((Border)c.Child).Height):F1} vs {heightBefore:F1}");
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

        var squareWrapped = margins
            .Where(kv => Math.Abs(kv.Value.Left - 250) < 0.5)
            .ToList();
        Assert.True(squareWrapped.Count >= 2, $"expected >= 2 square-wrapped paragraphs, got {squareWrapped.Count}");

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
        Assert.True(tailTop >= 758, $"spacer tail top {tailTop:F1} should be at/after band bottom 760");

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
