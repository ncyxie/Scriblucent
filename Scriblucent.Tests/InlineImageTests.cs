using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;
using Xunit;

namespace Scriblucent.Tests;

/// <summary>
/// Headless tests for the "in line with text" image wrap mode: conversion between the canvas
/// overlay and the text flow, drag re-anchoring, resize, delete, and the save/load round trip
/// through text markers.
/// </summary>
public class InlineImageTests
{
    private static string FixturePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "Images", "pixel.png");

    [Fact]
    public void FloatingToInline_MovesElementIntoFlow()
        => RunOnSta(RunFloatingToInlineScenario);

    [Fact]
    public void InlineToFloating_CreatesWidgetAndRemovesElement()
        => RunOnSta(RunInlineToFloatingScenario);

    [Fact]
    public void MoveInlineAnchor_ReanchorsInAnotherParagraph()
        => RunOnSta(RunReanchorScenario);

    [Fact]
    public void InlinePhotoClick_ParksCaretAfterThePhoto()
        => RunOnSta(RunInlineSelectCaretParkScenario);

    [Fact]
    public void InlineCaretPlacement_LeftBefore_RightAfter()
        => RunOnSta(RunInlineCaretSideScenario);

    private static void RunInlineSelectCaretParkScenario()
    {
        // Selecting an inline photo must park the caret deterministically RIGHT AFTER the
        // photo (visible, and typing continues after it) - not before it, where the user
        // cannot tell where the caret will land.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var container = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(container);
        var element = container.Child as InlineNoteImageElement;
        Assert.NotNull(element);

        var photoImage = FindVisualChildren<System.Windows.Controls.Image>(element!).First();
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = photoImage
        });
        harness.Editor.UpdateLayout();

        Assert.True(element.IsSelected);
        Assert.True(
            container.ContentEnd.GetOffsetToPosition(harness.Editor.CaretPosition) <= 1,
            "the caret must sit right after the photo");
        Assert.True(
            harness.Editor.CaretPosition.CompareTo(container.ContentStart) > 0,
            "the caret must not sit before the photo");
    }

    private static void RunInlineCaretSideScenario()
    {
        // Clicking the TEXT directly beside an inline photo must place the caret predictably:
        // left of the photo -> before it, right of the photo -> after it.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var container = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(container);
        var element = container.Child as InlineNoteImageElement;
        Assert.NotNull(element);

        var rect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        Assert.False(rect.IsEmpty);
        var midY = rect.Top + rect.Height / 2;
        var photoWidth = Math.Max(1, element.ActualWidth);

        var left = harness.Editor.GetPositionFromPoint(new Point(rect.Left - 6, midY), snapToText: true);
        var right = harness.Editor.GetPositionFromPoint(new Point(rect.Left + photoWidth + 10, midY), snapToText: true);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.True(left.CompareTo(container.ContentStart) < 0, "click left of the photo must give the caret before it");
        Assert.True(right.CompareTo(container.ContentEnd) >= 0, "click right of the photo must give the caret after it");
    }

    [Fact]
    public void InlineInput_PhotoBodyHit_ReachesElementNotChrome()
        => RunOnSta(RunPhotoBodyHitProbe);

    [Fact]
    public void InlineInput_RightClickTarget_ResolvesToPhoto()
        => RunOnSta(RunRightClickTargetProbe);

    [Fact]
    public void InlineDrag_ReanchorParksCaretVisiblyAfterPhoto()
        => RunOnSta(RunInlineDragCaretParkProbe);

    private static void RunInlineDragCaretParkProbe()
    {
        // After a re-anchor drag the caret is re-parked right after the moved photo, so
        // typing continues deterministically after it (the same convention as photo
        // selection) - never stranded at the pre-move drop position.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var container = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(container);
        var element = container.Child as InlineNoteImageElement;
        Assert.NotNull(element);
        var originalParagraph = GetContainerParagraph(container!);

        // Drop the photo into the snippet paragraph.
        var target = FindParagraphs(harness.Editor.Document)
            .First(p => !ReferenceEquals(p, originalParagraph));
        var dropPosition = target.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        harness.Coordinator.CompleteInlineDrag(harness.ImageId, dropPosition);
        harness.Editor.UpdateLayout();

        var movedContainer = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(movedContainer);
        Assert.True(
            ReferenceEquals(GetContainerParagraph(movedContainer!), target),
            "the container should move into the target paragraph");

        // The photo stays selected and the caret is parked right after it.
        Assert.True(harness.Coordinator.HasSelection);
        Assert.Equal(harness.ImageId, harness.Coordinator.SelectedImageId);
        var caret = harness.Editor.CaretPosition;
        Assert.True(
            caret.CompareTo(movedContainer!.ContentEnd) >= 0,
            "the caret must sit at or after the moved photo");
        var caretRect = caret.GetCharacterRect(LogicalDirection.Forward);
        Assert.False(caretRect.IsEmpty, "the parked caret must have a renderable position (visible)");
    }

    [Fact]
    public void InlineDrag_DropIndicator_PositionsAtDropRect()
        => RunOnSta(RunDropIndicatorPositionProbe);

    [Fact]
    public void InlineDrag_DropIndicator_EndOfDocument_LandsAtParagraphMark()
        => RunOnSta(RunDropIndicatorEndOfDocumentProbe);

    [Fact]
    public void InlineDrag_EdgeAutoScroll_ScrollsTowardPointer()
        => RunOnSta(RunEdgeAutoScrollProbe);

    [Fact]
    public void InlineDrag_EdgeAutoScroll_IndicatorTracksPointer()
        => RunOnSta(RunEdgeScrollIndicatorProbe);

    private static void RunEdgeAutoScrollProbe()
    {
        // The re-anchor drag auto-scrolls when the pointer nears a viewport edge (it no longer
        // depends on the native caret's scroll-follow, which needs editor focus).
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        for (var i = 0; i < 40; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        Assert.True(sv.ScrollableHeight > 200, $"the document must be scrollable, got {sv.ScrollableHeight:F0}");
        sv.ScrollToVerticalOffset(sv.ScrollableHeight / 2);
        harness.Editor.UpdateLayout();
        var midOffset = sv.VerticalOffset;
        Assert.True(midOffset > 0, "the view must sit mid-scroll");

        // Pointer deep in the bottom edge band: the view must scroll downward.
        var bottomPoint = new Point(sv.ViewportWidth / 2, sv.ViewportHeight - 3);
        harness.Coordinator.TryEdgeAutoScroll(bottomPoint);
        harness.Editor.UpdateLayout();
        var afterBottom = sv.VerticalOffset;
        Assert.True(
            afterBottom > midOffset,
            $"the view must scroll down near the bottom edge, got {afterBottom:F0} (was {midOffset:F0})");

        // Pointer deep in the top edge band: the view scrolls back up.
        var topPoint = new Point(sv.ViewportWidth / 2, 3);
        harness.Coordinator.TryEdgeAutoScroll(topPoint);
        harness.Editor.UpdateLayout();
        Assert.True(
            sv.VerticalOffset < afterBottom,
            $"the view must scroll up near the top edge, got {sv.VerticalOffset:F0} (was {afterBottom:F0})");
    }

    private static void RunEdgeScrollIndicatorProbe()
    {
        // After an edge auto-scroll the drop position is re-resolved under the (stationary)
        // pointer - the drop indicator must sit on the pointer's row.
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        for (var i = 0; i < 40; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        sv.ScrollToVerticalOffset(sv.ScrollableHeight / 2);
        harness.Editor.UpdateLayout();

        var point = new Point(sv.ViewportWidth / 2, sv.ViewportHeight - 3);
        harness.Coordinator.TryEdgeAutoScroll(point);
        harness.Editor.UpdateLayout();

        var position = harness.Editor.GetPositionFromPoint(point, snapToText: true);
        Assert.NotNull(position);
        harness.Coordinator.ShowInlineDropIndicator(position!);
        harness.Editor.UpdateLayout();

        var indicator = FindVisualChildren<Border>(harness.Overlay)
            .FirstOrDefault(b => b.Name == "InlineDropIndicator");
        Assert.NotNull(indicator);
        Assert.Equal(Visibility.Visible, indicator!.Visibility);
        var indicatorTop = Canvas.GetTop(indicator);
        Assert.True(
            Math.Abs(indicatorTop - point.Y) < 40,
            $"the indicator must sit on the pointer's row, got {indicatorTop:F0} vs pointer {point.Y:F0}");
    }

    private static void RunDropIndicatorPositionProbe()
    {
        // The drag's drop indicator is a caret-like accent bar on the OVERLAY - it must sit
        // exactly where the photo will land (its character slot), never depend on the editor's
        // focus or native caret, and never intercept clicks.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        var container = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(container);

        // The slot BEFORE the photo: the indicator must land at the photo's left edge.
        var position = container!.ContentStart;
        harness.Coordinator.ShowInlineDropIndicator(position);
        harness.Editor.UpdateLayout();

        var indicator = FindVisualChildren<Border>(harness.Overlay)
            .FirstOrDefault(b => b.Name == "InlineDropIndicator");
        Assert.NotNull(indicator);
        Assert.Equal(Visibility.Visible, indicator!.Visibility);
        Assert.False(indicator.IsHitTestVisible, "the indicator must never intercept clicks");

        // The character rect is already in the editor's coordinate space (padding included),
        // and the overlay shares the editor's origin - the indicator lands on the rect.
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        Assert.False(rect.IsEmpty, "the photo's slot must have a character rect");
        Assert.Equal(rect.Left, Canvas.GetLeft(indicator), precision: 1);
        Assert.Equal(rect.Top, Canvas.GetTop(indicator), precision: 1);
        Assert.Equal(rect.Height, indicator.Height, precision: 1);

        // Cross-check the coordinate space against a known on-screen anchor: the photo's own
        // box in the editor.
        var photoOrigin = element.TransformToAncestor(harness.Editor).Transform(new Point(0, 0));
        Assert.Equal(photoOrigin.X, Canvas.GetLeft(indicator), precision: 1);
        Assert.Equal(photoOrigin.Y, Canvas.GetTop(indicator), precision: 1);

        harness.Coordinator.HideInlineDropIndicator();
        Assert.Equal(Visibility.Collapsed, indicator.Visibility);
    }

    private static void RunDropIndicatorEndOfDocumentProbe()
    {
        // Dragging to the very end of the note: the trailing paragraph mark still provides a
        // forward character rect, so the indicator lands exactly where the photo would drop.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var position = harness.Editor.Document.ContentEnd;
        harness.Coordinator.ShowInlineDropIndicator(position);
        harness.Editor.UpdateLayout();

        var indicator = FindVisualChildren<Border>(harness.Overlay)
            .FirstOrDefault(b => b.Name == "InlineDropIndicator");
        Assert.NotNull(indicator);
        Assert.Equal(Visibility.Visible, indicator!.Visibility);

        var forward = position.GetCharacterRect(LogicalDirection.Forward);
        Assert.False(forward.IsEmpty, "the document end must still render the paragraph mark");
        Assert.Equal(forward.Left, Canvas.GetLeft(indicator), precision: 1);
        Assert.Equal(forward.Top, Canvas.GetTop(indicator), precision: 1);

        harness.Coordinator.HideInlineDropIndicator();
        Assert.Equal(Visibility.Collapsed, indicator.Visibility);
    }

    private static void RunRightClickTargetProbe()
    {
        // A right-click over the photo must resolve to the photo's element (the coordinator
        // redirects the editor's context-menu event to it); clicks on plain text resolve to
        // nothing.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        _ = SelectInlineAndGetChrome(harness);

        var center = element.TransformToAncestor(harness.Editor)
            .Transform(new Point(element.Width / 2, element.Height / 2));
        var resolved = harness.Coordinator.FindInlineElementAt(center);
        Assert.True(ReferenceEquals(element, resolved), "a right-click over the photo must resolve to the photo");

        Assert.Null(harness.Coordinator.FindInlineElementAt(new Point(5, 5)));
    }

    private static void RunPhotoBodyHitProbe()
    {
        // The overlay selection chrome sits ABOVE the photo while it is selected (auto-selected
        // on conversion). Its surface must be click-through: a click on the photo body must
        // reach the editor below (the editor routes it into the flow), NOT the chrome - while
        // the corner resize handles stay interactive. The probe walks the real hit-test path
        // from the window (the editor and the overlay are siblings).
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        var chrome = SelectInlineAndGetChrome(harness);
        Assert.Equal(Visibility.Visible, chrome.Visibility);

        // A grid across the photo: the clicks must pass through the chrome, and NOT ONE point
        // may resolve to the flow element itself - the surrounding text visuals cover the line,
        // so the element can never receive real OS input (the coordinator drives its
        // interaction at the editor instead).
        for (var gy = 0; gy <= 3; gy++)
        {
            for (var gx = 0; gx <= 3; gx++)
            {
                var px = 8 + gx * ((element.Width - 16) / 3);
                var py = 8 + gy * ((element.Height - 16) / 3);
                var pointInWindow = element.TransformToAncestor(harness.Window)
                    .Transform(new Point(px, py));
                var gridHit = harness.Window.InputHitTest(pointInWindow) as DependencyObject;
                Assert.NotNull(gridHit);
                Assert.False(
                    gridHit is InlineImageChrome,
                    $"photo point ({px:F0},{py:F0}) must not be swallowed by the selection chrome");
                Assert.True(
                    FindAncestor<InlineNoteImageElement>(gridHit) is null,
                    $"photo point ({px:F0},{py:F0}) must not resolve to the flow element (got {gridHit.GetType().Name})");
            }
        }

        // The corner resize handles must stay interactive THROUGH the chrome (their templates
        // carry their own transparent hit areas, so they work without the app's theme resources).
        var cornerInWindow = element.TransformToAncestor(harness.Window)
            .Transform(new Point(element.Width - 6, element.Height - 6));
        var handleHit = harness.Window.InputHitTest(cornerInWindow) as DependencyObject;
        Assert.NotNull(handleHit);
        Assert.NotNull(FindAncestor<InlineImageChrome>(handleHit));
    }

    [Fact]
    public void InlineResize_MultipleDeltaEvents_NoAmplification()
        => RunOnSta(RunMultiEventResizeProbe);

    private static void RunMultiEventResizeProbe()
    {
        // A real drag emits MANY DragDelta events. WPF's Thumb reports deltas relative to its
        // own position: axes where the handle tracks the growth (right/bottom) report
        // INCREMENTS, axes the flow pins (left/top - the thumb never moves) report TOTALS
        // since drag start. Both must end up 1:1 with the true mouse movement.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        element.LockAspectRatio = false;
        var chrome = SelectInlineAndGetChrome(harness);

        // BR: both axes track -> incremental reports (2, 3, 5 = 10px total).
        var thumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, "BR"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(2, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(3, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(5, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

        // 10px total drag -> exactly 10px growth, never more.
        Assert.Equal(210, element.Width, precision: 3);
        Assert.Equal(150, element.Height, precision: 3);

        // BL: the X axis is flow-pinned, so the reports are TOTALS (-3, -5, -10 for a 10px
        // leftward drag) - accumulating them would compound ("resizes like crazy").
        var leftThumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, "BL"));
        leftThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        leftThumb.RaiseEvent(new DragDeltaEventArgs(-3, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        leftThumb.RaiseEvent(new DragDeltaEventArgs(-5, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        leftThumb.RaiseEvent(new DragDeltaEventArgs(-10, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        leftThumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

        Assert.Equal(220, element.Width, precision: 3);
        Assert.Equal(150, element.Height, precision: 3);

        // TL: both axes pinned -> totals on both (mouse moved 4 left, 3 up in total).
        var topLeftThumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, "TL"));
        topLeftThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        topLeftThumb.RaiseEvent(new DragDeltaEventArgs(-2, -1) { RoutedEvent = Thumb.DragDeltaEvent });
        topLeftThumb.RaiseEvent(new DragDeltaEventArgs(-4, -2) { RoutedEvent = Thumb.DragDeltaEvent });
        topLeftThumb.RaiseEvent(new DragDeltaEventArgs(-4, -3) { RoutedEvent = Thumb.DragDeltaEvent });
        topLeftThumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

        Assert.Equal(224, element.Width, precision: 3);
        Assert.Equal(153, element.Height, precision: 3);
    }

    [Fact]
    public void InlineResize_LockOnLeftHandle_ScalesProportionally()
        => RunOnSta(RunLockOnLeftHandleProbe);

    [Fact]
    public void DecodeCache_ReusesFrozenSource()
        => RunOnSta(RunDecodeCacheProbe);

    [Fact]
    public void RestoreAsync_ManyPhotos_AllMaterialize()
        => RunOnSta(RunRestoreAsyncProbe);

    private static void RunDecodeCacheProbe()
    {
        // The session caches its frozen display source: conversions and re-syncs must reuse
        // the same instance (no re-decode), and only growing beyond the decode budget (the
        // decode width headroom) re-decodes and updates the cache.
        using var harness = CreateHarness();
        var dpi = VisualTreeHelper.GetDpi(harness.Editor).DpiScaleX;
        Assert.True(NoteImageLoader.TryLoadFromBytes(CreatePngBytes(1200, 900), 200, dpi, out var load, out var error), error);
        var id = harness.Coordinator.Insert(load!, 200, 150, new Point(40, 40));
        Assert.NotNull(id);
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().First(w => w.ImageId == id);
        var source1 = widget.Source;
        Assert.NotNull(source1);

        // Floating -> inline -> floating conversions must reuse the cached source.
        harness.Coordinator.SetWrapMode(id!.Value, NoteImageWrapMode.Square);
        harness.Coordinator.SetWrapMode(id.Value, NoteImageWrapMode.Inline);
        harness.Coordinator.SetWrapMode(id.Value, NoteImageWrapMode.None);
        harness.Editor.UpdateLayout();
        var widget2 = harness.Overlay.Children.OfType<NoteImageWidget>().First(w => w.ImageId == id);
        Assert.True(ReferenceEquals(source1, widget2.Source), "conversions must reuse the cached frozen source");

        // 200px wide -> decode budget 600px. Growing to ~238px raises the budget above the
        // cached width (the natural 1200px cap is far above), so a re-decode replaces it.
        var widthBefore = widget2.Width;
        var thumb = FindVisualChildren<Thumb>(widget2).First(t => Equals(t.Tag, "BR"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(60, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        harness.Editor.UpdateLayout();

        Assert.True(widget2.Width > widthBefore, $"the resize must have applied, got {widget2.Width:F1}");
        Assert.False(ReferenceEquals(source1, widget2.Source), "growing beyond the decode budget must re-decode");
    }

    private static void RunRestoreAsyncProbe()
    {
        // The async restore decodes off the UI thread and materializes on it - every photo
        // must appear, with the decoded sources seeded onto the sessions' caches.
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var attachments = new List<NoteImageAttachment>();
        for (var i = 0; i < 8; i++)
            attachments.Add(BuildAttachment(40, 100 + i * 160));
        harness.Coordinator.Clear(raiseChanged: false);

        var task = harness.Coordinator.RestoreAsync(attachments);
        PumpDispatcherUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        harness.Editor.UpdateLayout();

        Assert.Equal(8, harness.Overlay.Children.OfType<NoteImageWidget>().Count());
        Assert.Equal(8, harness.Coordinator.BuildAttachments().Count());
    }

    [Fact]
    public void BuildAttachments_CachesBase64Data()
        => RunOnSta(RunBuildAttachmentsCacheProbe);

    private static void RunBuildAttachmentsCacheProbe()
    {
        // The photo bytes are immutable between picture replacements - the save path must
        // reuse the cached Base64 instead of re-encoding every photo on every auto-save.
        using var harness = CreateHarness();
        var first = harness.Coordinator.BuildAttachments().Single();
        var second = harness.Coordinator.BuildAttachments().Single();
        Assert.True(ReferenceEquals(first.Data, second.Data), "the Base64 encoding must be cached across saves");
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(FixturePath)), first.Data);

        // Replacing the picture invalidates the cache and the next build encodes the new bytes.
        Assert.True(NoteImageLoader.TryLoadFromBytes(CreatePngBytes(400, 300), 200, 1.0, out var load, out var error), error);
        Assert.True(harness.Coordinator.ReplaceImageBytes(harness.ImageId, load!));
        var replaced = harness.Coordinator.BuildAttachments().Single();
        Assert.False(ReferenceEquals(first.Data, replaced.Data), "replacing the picture must invalidate the cached encoding");
        Assert.Equal(Convert.ToBase64String(load!.Bytes), replaced.Data);
    }

    [Fact]
    public void CaptionText_UpdatesOnChange_AndStaysCached()
        => RunOnSta(RunCaptionCacheProbe);

    private static void RunCaptionCacheProbe()
    {
        // Caption text is cached per session and rebuilt only when the settings or the
        // renumbering change - the rendered caption must follow every mutation.
        using var harness = CreateHarness();
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings("Figure", "A sample", CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        harness.Editor.UpdateLayout();

        var caption = harness.Overlay.Children.OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
        Assert.Equal("Figure 1: A sample", caption.Text);

        // Changing the label renumbers and refreshes the rendered caption.
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings("Table", "A sample", CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        harness.Editor.UpdateLayout();
        Assert.Equal("Table 1: A sample", caption.Text);

        // Removing the caption removes the element.
        harness.Coordinator.RemoveCaption(harness.ImageId);
        harness.Editor.UpdateLayout();
        Assert.DoesNotContain(harness.Overlay.Children.OfType<TextBlock>(), t => !string.IsNullOrEmpty(t.Text));
    }

    [Fact]
    public void WidgetResize_TogglesBitmapScalingMode()
        => RunOnSta(RunWidgetScalingModeProbe);

    private static void RunWidgetScalingModeProbe()
    {
        // The image re-scales on every drag frame - the widget renders it with the cheap
        // Linear filter during a resize gesture and switches back to HighQuality at rest.
        using var harness = CreateHarness();
        harness.Editor.UpdateLayout();
        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var image = FindVisualChildren<System.Windows.Controls.Image>(widget).First();
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));

        var thumb = FindVisualChildren<Thumb>(widget).First(t => Equals(t.Tag, "BR"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Assert.Equal(BitmapScalingMode.Linear, RenderOptions.GetBitmapScalingMode(image));

        thumb.RaiseEvent(new DragDeltaEventArgs(10, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Assert.Equal(BitmapScalingMode.Linear, RenderOptions.GetBitmapScalingMode(image));

        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
    }

    [Fact]
    public void InlineResize_TogglesBitmapScalingMode()
        => RunOnSta(RunInlineScalingModeProbe);

    private static void RunInlineScalingModeProbe()
    {
        // The in-line element's image re-scales on every resize frame - the chrome switches it
        // to the cheap Linear filter for the gesture and back to full quality at the end.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        var chrome = SelectInlineAndGetChrome(harness);
        var image = FindVisualChildren<System.Windows.Controls.Image>(element!).First();
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));

        var thumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, "BR"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Assert.Equal(BitmapScalingMode.Linear, RenderOptions.GetBitmapScalingMode(image));

        thumb.RaiseEvent(new DragDeltaEventArgs(10, 10) { RoutedEvent = Thumb.DragDeltaEvent });
        Assert.Equal(BitmapScalingMode.Linear, RenderOptions.GetBitmapScalingMode(image));

        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
    }

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        while (!condition())
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void RunLockOnLeftHandleProbe()
    {
        // With the aspect lock on, a one-axis left-handle drag must SCALE the photo (ratio
        // preserved) instead of freezing it - the LO smaller-factor rule freezes, the widget's
        // projection scales.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        Assert.True(element.LockAspectRatio, "inline lock should default on");
        var chrome = SelectInlineAndGetChrome(harness);

        var thumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, "BL"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(-10, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

        Assert.True(element.Width > 200, $"the locked left-handle drag must respond, got {element.Width:F1}");
        Assert.Equal(200.0 / 150.0, element.Width / element.Height, precision: 4);
    }

    [Fact]
    public void InlineResize_GainIsOneToOne_AllHandlesCorrect()
        => RunOnSta(RunInlineGainProbe);

    private static void RunInlineGainProbe()
    {
        // The amplification regression: a 1px drag must change the size by exactly 1px, and
        // EVERY corner handle must map its axes correctly (the TR/BL handles were inverted -
        // dragging the top-right corner right actually shrank the photo). The drags drive the
        // overlay selection chrome (the photo's resize handles live there, not in the flow).
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        element.LockAspectRatio = false;
        var chrome = SelectInlineAndGetChrome(harness);

        void Drag(string tag, double dx, double dy)
            => DragChromeHandle(chrome, tag, dx, dy);

        // BR: dragging right/down grows both axes 1:1.
        Drag("BR", 10, 0);
        Assert.Equal(210, element.Width, precision: 3);
        Assert.Equal(150, element.Height, precision: 3);

        // TR: dragging right grows the width (NOT inverted), dragging down shrinks the height.
        Drag("TR", 10, 0);
        Assert.Equal(220, element.Width, precision: 3);
        Assert.Equal(150, element.Height, precision: 3);
        Drag("TR", 0, 10);
        Assert.Equal(220, element.Width, precision: 3);
        Assert.Equal(140, element.Height, precision: 3);

        // BL: dragging right shrinks the width, dragging down grows the height.
        Drag("BL", 10, 0);
        Assert.Equal(210, element.Width, precision: 3);
        Assert.Equal(140, element.Height, precision: 3);
        Drag("BL", 0, 10);
        Assert.Equal(210, element.Width, precision: 3);
        Assert.Equal(150, element.Height, precision: 3);

        // TL: dragging right/down shrinks both axes 1:1.
        Drag("TL", 10, 10);
        Assert.Equal(200, element.Width, precision: 3);
        Assert.Equal(140, element.Height, precision: 3);

        var attachment = harness.Coordinator.BuildAttachments().Single();
        Assert.Equal(element.Width, attachment.Width, precision: 3);
        Assert.Equal(element.Height, attachment.Height, precision: 3);
    }

    [Fact]
    public void InlineResize_CaptionDoesNotEnterTheMath()
        => RunOnSta(RunInlineCaptionIsolationProbe);

    private static void RunInlineCaptionIsolationProbe()
    {
        // The resize operates on the pure image box - a caption row must never distort the
        // math or the ratio.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        // Set the caption through the coordinator (the session is the caption owner -
        // setting it on the element alone is reset by the gesture-end renumbering).
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings("Figure", "Figure 1", CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        harness.Editor.UpdateLayout();
        element.LockAspectRatio = false;

        var captionHeight = element.CaptionHeight;
        Assert.True(captionHeight > 0, "the probe needs an actual caption row");

        var chrome = SelectInlineAndGetChrome(harness);
        DragChromeHandle(chrome, "BR", 10, 10);

        // The image box grew exactly 1:1; the caption row is untouched.
        Assert.Equal(210, element.Width, precision: 3);
        Assert.Equal(160, element.Height - element.CaptionHeight, precision: 3);
        Assert.Equal(160 + captionHeight, element.Height, precision: 3);
        Assert.Equal(captionHeight, element.CaptionHeight, precision: 3);
    }

    [Fact]
    public void InlineResize_LockMaintainsAspect_FreeformWithout()
        => RunOnSta(RunInlineLockResizeScenario);

    [Fact]
    public void ResizeInlineImage_UpdatesSessionAndAttachment()
        => RunOnSta(RunResizeScenario);

    [Fact]
    public void SaveLoadRoundTrip_PreservesAnchorAndText()
        => RunOnSta(RunRoundTripScenario);

    [Fact]
    public void MarkerAtDocumentEnd_LoadsWithoutObj()
        => RunOnSta(RunMarkerAtDocumentEndScenario);

    [Fact]
    public void UnpairedMarker_SweptOnSaveAndLoad()
        => RunOnSta(RunUnpairedMarkerSweepScenario);

    [Fact]
    public void LoadedMarkers_SurviveFormattingChangesUntilReanchored()
        => RunOnSta(RunLoadFormattingGapScenario);

    [Fact]
    public void SaveCycleUndo_NeverResurrectsObjMarker()
        => RunOnSta(RunSaveUndoCycleScenario);

    [Fact]
    public void PastedRtfWithMarker_IsSanitized()
        => RunOnSta(RunPasteSanitizerScenario);

    [Fact]
    public void GetImageBytes_ReturnsStoredBytes()
        => RunOnSta(RunGetImageBytesProbe);

    [Fact]
    public void ReplaceImageBytes_RefitsAspect_AndRefreshesSource()
        => RunOnSta(RunReplaceImageBytesProbe);

    [Fact]
    public void ReplaceInlineImageBytes_RefitsAspect_AndRefreshesSource()
        => RunOnSta(RunReplaceInlineImageBytesProbe);

    [Fact]
    public void DibWrappedAsBmp_DecodesCorrectly()
        => RunOnSta(RunDibDecodeProbe);

    [Fact]
    public void WheelOverWidget_ScrollsEditor()
        => RunOnSta(RunWheelScrollProbe);

    [Fact]
    public void WheelForward_TunnelsThroughSmoothScrollBehavior()
        => RunOnSta(RunSmoothScrollInterceptionProbe);

    [Fact]
    public void Caption_RoundTripsThroughAttachments()
        => RunOnSta(RunCaptionRoundTripProbe);

    [Fact]
    public void Captions_NumberPerLabelInDocumentOrder()
        => RunOnSta(RunCaptionNumberingProbe);

    [Fact]
    public void Caption_TextFormatting_AndFloatingElement()
        => RunOnSta(RunCaptionRenderingProbe);

    [Fact]
    public void CardPreview_HidesPhotoMarkers_AndShowsHasPictures()
        => RunOnSta(RunCardPreviewProbe);

    [Fact]
    public void SerializeCycle_PreservesScrollAndCaret()
        => RunOnSta(RunSerializeViewPreserveProbe);

    [Fact]
    public void SaveWrapCycle_PreservesView()
        => RunOnSta(RunSaveWrapPreserveProbe);

    [Fact]
    public void Attachments_FileBacked_WriteAndRestore()
        => RunOnSta(RunFileBackedAttachmentScenario);

    [Fact]
    public void InlineImage_IsExcludedFromWrapEngineAndSurvivesApply()
        => RunOnSta(RunEngineExclusionScenario);

    [Fact]
    public void DeleteKey_RemovesSelectedInlineImage()
        => RunOnSta(RunDeleteScenario);

    [Fact]
    public void BringForward_SendBackward_ReorderOverlappingImages()
        => RunOnSta(RunStackReorderScenario);

    [Fact]
    public void Select_DoesNotReorderStack()
        => RunOnSta(RunSelectNoReorderScenario);

    [Fact]
    public void StackingOrder_RoundTripsThroughAttachments()
        => RunOnSta(RunStackRoundTripScenario);

    [Fact]
    public void ActionChips_CloserToCorner_AndFlipWhenPressedAgainstTop()
        => RunOnSta(RunChipPlacementScenario);

    [Fact]
    public void ImagePosition_RoundTripsExactly()
        => RunOnSta(RunPositionRoundTripScenario);

    [Fact]
    public void ImagePosition_OutOfViewWidth_SnapsIntoReachableArea()
        => RunOnSta(RunOutOfViewWidthScenario);

    [Fact]
    public void ImagePosition_DeepInDocument_KeepsY()
        => RunOnSta(RunDeepYScenario);

    [Fact]
    public void ImagePosition_WindowResizeStable()
        => RunOnSta(RunResizeStableScenario);

    [Fact]
    public void ImagePosition_WrapOff_KeepsFarRightX()
        => RunOnSta(RunWrapOffScenario);
    [Fact]
    public void DragScroll_OffsetAccumulates()
        => RunOnSta(RunDragScrollAccumulateProbe);

    private static void RunDragScrollAccumulateProbe()
    {
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; // the app's setting

        // A deep image grows the extent pad - the drag auto-scroll must be able to travel.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { BuildAttachment(40, 3000) });
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        Assert.True(sv.ScrollableHeight > 1500, $"expected a scrollable document, got {sv.ScrollableHeight:F0}");

        // Simulate repeated auto-scroll ticks (48px steps, like the drag timer).
        for (var i = 0; i < 80; i++)
        {
            sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 48));
            harness.Editor.UpdateLayout();
        }

        Assert.True(sv.VerticalOffset > 1000, $"auto-scroll should accumulate, offset={sv.VerticalOffset:F0}");
    }

    [Fact]
    public void DragRelease_DeepPosition_DoesNotSnapBack()
        => RunOnSta(RunDragReleaseSnapTest);

    [Fact]
    public void DragPad_GrowsSmoothlyDuringDrag()
        => RunOnSta(RunDragPadSmoothnessProbe);

    [Fact]
    public void TextlessNote_DragCycle_PhotoReachableAfterRelease()
        => RunOnSta(RunTextlessDragCycleProbe);

    private static void RunDragPadSmoothnessProbe()
    {
        // Regression: during a drag the extent pad must track the drag 1:1 - a quantized pad
        // (coarse steps) makes the scrollbar thumb jump in steps ("trembling") because both its
        // size and position are derived from the extent.
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var sv = FindEditorScrollViewer(harness.Editor);

        widget.DocumentLeft = 40;
        widget.DocumentTop = 600;
        harness.Coordinator.EnsureImageExtentPad(dragHeadroom: true);
        harness.Editor.UpdateLayout();
        var previousExtent = sv.ExtentHeight;

        // 40 ticks of a downward drag; the view advances 48px and the image stays just above
        // the viewport bottom. The extent must follow continuously, not jump in 128px steps.
        for (var i = 0; i < 40; i++)
        {
            sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 48));
            widget.DocumentTop = sv.VerticalOffset + sv.ViewportHeight - 150;
            harness.Coordinator.EnsureImageExtentPad(dragHeadroom: true);
            harness.Editor.UpdateLayout();

            var delta = sv.ExtentHeight - previousExtent;
            previousExtent = sv.ExtentHeight;
            Assert.True(delta <= 60,
                $"extent jumped by {delta:F0}px at step {i} - pad growth must track the drag, not jump in steps");
        }
    }

    private static void RunTextlessDragCycleProbe()
    {
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Editor.Document.Blocks.Clear();
        harness.Editor.Document.Blocks.Add(new Paragraph());
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var sv = FindEditorScrollViewer(harness.Editor);

        // Simulate the drag: the view auto-scrolls, the image follows the cursor near the
        // viewport bottom, the extent pad grows with the drag headroom (as the app's drag does).
        var trace = new System.Text.StringBuilder();
        for (var i = 0; i < 150; i++)
        {
            sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 48));
            widget.DocumentTop = sv.VerticalOffset + sv.ViewportHeight - 150;
            harness.Coordinator.EnsureImageExtentPad(dragHeadroom: true);
            harness.Editor.UpdateLayout();
            if (i % 30 == 0)
                trace.AppendLine($"i={i} offset={sv.VerticalOffset:F0} scrollable={sv.ScrollableHeight:F0} docTop={widget.DocumentTop:F0} extent={sv.ExtentHeight:F0}");
        }

        // Release.
        harness.Coordinator.EnsureImageExtentPad(stabilizeViewport: true);
        harness.Editor.UpdateLayout();
        var lastBlock = harness.Editor.Document.Blocks.LastBlock;
        var padHeight = lastBlock is BlockUIContainer { Child: FrameworkElement fe } ? fe.Height : -1;
        trace.AppendLine($"release offset={sv.VerticalOffset:F0} scrollable={sv.ScrollableHeight:F0} extent={sv.ExtentHeight:F0} padHeight={padHeight:F0} docTop={widget.DocumentTop:F0}");

        Assert.True(sv.VerticalOffset > 3000, $"view must stay deep, got {sv.VerticalOffset:F0}\n{trace}");
        Assert.True(widget.DocumentTop > 3000, $"photo must stay deep, got {widget.DocumentTop:F0}");
        Assert.True(
            sv.ScrollableHeight >= widget.DocumentTop + widget.Height + 20 - sv.ViewportHeight,
            $"photo must remain reachable after release: scrollable={sv.ScrollableHeight:F0} photoBottom={widget.DocumentTop + widget.Height:F0} viewport={sv.ViewportHeight:F0}\n{trace}");
    }

    [Fact]
    public void TextlessNote_DeepPhoto_ScrollReachable()
        => RunOnSta(RunTextlessReachabilityProbe);

    private static void RunTextlessReachabilityProbe()
    {
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Editor.Document.Blocks.Clear();
        harness.Editor.Document.Blocks.Add(new Paragraph());
        harness.Editor.UpdateLayout();

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { BuildAttachment(40, 3000) });
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        Assert.True(
            sv.ScrollableHeight > 2000,
            $"a text-less note must still be scrollable to the photo, got {sv.ScrollableHeight:F0}");
        sv.ScrollToVerticalOffset(2800);
        harness.Editor.UpdateLayout();
        Assert.True(sv.VerticalOffset > 2000, $"should scroll to the photo, got {sv.VerticalOffset:F0}");
    }

    [Fact]
    public void DragCycle_PadAndScrollStayDeep_AfterRelease()
        => RunOnSta(RunDragCycleProbe);

    private static void RunDragCycleProbe()
    {
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; // the app's setting
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var sv = FindEditorScrollViewer(harness.Editor);
        var trace = new System.Text.StringBuilder();

        // 150 ticks of a downward drag: the view auto-scrolls, the image follows the cursor
        // near the viewport bottom, and the extent pad tracks the image's position.
        for (var i = 0; i < 150; i++)
        {
            sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 48));
            widget.DocumentTop = sv.VerticalOffset + sv.ViewportHeight - 150;
            widget.DocumentLeft = 40;
            harness.Coordinator.EnsureImageExtentPad(dragHeadroom: true);
            harness.Editor.UpdateLayout();
            if (i % 30 == 0)
                trace.AppendLine($"i={i} offset={sv.VerticalOffset:F0} scrollable={sv.ScrollableHeight:F0} extent={sv.ExtentHeight:F0}");
        }

        Assert.True(sv.VerticalOffset > 3000, $"view should be deep, got {sv.VerticalOffset:F0}\n{trace}");
        Assert.True(widget.DocumentTop > 3000, $"image should be deep, got {widget.DocumentTop:F0}\n{trace}");

        // Release: the stabilized pad keeps the view in place - no jump-back.
        harness.Coordinator.EnsureImageExtentPad(stabilizeViewport: true);
        harness.Editor.UpdateLayout();
        Assert.True(sv.VerticalOffset > 3000, $"view must not jump back after release, got {sv.VerticalOffset:F0}\n{trace}");
    }

    private static void RunDragReleaseSnapTest()
    {
        using var harness = CreateHarness();
        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();

        // Simulate a drag that moved the image deep below the text (the user dragged + scrolled
        // down) and the release: the widget carries the dragged geometry, BoundsChanged fires.
        widget.DocumentLeft = 40;
        widget.DocumentTop = 3000;
        widget.SetDisplaySize(200, 150);

        // The user had scrolled down while dragging.
        var scrollViewer = FindEditorScrollViewer(harness.Editor);
        scrollViewer.ScrollToVerticalOffset(500);
        harness.Editor.UpdateLayout();

        var method = typeof(FloatingImageCoordinator).GetMethod(
            "Widget_BoundsChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(harness.Coordinator, new object[] { widget, EventArgs.Empty });
        harness.Editor.UpdateLayout();

        Assert.Equal(3000, widget.DocumentTop, precision: 2);
        Assert.Equal(3000 + 16, Canvas.GetTop(widget), precision: 2);
        // The release must keep the extent covering the current viewport - the view does not
        // jump back up when the drag-only edge reserve disappears.
        Assert.True(
            scrollViewer.ExtentHeight >= scrollViewer.VerticalOffset + scrollViewer.ViewportHeight - 1,
            $"extent {scrollViewer.ExtentHeight:F0} must cover the viewport bottom {scrollViewer.VerticalOffset + scrollViewer.ViewportHeight:F0}");
    }

    private static void RunRestoreBeforeLayoutScenario()
    {
        // Mirrors the tab flow: the editor is created and the note loaded BEFORE the window is
        // shown, so the editor is not measured when Restore runs. The stored geometry must not
        // be destroyed against the (400,300) fallback (which previously pinned images to the
        // top-left and the wrecked values then got re-saved).
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
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // The real editor paints its surface with a transparent background (a null
            // background would make the whole surface invisible to hit-testing).
            Background = Brushes.Transparent
        };
        editor.Document.Blocks.Clear();
        editor.Document.Blocks.Add(new Paragraph(new Run(
            string.Join(" ", Enumerable.Repeat(
                "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
                12)))));
        var overlay = new Canvas();
        host.Children.Add(editor);
        host.Children.Add(overlay);
        var coordinator = new FloatingImageCoordinator();
        coordinator.Attach(overlay, editor, host);

        // Restore while the window is not shown (unmeasured editor).
        var attachment = BuildAttachment(40, 3000);
        coordinator.Restore(new[] { attachment });

        // The stored geometry must survive the restore unchanged.
        var stored = coordinator.BuildAttachments().Single();
        Assert.Equal(40, stored.Left, precision: 2);
        Assert.Equal(3000, stored.Top, precision: 2);

        // After the window is shown and laid out, the in-reach position is still intact.
        window.Show();
        editor.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        editor.UpdateLayout();

        var after = coordinator.BuildAttachments().Single();
        Assert.Equal(40, after.Left, precision: 2);
        Assert.Equal(3000, after.Top, precision: 2);

        coordinator.Detach();
        window.Close();
    }

    private static NoteImageAttachment BuildAttachment(double left, double top, double width = 200, double height = 150)
    {
        var bytes = File.ReadAllBytes(FixturePath);
        return new NoteImageAttachment
        {
            Id = Guid.NewGuid(),
            Data = Convert.ToBase64String(bytes),
            Width = width,
            Height = height,
            Left = left,
            Top = top,
            LockAspectRatio = true,
            WrapMode = NoteImageWrapMode.None,
            ZIndex = 0
        };
    }

    private static ScrollViewer FindEditorScrollViewer(RichTextBox editor)
        => FindVisualChildren<ScrollViewer>(editor).First();

    private static void RunPositionRoundTripScenario()
    {
        using var harness = CreateHarness();

        // The harness image sits at (40, 300) - the full save chain must keep it exact.
        var attachments = harness.Coordinator.BuildAttachments().ToList();
        Assert.Equal(40, attachments.Single().Left, precision: 2);
        Assert.Equal(300, attachments.Single().Top, precision: 2);

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(attachments);
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        Assert.Equal(40 + 18, Canvas.GetLeft(widget), precision: 2); // padding.Left
        Assert.Equal(300 + 16, Canvas.GetTop(widget), precision: 2); // padding.Top
    }

    private static void RunOutOfViewWidthScenario()
    {
        using var harness = CreateHarness();

        // A stored X far beyond the reachable area must snap to its edge - visibly findable,
        // with the size unchanged.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { BuildAttachment(9000, 300) });
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var reachableWidth = Math.Max(FindEditorScrollViewer(harness.Editor).ViewportWidth, FindEditorScrollViewer(harness.Editor).ExtentWidth);
        Assert.Equal(Math.Max(0, reachableWidth - 200) + 18, Canvas.GetLeft(widget), precision: 2);
        Assert.Equal(200, widget.Width, precision: 2); // size untouched
        Assert.Equal(150, widget.Height, precision: 2);
    }

    private static void RunDeepYScenario()
    {
        using var harness = CreateHarness();

        // A deep image (pad-covered) must restore exactly at its Y.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { BuildAttachment(40, 3000) });
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        Assert.Equal(3000 + 16, Canvas.GetTop(widget), precision: 2);
    }

    private static void RunResizeStableScenario()
    {
        using var harness = CreateHarness();
        var attachment = BuildAttachment(40, 300);

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { attachment });
        harness.Editor.UpdateLayout();
        var left1 = Canvas.GetLeft(harness.Overlay.Children.OfType<NoteImageWidget>().Single());
        var top1 = Canvas.GetTop(harness.Overlay.Children.OfType<NoteImageWidget>().Single());

        // Widen the window and restore again - in-view positions must be identical.
        harness.Window.Width = 1200;
        harness.Editor.UpdateLayout();
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { attachment });
        harness.Editor.UpdateLayout();
        var widget2 = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        Assert.Equal(left1, Canvas.GetLeft(widget2), precision: 2);
        Assert.Equal(top1, Canvas.GetTop(widget2), precision: 2);
    }

    private static void RunWrapOffScenario()
    {
        using var harness = CreateHarness();

        // Word wrap off: the content is up to 10000px wide, so a far-right image is part of
        // the scrollable area and must restore exactly where it was placed.
        harness.Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Editor.Document.PageWidth = 10000;
        harness.Editor.UpdateLayout();

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[] { BuildAttachment(5000, 300) });
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        Assert.Equal(5000 + 18, Canvas.GetLeft(widget), precision: 2);
    }

    private static void RunChipPlacementScenario()
    {
        using var harness = CreateHarness();

        // Mid-document widget: chips sit above the top-right corner, close to the edge.
        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        harness.Editor.UpdateLayout();
        Assert.False(widget.ButtonsFlipped);
        var widgetButtons = FindVisualChildren<Button>(widget).ToList();
        Assert.True(widgetButtons.Count > 0, $"no buttons in the widget's visual tree (found {widgetButtons.Count})");
        var deleteButton = widgetButtons.First(b => b.Name == "DeleteButton");
        var lockButton = widgetButtons.First(b => b.Name == "LockAspectButton");
        Assert.Equal(12, deleteButton.Margin.Right, precision: 2);
        Assert.Equal(44, lockButton.Margin.Right, precision: 2);
        Assert.Equal(VerticalAlignment.Top, deleteButton.VerticalAlignment);

        // Image pressed against the top edge: the chips flip below the image.
        var topId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 0));
        Assert.NotNull(topId);
        harness.Editor.UpdateLayout();
        var topWidget = harness.Overlay.Children.OfType<NoteImageWidget>().First(w => w.ImageId == topId);
        Assert.True(topWidget.ButtonsFlipped);
        var topDelete = FindVisualChildren<Button>(topWidget).First(b => b.Name == "DeleteButton");
        Assert.Equal(VerticalAlignment.Bottom, topDelete.VerticalAlignment);
        Assert.Equal(-34, topDelete.Margin.Bottom, precision: 2);

        // Inline photos flip the same way when their anchor is at the very top - the flip
        // lives on the OVERLAY chrome (the flow element itself carries no chrome anymore).
        harness.Coordinator.SetWrapMode(topId!.Value, NoteImageWrapMode.Inline);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        harness.Coordinator.SelectImage(topId.Value);
        harness.Editor.UpdateLayout();
        var topChrome = FindVisualChildren<InlineImageChrome>(harness.Overlay).Single();
        Assert.True(topChrome.ButtonsFlipped);

        // The mid-document photo's chrome stays above.
        harness.Coordinator.SelectImage(harness.ImageId);
        harness.Editor.UpdateLayout();
        Assert.False(topChrome.ButtonsFlipped);
    }

    private static void RunStackReorderScenario()
    {
        using var harness = CreateHarness();

        // Two overlapping images - the second is on top (insertion order).
        var secondId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 300));
        Assert.NotNull(secondId);
        harness.Editor.UpdateLayout();

        var stack = StackOf(harness);
        Assert.Equal(new[] { harness.ImageId, secondId!.Value }, stack);

        // Bring the bottom one forward - it swaps with its neighbor.
        harness.Coordinator.BringForward(harness.ImageId);
        Assert.Equal(new[] { secondId!.Value, harness.ImageId }, StackOf(harness));
        Assert.Equal(1, ZIndexOf(harness, harness.ImageId));
        Assert.Equal(0, ZIndexOf(harness, secondId.Value));

        // Send it back again.
        harness.Coordinator.SendBackward(harness.ImageId);
        Assert.Equal(new[] { harness.ImageId, secondId.Value }, StackOf(harness));

        // Edges are no-ops: the topmost cannot go further forward.
        harness.Coordinator.BringForward(secondId.Value);
        Assert.Equal(new[] { harness.ImageId, secondId.Value }, StackOf(harness));
    }

    private static void RunSelectNoReorderScenario()
    {
        using var harness = CreateHarness();
        var secondId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 300));
        Assert.NotNull(secondId);
        harness.Editor.UpdateLayout();

        // Selection only shows the chrome - it must NOT change the stacking order.
        Assert.True(harness.Coordinator.SelectImage(harness.ImageId));
        Assert.Equal(new[] { harness.ImageId, secondId!.Value }, StackOf(harness));
        Assert.True(harness.Coordinator.SelectImage(secondId.Value));
        Assert.Equal(new[] { harness.ImageId, secondId.Value }, StackOf(harness));
        Assert.True(harness.Coordinator.HasSelection);
        Assert.Equal(secondId.Value, harness.Coordinator.SelectedImageId);
    }

    private static void RunStackRoundTripScenario()
    {
        using var harness = CreateHarness();
        var secondId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 300));
        Assert.NotNull(secondId);
        harness.Editor.UpdateLayout();

        // Settle an order different from the insertion order.
        harness.Coordinator.BringForward(harness.ImageId);
        Assert.Equal(new[] { secondId!.Value, harness.ImageId }, StackOf(harness));

        var attachments = harness.Coordinator.BuildAttachments().ToList();
        var byId = attachments.ToDictionary(a => a.Id);
        Assert.Equal(1, byId[harness.ImageId].ZIndex);
        Assert.Equal(0, byId[secondId.Value].ZIndex);

        // Reload: the settled stacking must survive.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(attachments);
        harness.Editor.UpdateLayout();
        Assert.Equal(new[] { secondId.Value, harness.ImageId }, StackOf(harness));
    }

    private static List<Guid> StackOf(EditorHarness harness)
        => harness.Overlay.Children.OfType<NoteImageWidget>().Select(w => w.ImageId).ToList();

    private static int ZIndexOf(EditorHarness harness, Guid imageId)
    {
        var attachments = harness.Coordinator.BuildAttachments();
        return attachments.First(a => a.Id == imageId).ZIndex;
    }

    [Fact]
    public void MultipleImages_ConversionCycles_SaveLoad_NoDuplication()
        => RunOnSta(RunMultiImageStressScenario);

    [Fact]
    public void WrapMenu_CheckmarksFollowMode()
        => RunOnSta(RunMenuCheckScenario);

    [Fact]
    public void MenuItemLeafTemplate_PlainItemsGetNoGutter()
        => RunOnSta(RunLeafTemplateGutterScenario);

    [Fact]
    public void SubmenuHeaderTemplate_IconHeaderAlignsWithLeafItems()
        => RunOnSta(RunSubmenuHeaderGutterScenario);

    private static void RunSubmenuHeaderGutterScenario()
    {
        // Mirrors the app's MenuItemSubmenuHeaderTemplate (App.xaml): a submenu header with an
        // icon (e.g. the "Text wrap" header) must reserve the same slot as the leaf items, so
        // its text lines up with them; headers without an icon get no slot.
        const string templateXaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:conv="clr-namespace:Scriblucent.Converters;assembly=Scriblucent"
                             TargetType="MenuItem">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid x:Name="IconGutter" Grid.Column="0" Width="Auto" VerticalAlignment="Center">
                        <Grid.MinWidth>
                            <MultiBinding Converter="{x:Static conv:MenuGutterGeometryConverter.Instance}">
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="Icon"/>
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="IsCheckable"/>
                            </MultiBinding>
                        </Grid.MinWidth>
                        <Grid.Margin>
                            <MultiBinding Converter="{x:Static conv:MenuGutterGeometryConverter.Instance}">
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="Icon"/>
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="IsCheckable"/>
                            </MultiBinding>
                        </Grid.Margin>
                        <ContentPresenter x:Name="IconPresenter" ContentSource="Icon"/>
                    </Grid>
                    <ContentPresenter Grid.Column="1" ContentSource="Header"/>
                </Grid>
            </ControlTemplate>
            """;

        var template = (ControlTemplate)XamlReader.Parse(templateXaml);
        var plainHeader = new MenuItem { Header = "No icon", Template = template };
        var iconHeader = new MenuItem { Header = "Text wrap", Template = template, Icon = new Rectangle { Width = 14, Height = 14 } };

        foreach (var item in new[] { plainHeader, iconHeader })
        {
            item.ApplyTemplate();
            item.UpdateLayout();
        }

        AssertGutterGeometry(plainHeader, 0, 0, "submenu headers without an icon get no slot");
        AssertGutterGeometry(iconHeader, 20, 10, "icon submenu headers align with the leaf items' slot");
    }

    private static void RunLeafTemplateGutterScenario()
    {
        // Mirrors the app's MenuItemLeafTemplate (App.xaml): the icon/check gutter must give
        // checkable items and icon items the SAME fixed slot (so the wrap-mode checkmark and
        // the Delete trash-can icon line up), and plain items no slot at all.
        const string templateXaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:conv="clr-namespace:Scriblucent.Converters;assembly=Scriblucent"
                             TargetType="MenuItem">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid x:Name="IconGutter" Grid.Column="0" Width="Auto" VerticalAlignment="Center">
                        <Grid.MinWidth>
                            <MultiBinding Converter="{x:Static conv:MenuGutterGeometryConverter.Instance}">
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="Icon"/>
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="IsCheckable"/>
                            </MultiBinding>
                        </Grid.MinWidth>
                        <Grid.Margin>
                            <MultiBinding Converter="{x:Static conv:MenuGutterGeometryConverter.Instance}">
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="Icon"/>
                                <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="IsCheckable"/>
                            </MultiBinding>
                        </Grid.Margin>
                        <ContentPresenter x:Name="IconPresenter" ContentSource="Icon"/>
                        <Path x:Name="CheckMark" Width="10" Height="8" Visibility="Collapsed"/>
                    </Grid>
                    <ContentPresenter Grid.Column="1" ContentSource="Header"/>
                </Grid>
            </ControlTemplate>
            """;

        var template = (ControlTemplate)XamlReader.Parse(templateXaml);
        var plain = new MenuItem { Header = "Plain", Template = template };
        var checkable = new MenuItem { Header = "Checkable", IsCheckable = true, Template = template };
        var withIcon = new MenuItem { Header = "Icon", Template = template, Icon = new Rectangle { Width = 12, Height = 12 } };
        var iconAndCheckable = new MenuItem { Header = "Both", IsCheckable = true, Template = template, Icon = new Rectangle { Width = 12, Height = 12 } };

        foreach (var item in new[] { plain, checkable, withIcon, iconAndCheckable })
        {
            item.ApplyTemplate();
            item.UpdateLayout();
        }

        // Plain items: no slot, no spacing.
        AssertGutterGeometry(plain, 0, 0, "plain items must not reserve the icon/check gutter");
        // Checkable and icon items share the SAME slot geometry - checkmark and icon line up.
        AssertGutterGeometry(checkable, 20, 10, "checkable items reserve the checkmark slot");
        AssertGutterGeometry(withIcon, 20, 10, "icon items reserve the same slot as the checkmark");
        AssertGutterGeometry(iconAndCheckable, 20, 10, "checkable icon items reserve the same slot");
    }

    private static void AssertGutterGeometry(MenuItem item, double expectedMinWidth, double expectedRightMargin, string message)
    {
        var gutter = item.Template.FindName("IconGutter", item) as FrameworkElement;
        Assert.NotNull(gutter);
        Assert.True(
            Math.Abs(gutter.MinWidth - expectedMinWidth) < 0.01
            && Math.Abs(gutter.Margin.Right - expectedRightMargin) < 0.01,
            $"{message} (got minWidth={gutter.MinWidth:F1} right={gutter.Margin.Right:F1}, expected {expectedMinWidth:F1}/{expectedRightMargin:F1})");
    }

    private static void RunMenuCheckScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();
        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);

        var menu = element.ContextMenu;
        Assert.NotNull(menu);

        // Opening the menu syncs the checkmarks from the current wrap mode (the coordinator
        // opens the menu directly, so the sync runs on the menu's Opened event).
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(GetItem(menu, "Inline").IsChecked, "Inline should be checked when the menu opens");
        Assert.False(GetItem(menu, "Square").IsChecked);
        Assert.False(GetItem(menu, "TopAndBottom").IsChecked);
        Assert.False(GetItem(menu, "None").IsChecked);

        // Clicking "Square" converts to floating and moves the checkmark.
        GetItem(menu, "Square").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(NoteImageWrapMode.Square, element.WrapMode);
        Assert.Single(harness.Overlay.Children.OfType<NoteImageWidget>()); // converted to a floating widget
        Assert.True(GetItem(menu, "Square").IsChecked, "Square should be checked after the click");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        // Reopening keeps the checkmark on the active mode (via the menu's Opened backup too).
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(GetItem(menu, "Square").IsChecked, "Square should stay checked on reopen");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(GetItem(menu, "Square").IsChecked, "Opened-event backup must keep the checkmark");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        // Radio semantics: clicking the already-active item keeps its checkmark.
        GetItem(menu, "Square").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(GetItem(menu, "Square").IsChecked, "clicking the active item must keep the checkmark");

        // The lock-aspect-ratio item mirrors the control state and toggles the session lock.
        var lockItem = GetRootItem(menu, "LockAspectItem");
        Assert.True(harness.Coordinator.BuildAttachments().Single().LockAspectRatio, "aspect lock should default to on");
        Assert.True(lockItem.IsChecked, "lock item should be checked when the aspect is locked");
        lockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(lockItem.IsChecked, "clicking the lock item should uncheck it");
        Assert.False(harness.Coordinator.BuildAttachments().Single().LockAspectRatio, "the session lock should follow");
    }

    private static MenuItem GetRootItem(ContextMenu menu, string name)
    {
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            if (child.Name == name)
                return child;
        }

        throw new Xunit.Sdk.XunitException($"root menu item '{name}' not found");
    }

    private static MenuItem GetItem(ContextMenu menu, string tag)
    {
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            if (child.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, tag)) is { } found)
                return found;
        }

        throw new Xunit.Sdk.XunitException($"menu item with tag '{tag}' not found");
    }

    private static void RunMultiImageStressScenario()
    {
        using var harness = CreateHarness();

        // Two images; the second inserted LOWER in the document is converted first (the case
        // that breaks naive order-based marker pairing).
        var secondId = harness.Coordinator.Insert(harness.Load, 200, 150, new Point(40, 80));
        Assert.NotNull(secondId);
        harness.Coordinator.SetWrapMode(secondId!.Value, NoteImageWrapMode.Inline);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Save/load round trip (markers in content + offsets on attachments).
            harness.Coordinator.PrepareInlineImagesForSerialization();
            var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.XamlPackage);
            harness.Coordinator.RestoreInlineImagesAfterSerialization();
            harness.Editor.UpdateLayout();

            var attachments = harness.Coordinator.BuildAttachments().ToList();
            Assert.Equal(2, attachments.Count);
            Assert.All(attachments.Where(a => a.WrapMode == NoteImageWrapMode.Inline),
                a => Assert.NotNull(a.InlineAnchorOffset));

            harness.Coordinator.Clear(raiseChanged: false);
            harness.Editor.Document.Blocks.Clear();
            stream.Position = 0;
            LoadIntoEditor(harness, stream, DataFormats.XamlPackage);
            harness.Coordinator.Restore(attachments);
            harness.Editor.UpdateLayout();

            // Exactly one representation per session - no duplication, no leftover markers.
            var inlineCount = attachments.Count(a => a.WrapMode == NoteImageWrapMode.Inline);
            Assert.Equal(inlineCount, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount, harness.Overlay.Children.OfType<NoteImageWidget>().Count());
            Assert.DoesNotContain('\uFFFC', new TextRange(
                harness.Editor.Document.ContentStart,
                harness.Editor.Document.ContentEnd).Text);

            // Floating -> inline and back again: never two representations.
            var anyId = harness.Coordinator.BuildAttachments().First().Id;
            harness.Coordinator.SetWrapMode(anyId, NoteImageWrapMode.Square);
            harness.Editor.UpdateLayout();
            Assert.Equal(inlineCount - 1, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount + 1, harness.Overlay.Children.OfType<NoteImageWidget>().Count());

            var floatingId = harness.Coordinator.BuildAttachments()
                .First(a => a.WrapMode != NoteImageWrapMode.Inline).Id;
            harness.Coordinator.SetWrapMode(floatingId, NoteImageWrapMode.Inline);
            harness.Editor.UpdateLayout();
            Assert.Equal(inlineCount, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount, harness.Overlay.Children.OfType<NoteImageWidget>().Count());
        }
    }

    [Fact]
    public void ClickingInlineImage_SelectsIt()
        => RunOnSta(RunClickScenario);

    [Fact]
    public void SelectingFloatingPhoto_NeverMovesTheView()
        => RunOnSta(RunSelectionParkKeepsViewScenario);

    [Fact]
    public void InlineImage_UnderFixedLineSpacing_GrowsTheLine()
        => RunOnSta(RunLineGrowthScenario);

    [Fact]
    public void InlineImage_MenuDoesNotInheritParagraphLineHeight_AndHasChips()
        => RunOnSta(RunMenuAndChipsScenario);

    private static void RunMenuAndChipsScenario()
    {
        // The element lives inside the flow: the paragraph's LineHeight/BlockLineHeight must
        // NOT leak into the context menu (it would stretch every menu row). The selection
        // chrome - which lives on the OVERLAY, not in the flow - keeps the lock/delete chips
        // of the floating widget.
        using var harness = CreateHarness();
        foreach (var paragraph in FindParagraphs(harness.Editor.Document))
        {
            paragraph.LineHeight = 19;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        harness.Editor.UpdateLayout();

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        var menu = element.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(
            double.IsNaN(Block.GetLineHeight(menu)),
            "the menu must not inherit the paragraph line height");
        Assert.Equal(LineStackingStrategy.MaxHeight, Block.GetLineStackingStrategy(menu));

        // The photo is part of the text flow; it is auto-selected on conversion, so it shows
        // the move cursor (and the text cursor when unselected).
        Assert.Equal(Cursors.SizeAll, element.Cursor);

        // The chrome is on the overlay: its handles always meet the photo's corners. The photo
        // sits on the editor's FIRST row here - exactly the case where flow-hosted handles were
        // clipped away. The chrome keeps ALL FOUR handles visible (hiding the top ones was
        // rejected: it removed usable corners); the chips flip rule itself is covered by the
        // chip-placement scenario (this photo's anchor is mid-text, so it stays above).
        var chrome = SelectInlineAndGetChrome(harness);
        Assert.Equal(Visibility.Visible, chrome.Visibility);
        foreach (var thumb in FindVisualChildren<Thumb>(chrome))
            Assert.Equal(Visibility.Visible, thumb.Visibility);

        var lockButton = FindVisualChildren<Button>(chrome).First(b => b.Name == "LockAspectButton");
        var deleteButton = FindVisualChildren<Button>(chrome).First(b => b.Name == "DeleteButton");
        Assert.Equal(VerticalAlignment.Top, lockButton.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Top, deleteButton.VerticalAlignment);

        // The lock chip toggles the session's aspect lock through its real click path (the
        // chrome's own Button - a plain click, which works because the chrome is not part of
        // the caret-ingesting text flow).
        Assert.True(harness.Coordinator.BuildAttachments().Single().LockAspectRatio);
        lockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(chrome.LockAspectRatio);
        Assert.False(element.LockAspectRatio);
        Assert.False(harness.Coordinator.BuildAttachments().Single().LockAspectRatio);

        // Flipping the chrome moves the chips below the photo (and back).
        chrome.ButtonsFlipped = true;
        Assert.Equal(VerticalAlignment.Bottom, lockButton.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Bottom, deleteButton.VerticalAlignment);
        chrome.ButtonsFlipped = false;
        Assert.Equal(VerticalAlignment.Top, lockButton.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Top, deleteButton.VerticalAlignment);
    }

    private static void RunLineGrowthScenario()
    {
        // The app's line-spacing feature fixes paragraphs to BlockLineHeight + LineHeight,
        // which would cap the line and make a tall inline image overflow the text (floating
        // look). The coordinator must switch such paragraphs to MaxHeight and back.
        using var harness = CreateHarness();
        var targetParagraphs = FindParagraphs(harness.Editor.Document);
        foreach (var paragraph in targetParagraphs)
        {
            paragraph.LineHeight = 19;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        harness.Editor.UpdateLayout();

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var imageParagraph = GetContainerParagraph(FindInlineContainer(harness.Editor.Document)!);
        Assert.NotNull(imageParagraph);
        Assert.Equal(LineStackingStrategy.MaxHeight, imageParagraph.LineStackingStrategy);
        Assert.Equal(19, imageParagraph.LineHeight); // the spacing minimum is preserved

        // The line hosting the image grows to fit it instead of overlapping the text.
        var container = FindInlineContainer(harness.Editor.Document)!;
        var lineRect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        Assert.True(lineRect.Height >= 150 - 2, $"image line should grow to ~150, got {lineRect.Height:F1}");

        // Removing the image restores the original strategy.
        harness.Coordinator.RemoveInlineImage(harness.ImageId);
        harness.Editor.UpdateLayout();
        Assert.Equal(LineStackingStrategy.BlockLineHeight, imageParagraph.LineStackingStrategy);
    }

    private static void RunSelectionParkKeepsViewScenario()
    {
        // Regression: selecting a photo parks the caret at its text anchor, which makes the
        // RichTextBox scroll the caret into view on the next layout - the text "runs away"
        // from the photo being grabbed (worst with wrap off, where nothing else masks it).
        // The click must never move the view, even after layout churn.
        using var harness = CreateHarness();
        var sv = FindEditorScrollViewer(harness.Editor);

        // Move the photo deep below the text so its anchor is far off-screen when we scroll.
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var sessionsField = typeof(FloatingImageCoordinator).GetField(
            "_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(sessionsField);
        var sessions = (Dictionary<Guid, NoteImageSession>)sessionsField!.GetValue(harness.Coordinator)!;
        var session = sessions[harness.ImageId];
        session.SetGeometry(40, 2000, session.Width, session.Height);
        harness.Coordinator.SyncAll();
        harness.Editor.UpdateLayout();

        // Scroll deep: the photo's anchor (y=2000) is now below the fold, so the caret park
        // would make the editor scroll down to reveal it.
        sv.ScrollToVerticalOffset(800);
        harness.Editor.UpdateLayout();
        var offsetBefore = sv.VerticalOffset;
        Assert.True(offsetBefore > 100, $"expected a deep scroll for the probe, got {offsetBefore:F1}");

        InvokeWidgetSelectionRequested(harness.Coordinator, harness.Overlay.Children.OfType<NoteImageWidget>().Single());
        harness.Editor.UpdateLayout();
        PumpDispatcherFrame();

        Assert.Equal(offsetBefore, sv.VerticalOffset, precision: 1);

        // Drag-time layout churn (extent pad growth / forced re-layouts) must not move it either.
        harness.Editor.UpdateLayout();
        PumpDispatcherFrame();
        Assert.Equal(offsetBefore, sv.VerticalOffset, precision: 1);
    }

    private static void InvokeWidgetSelectionRequested(FloatingImageCoordinator coordinator, NoteImageWidget widget)
    {
        var method = typeof(FloatingImageCoordinator).GetMethod(
            "Widget_SelectionRequested",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(coordinator, new object[] { widget, EventArgs.Empty });
    }

    private static void PumpDispatcherFrame()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void RunClickScenario()
    {
        // Regression: the inline element must actually wire its click handler - a click on the
        // image inside the flow selects it (selection wash + handles) so the user can focus it.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        harness.Coordinator.ClearSelection(); // the conversion selects; start deselected
        harness.Editor.UpdateLayout();
        Assert.False(harness.Coordinator.HasSelection);

        var image = FindVisualChildren<Image>(element!).First();
        image.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        });

        Assert.True(harness.Coordinator.HasSelection);
        Assert.Equal(harness.ImageId, harness.Coordinator.SelectedImageId);
        Assert.True(element.IsSelected);
    }

    private static void RunFloatingToInlineScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        // No canvas widget anymore - only the view-layer selection chrome of the in-line photo.
        Assert.DoesNotContain(harness.Overlay.Children.Cast<object>(), c => c is NoteImageWidget);
        Assert.DoesNotContain(harness.Coordinator.GetWrapSessions(), s => s.WrapMode == NoteImageWrapMode.Inline);

        Assert.True(harness.Coordinator.SelectImage(harness.ImageId));
        Assert.Equal(harness.ImageId, harness.Coordinator.SelectedImageId);
    }

    private static void RunInlineToFloatingScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();
        Assert.NotNull(FindInlineElement(harness.Editor.Document));

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        harness.Editor.UpdateLayout();

        Assert.Null(FindInlineElement(harness.Editor.Document));
        Assert.Single(harness.Overlay.Children.OfType<NoteImageWidget>());
        var sessions = harness.Coordinator.GetWrapSessions();
        Assert.Contains(sessions, s => s.WrapMode == NoteImageWrapMode.Square);
    }

    private static void RunReanchorScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var container = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(container);
        var originalParagraph = GetContainerParagraph(container!);

        // Re-anchor into the snippet paragraph.
        var snippets = FindParagraphs(harness.Editor.Document).Where(p => !ReferenceEquals(p, originalParagraph)).ToList();
        Assert.NotEmpty(snippets);
        harness.Coordinator.MoveInlineAnchor(harness.ImageId, snippets[0].ContentStart);
        harness.Editor.UpdateLayout();

        var movedContainer = FindInlineContainer(harness.Editor.Document);
        Assert.NotNull(movedContainer);
        Assert.True(
            ReferenceEquals(GetContainerParagraph(movedContainer!), snippets[0]),
            "the container should move into the target paragraph");
    }

    private static void RunInlineLockResizeScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        Assert.True(element.LockAspectRatio, "inline lock should default on");

        // The photo is auto-selected on conversion: the move cursor (it is draggable).
        Assert.Equal(Cursors.SizeAll, element.Cursor);
        element.IsSelected = false;
        Assert.Equal(Cursors.IBeam, element.Cursor);
        element.IsSelected = true;
        Assert.Equal(Cursors.SizeAll, element.Cursor);

        var chrome = SelectInlineAndGetChrome(harness);

        // Locked: a diagonal BR drag must keep the 200:150 ratio.
        DragChromeHandle(chrome, "BR", 80, 100);
        Assert.Equal(200.0 / 150.0, element.Width / element.Height, precision: 4);

        // Unlocked: the same drag becomes freeform (the ratio changes with the deltas).
        element.LockAspectRatio = false;
        var wBefore = element.Width;
        var hBefore = element.Height;
        DragChromeHandle(chrome, "BR", 40, 10);
        Assert.Equal(wBefore + 40, element.Width, precision: 3);
        Assert.Equal(hBefore + 10, element.Height, precision: 3);
        Assert.NotEqual(wBefore / hBefore, element.Width / element.Height, precision: 4);
    }

    private static void RunResizeScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);

        // Grab the bottom-right resize handle of the overlay chrome and simulate a drag.
        var chrome = SelectInlineAndGetChrome(harness);
        DragChromeHandle(chrome, "BR", 40, 30);

        Assert.True(element.Width > 200, $"image should have grown, got {element.Width:F1}");
        Assert.True(element.Height > 150, $"image should have grown, got {element.Height:F1}");

        var attachment = harness.Coordinator.BuildAttachments().Single();
        Assert.True(attachment.Width > 200, $"attachment should carry the new width, got {attachment.Width:F1}");
        Assert.True(attachment.Height > 150, $"attachment should carry the new height, got {attachment.Height:F1}");
    }

    private static void RunSaveWrapPreserveProbe()
    {
        // Regression: the save path clears and reapplies the wrap engine's bands around the
        // serialization; each pass forces intermediate layouts while the flow is shrunk, which
        // clamps the scroll offset (the auto-save "does not keep me in the same place"). The
        // view state captured before the cycle must come back intact.
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // Square wrap engages the wrap engine's bands (the intermediate shrink is real).
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Square);
        for (var i = 0; i < 30; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        // The bands normally materialize via the dispatcher; apply them synchronously so the
        // baseline matches the steady-state layout the save's Clear+Reapply cycles through.
        harness.Engine.Reapply();
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);

        harness.Editor.CaretPosition = harness.Editor.Document.ContentEnd;
        sv.ScrollToVerticalOffset(sv.ScrollableHeight);
        harness.Editor.UpdateLayout();
        var offsetBefore = sv.VerticalOffset;
        Assert.True(offsetBefore > 100, $"expected a deep scroll, got {offsetBefore:F0}");

        // The save-path shape: the wrap clears (flow shrinks), an intermediate layout runs,
        // the wrap reapplies, then the view state is restored.
        var state = harness.Coordinator.CaptureViewState();
        harness.Engine.Clear();
        harness.Editor.UpdateLayout();
        harness.Engine.Reapply();
        harness.Coordinator.RestoreViewState(state);
        harness.Editor.UpdateLayout();

        Assert.Equal(offsetBefore, sv.VerticalOffset, 1.0);
        Assert.Equal(0, state.Caret!.GetOffsetToPosition(harness.Editor.CaretPosition));
    }

    private static void RunFileBackedAttachmentScenario()
    {
        // The photo bytes live as files in the media directory (not as Base64 in the store
        // JSON) - the save must write the file once, reference it by name, and the restore
        // must read it back into the session.
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.TopAndBottom);
        var mediaDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "notecards-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            harness.Coordinator.MediaDirectory = mediaDir;

            var attachments = harness.Coordinator.BuildAttachments().ToList();
            Assert.Single(attachments);
            var attachment = attachments[0];
            Assert.Equal(harness.ImageId, attachment.Id);
            Assert.False(string.IsNullOrEmpty(attachment.DataFilePath),
                "file-backed attachments must carry a media file name");
            Assert.True(string.IsNullOrEmpty(attachment.Data),
                "file-backed attachments must not carry inline base64");
            var filePath = System.IO.Path.Combine(mediaDir, attachment.DataFilePath);
            Assert.True(System.IO.File.Exists(filePath), "the media file should have been written");

            // Clear + restore from the file-backed attachments must reproduce the session.
            harness.Coordinator.Clear();
            harness.Coordinator.Restore(attachments);
            harness.Editor.UpdateLayout();
            var restored = harness.Coordinator.GetWrapSessions().ToList();
            Assert.Single(restored);
            Assert.Equal(harness.ImageId, restored[0].Id);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(mediaDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void RunSerializeViewPreserveProbe()
    {
        // Regression: the save path strips the extent pad and the inline photo containers out
        // of the live flow - any layout pass in between clamps the scroll / snaps the caret
        // (the auto-save "teleport to the start of the note"). The view state captured before
        // the cycle must come back intact afterwards.
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        // Make the document tall enough to scroll.
        for (var i = 0; i < 30; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        var container = FindAllInlineContainers(harness.Editor.Document).Single();
        var docEnd = harness.Editor.Document.ContentEnd;

        // Deep scroll with the caret parked at the end of the document.
        harness.Editor.CaretPosition = docEnd;
        sv.ScrollToVerticalOffset(sv.ScrollableHeight);
        harness.Editor.UpdateLayout();
        var offsetBefore = sv.VerticalOffset;
        Assert.True(offsetBefore > 100, $"expected a deep scroll, got {offsetBefore:F0}");

        // The full serialization cycle, with a layout pass in the middle (the dangerous part).
        var state = harness.Coordinator.CaptureViewState();
        harness.Coordinator.StripImageExtentPad();
        harness.Coordinator.PrepareInlineImagesForSerialization();
        harness.Editor.UpdateLayout();
        harness.Coordinator.RestoreInlineImagesAfterSerialization();
        harness.Coordinator.EnsureImageExtentPad();
        harness.Coordinator.RestoreViewState(state);
        harness.Editor.UpdateLayout();

        Assert.Equal(offsetBefore, sv.VerticalOffset, 1.0);
        Assert.Equal(0, state.Caret!.GetOffsetToPosition(harness.Editor.CaretPosition));
    }

    private static void RunGetImageBytesProbe()
    {
        using var harness = CreateHarness();
        var original = File.ReadAllBytes(FixturePath);
        var bytes = harness.Coordinator.GetImageBytes(harness.ImageId);
        Assert.NotNull(bytes);
        Assert.Equal(original, bytes);
    }

    private static void RunReplaceImageBytesProbe()
    {
        using var harness = CreateHarness();
        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var oldSource = widget.Source;

        // A wide 2:1 image replaces the original; the frame keeps its width and re-fits height.
        var wideBytes = CreatePngBytes(200, 100);
        Assert.True(NoteImageLoader.TryLoadFromBytes(wideBytes, 200, 1, out var load, out var error), error);
        Assert.NotNull(load);

        Assert.True(harness.Coordinator.ReplaceImageBytes(harness.ImageId, load!));
        Assert.NotSame(oldSource, widget.Source);
        // Width kept (200), height re-fits to the 2:1 aspect → 100.
        Assert.Equal(100, widget.Height, 1.0);
        Assert.Equal(wideBytes, harness.Coordinator.GetImageBytes(harness.ImageId));
    }

    private static void RunReplaceInlineImageBytesProbe()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        var oldSource = element.Source;

        var wideBytes = CreatePngBytes(200, 100);
        Assert.True(NoteImageLoader.TryLoadFromBytes(wideBytes, 200, 1, out var load, out var error), error);
        Assert.NotNull(load);

        Assert.True(harness.Coordinator.ReplaceImageBytes(harness.ImageId, load!));
        Assert.NotSame(oldSource, element.Source);
        // Width kept (200), height re-fits to the 2:1 aspect → 100.
        Assert.Equal(100, element.Height, 1.0);
    }

    private static void RunDibDecodeProbe()
    {
        // Clipboard DIB data has no BITMAPFILEHEADER; the window wraps it in one and decodes
        // through BmpBitmapDecoder. Verify that a stripped-then-wrapped DIB still decodes.
        var source = new RenderTargetBitmap(10, 10, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 10, 10));
        source.Render(visual);

        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
        using var bmpStream = new MemoryStream();
        encoder.Save(bmpStream);
        var bmp = bmpStream.ToArray();

        const int fileHeaderSize = 14;
        var dib = bmp.AsSpan(fileHeaderSize).ToArray();

        var wrapped = new byte[dib.Length + fileHeaderSize];
        wrapped[0] = (byte)'B';
        wrapped[1] = (byte)'M';
        BitConverter.GetBytes(wrapped.Length).CopyTo(wrapped, 2);
        BitConverter.GetBytes(fileHeaderSize).CopyTo(wrapped, 10);
        Array.Copy(dib, 0, wrapped, fileHeaderSize, dib.Length);

        var decoder = new BmpBitmapDecoder(
            new MemoryStream(wrapped),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(10, decoder.Frames[0].PixelWidth);
        Assert.Equal(10, decoder.Frames[0].PixelHeight);
    }

    private static void RunWheelScrollProbe()
    {
        // The floating widget sits on the overlay - a sibling of the editor - so the wheel over
        // a photo must be forwarded to the editor's ScrollViewer (it never reaches it naturally).
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        for (var i = 0; i < 30; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        Assert.True(sv.ScrollableHeight > 100, $"expected a scrollable document, got {sv.ScrollableHeight:F0}");

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        var before = sv.VerticalOffset;
        // Negative delta = wheel down: from the top the view must move down into the document.
        // The ScrollViewer applies the offset at the next layout pass.
        widget.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        });
        harness.Editor.UpdateLayout();
        var after = sv.VerticalOffset;

        // A rapid wheel (5 more notches in a row) must keep accumulating - the editor's smooth
        // scrolling machinery must not swallow the re-raised events.
        for (var i = 0; i < 5; i++)
        {
            widget.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            });
            harness.Editor.UpdateLayout();
        }

        Assert.True(after > before, $"wheel over a floating photo must scroll the editor (before={before:F0} after={after:F0})");
        Assert.True(sv.VerticalOffset > after, "repeated wheels over a photo must keep scrolling");
    }

    private static void RunSmoothScrollInterceptionProbe()
    {
        // The app attaches SmoothScrollBehavior to every ScrollViewer (implicit style) - the
        // editor's own wheel path. A forwarded wheel must go through that same tunneling route
        // so the behavior handles it (and its animation doesn't fight the scroll).
        using var harness = CreateHarness();
        harness.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        for (var i = 0; i < 30; i++)
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Filler paragraph to make the document tall enough to scroll.")));
        harness.Editor.UpdateLayout();

        var sv = FindEditorScrollViewer(harness.Editor);
        Scriblucent.Behaviors.SmoothScrollBehavior.SetIsEnabled(sv, true);
        harness.Editor.UpdateLayout();

        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        sv.RaiseEvent(preview);

        Assert.True(preview.Handled,
            "the tunneling preview raise must be handled by SmoothScrollBehavior (the real-app wheel path)");
    }

    private static void RunCaptionRoundTripProbe()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings(
                "Figure", "A caption", CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        harness.Editor.UpdateLayout();

        var attachments = harness.Coordinator.BuildAttachments().ToList();
        var attachment = Assert.Single(attachments);
        Assert.Equal("Figure", attachment.CaptionLabel);
        Assert.Equal("A caption", attachment.CaptionText);
        Assert.Equal(CaptionPosition.Below, attachment.CaptionPosition);

        // Restore round-trip: a fresh harness restores the caption.
        using (var restored = CreateHarness())
        {
            restored.Coordinator.Restore(new[] { attachment });
            var caption = restored.Coordinator.GetCaption(attachment.Id);
            Assert.NotNull(caption);
            Assert.Equal("Figure", caption.Label);
            Assert.Equal("A caption", caption.Text);
            Assert.Equal(": ", caption.Separator);
            Assert.Equal(CaptionPosition.Below, caption.Position);
        }
    }

    private static void RunCaptionNumberingProbe()
    {
        using var harness = CreateHarness();
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Coordinator.Restore(new[]
        {
            BuildAttachment(40, 3000), // second in document order
            BuildAttachment(40, 100)   // first in document order
        });
        harness.Editor.UpdateLayout();

        var sessions = harness.Coordinator.GetWrapSessions();
        Assert.Equal(2, sessions.Count);
        var ids = sessions.Select(s => s.Id).ToList();

        foreach (var id in ids)
        {
            harness.Coordinator.SetCaption(
                id,
                new FloatingImageCoordinator.CaptionSettings(
                    "Figure", string.Empty, CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        }

        // The first captioned image (document-top 100) is Figure 1; the deeper one is Figure 2.
        var captioned = sessions.OrderBy(s => s.DocumentTop).ToList();
        Assert.Equal(1, captioned[0].CaptionNumber);
        Assert.Equal(2, captioned[1].CaptionNumber);

        // Per-label sequences: the deep image relabels to "Illustration", so the two labels
        // number independently (the shallow one is now the only Figure → 1).
        harness.Coordinator.SetCaption(
            captioned[1].Id,
            new FloatingImageCoordinator.CaptionSettings(
                "Illustration", string.Empty, CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false));
        Assert.Equal(1, captioned[0].CaptionNumber);
        Assert.Equal(1, captioned[1].CaptionNumber);
    }

    private static void RunCaptionRenderingProbe()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings(
                "Figure", "A diagram", CaptionNumbering.RomanUpper, ": ", CaptionPosition.Below, false));
        harness.Editor.UpdateLayout();

        var widget = harness.Overlay.Children.OfType<NoteImageWidget>().Single();
        // The floating caption element sits on the overlay, following the photo.
        var captionBlock = harness.Overlay.Children.OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));
        Assert.NotNull(captionBlock);
        Assert.Equal("Figure I: A diagram", captionBlock!.Text);

        // Exclude label → just the number.
        harness.Coordinator.SetCaption(
            harness.ImageId,
            new FloatingImageCoordinator.CaptionSettings(
                "Figure", "A diagram", CaptionNumbering.RomanUpper, ": ", CaptionPosition.Below, true));
        Assert.Equal("I: A diagram", captionBlock.Text);

        harness.Coordinator.RemoveCaption(harness.ImageId);
        Assert.DoesNotContain(harness.Overlay.Children.OfType<TextBlock>(), t => !string.IsNullOrEmpty(t.Text));
    }

    private static void RunCardPreviewProbe()
    {
        // In-line photo markers (\uFFFC) become position sentinels in the preview (no "obj"),
        // and floating photos trail sentinels so the card still signals their presence.
        var flow = new FlowDocument();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("Before "));
        paragraph.Inlines.Add(new Run('\uFFFC'.ToString()));
        paragraph.Inlines.Add(new Run(" after"));
        flow.Blocks.Add(paragraph);

        using var ms = new MemoryStream();
        new TextRange(flow.ContentStart, flow.ContentEnd).Save(ms, DataFormats.XamlPackage);
        var content = Convert.ToBase64String(ms.ToArray());

        var document = new NoteDocument
        {
            Title = "T",
            Content = content,
            Images = new List<NoteImageAttachment>
            {
                new NoteImageAttachment { WrapMode = NoteImageWrapMode.Inline },   // marker in flow
                new NoteImageAttachment { WrapMode = NoteImageWrapMode.Square }    // floating → sentinel
            }
        };
        var vm = new Scriblucent.ViewModels.NoteCardViewModel(document, _ => { });
        Assert.DoesNotContain("obj", vm.Content);
        // The inline marker stays at its position; the floating photo adds one trailing sentinel.
        Assert.Equal("Before \uFFFC after\uFFFC", vm.Content);
        Assert.True(vm.HasPictures);

        // Legacy marker content becomes a sentinel too.
        var legacy = new NoteDocument { Title = "L", Content = "Text [[ScriblucentImage:abc]] more" };
        var legacyVm = new Scriblucent.ViewModels.NoteCardViewModel(legacy, _ => { });
        Assert.Contains('\uFFFC', legacyVm.Content);
        Assert.DoesNotContain("[[ScriblucentImage", legacyVm.Content);

        // A floating photo deep in the pad region (far below the text) adds no sentinel...
        var farDocument = new NoteDocument
        {
            Title = "F",
            Content = content,
            Images = new List<NoteImageAttachment>
            {
                new NoteImageAttachment { WrapMode = NoteImageWrapMode.Square, Top = 5000 }
            }
        };
        var farVm = new Scriblucent.ViewModels.NoteCardViewModel(farDocument, _ => { });
        Assert.Equal("Before \uFFFC after", farVm.Content);

        // ...but a photo-only note (no text) still signals the photo, wherever it sits.
        var photoOnly = new NoteDocument
        {
            Title = "P",
            Content = null,
            Images = new List<NoteImageAttachment>
            {
                new NoteImageAttachment { WrapMode = NoteImageWrapMode.Square, Top = 5000 }
            }
        };
        var photoOnlyVm = new Scriblucent.ViewModels.NoteCardViewModel(photoOnly, _ => { });
        Assert.Equal('\uFFFC'.ToString(), photoOnlyVm.Content);
    }

    private static NoteImageAttachment BuildAttachment(double left, double top)
    {
        var bytes = File.ReadAllBytes(FixturePath);
        var dpi = 1.0;
        if (!NoteImageLoader.TryLoadFromBytes(bytes, 200, dpi, out var load, out _) || load is null)
            throw new InvalidOperationException("fixture failed to load");

        return new NoteImageAttachment
        {
            Data = Convert.ToBase64String(bytes),
            Width = 200,
            Height = 150,
            Left = left,
            Top = top,
            WrapMode = NoteImageWrapMode.Square,
            ZIndex = 0
        };
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, height));
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RunRoundTripScenario()
    {
        using var harness = CreateHarness();
        var originalText = GetRunsText(harness.Editor.Document);

        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();
        var beforeText = GetTextBeforeFirstInline(harness.Editor.Document);

        // Simulate SaveToDocument: markers replace the elements, the flow is serialized,
        // then the elements come back and the offsets are recorded on the attachments.
        harness.Coordinator.PrepareInlineImagesForSerialization();
        Assert.Null(FindInlineElement(harness.Editor.Document)); // DEBUG: prepare must remove the element
        var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.XamlPackage);
        harness.Coordinator.RestoreInlineImagesAfterSerialization();
        harness.Editor.UpdateLayout();

        Assert.NotNull(FindInlineElement(harness.Editor.Document)); // elements are back in the live editor
        var attachments = harness.Coordinator.BuildAttachments().ToList();
        Assert.Single(attachments);
        Assert.Equal(NoteImageWrapMode.Inline, attachments[0].WrapMode);
        Assert.NotNull(attachments[0].InlineAnchorOffset);

        // Simulate LoadFromDocument: fresh document content, then Restore re-anchors.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Editor.Document.Blocks.Clear();
        stream.Position = 0;
        LoadIntoEditor(harness, stream, DataFormats.XamlPackage);
        harness.Coordinator.Restore(attachments);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        Assert.Equal(beforeText, GetTextBeforeFirstInline(harness.Editor.Document));
        Assert.Equal(originalText, GetRunsText(harness.Editor.Document)); // no text loss, no leftover markers
    }

    private static void LoadIntoEditor(EditorHarness harness, MemoryStream stream, string dataFormat)
    {
        harness.Coordinator.SuppressMarkerSweep = true;
        try
        {
            var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            range.Load(stream, dataFormat);
        }
        finally
        {
            harness.Coordinator.SuppressMarkerSweep = false;
        }
    }

    private static void RunMarkerAtDocumentEndScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.Clear(raiseChanged: false);

        // A note whose photo is the LAST thing in the text: the trailing marker is dropped by
        // the RichTextBox while loading (before it could ever pair), so the session falls back
        // to floating. The load must never render an "obj" or lose the photo.
        harness.Editor.Document.Blocks.Clear();
        harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Text before\uFFFC")));
        harness.Editor.UpdateLayout();

        var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.XamlPackage);

        var attachment = new NoteImageAttachment
        {
            Data = Convert.ToBase64String(File.ReadAllBytes(FixturePath)),
            Width = 200,
            Height = 150,
            Left = 40,
            Top = 300,
            WrapMode = NoteImageWrapMode.Inline,
            InlineAnchorOffset = "Text before".Length
        };

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Editor.Document.Blocks.Clear();
        stream.Position = 0;
        LoadIntoEditor(harness, stream, DataFormats.XamlPackage);
        harness.Coordinator.Restore(new[] { attachment });
        harness.Editor.UpdateLayout();

        Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
        Assert.Equal("Text before", GetRunsText(harness.Editor.Document));
        var inline = FindInlineElement(harness.Editor.Document);
        var floating = harness.Overlay.Children.OfType<NoteImageWidget>().Any();
        Assert.True(inline is not null || floating, "the photo must not be lost");
    }

    private static void RunUnpairedMarkerSweepScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.Clear(raiseChanged: false);

        // Build the marker content under suppression so the scenario genuinely exercises the
        // prepare-time sweep (the app never builds markers outside the guarded windows).
        harness.Coordinator.SuppressMarkerSweep = true;
        try
        {
            // Save side: a stray marker (e.g. pasted RTF from a copied selection) with no image
            // session must be swept before the flow is serialized - never persisted.
            harness.Editor.Document.Blocks.Clear();
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("before\uFFFCafter")));
        }
        finally
        {
            harness.Coordinator.SuppressMarkerSweep = false;
        }
        harness.Editor.UpdateLayout();

        var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        harness.Coordinator.PrepareInlineImagesForSerialization();
        range.Save(stream, DataFormats.XamlPackage);
        harness.Coordinator.RestoreInlineImagesAfterSerialization();
        harness.Editor.UpdateLayout();

        Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
        Assert.Equal("beforeafter", GetRunsText(harness.Editor.Document));

        // Load side: a document with a stray marker and a session whose offset pairs with
        // nothing must still end up with the marker swept (the session goes floating).
        harness.Coordinator.SuppressMarkerSweep = true;
        try
        {
            harness.Coordinator.Clear(raiseChanged: false);
            harness.Editor.Document.Blocks.Clear();
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("before\uFFFCafter")));
        }
        finally
        {
            harness.Coordinator.SuppressMarkerSweep = false;
        }
        harness.Editor.UpdateLayout();
        range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var strayStream = new MemoryStream();
        range.Save(strayStream, DataFormats.XamlPackage);

        var orphanAttachment = new NoteImageAttachment
        {
            Data = Convert.ToBase64String(File.ReadAllBytes(FixturePath)),
            Width = 200,
            Height = 150,
            Left = 40,
            Top = 300,
            WrapMode = NoteImageWrapMode.Inline,
            InlineAnchorOffset = 999 // matches nothing - goes floating, marker still swept
        };

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Editor.Document.Blocks.Clear();
        strayStream.Position = 0;
        LoadIntoEditor(harness, strayStream, DataFormats.XamlPackage);
        harness.Coordinator.Restore(new[] { orphanAttachment });
        harness.Editor.UpdateLayout();

        Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
        Assert.Equal("beforeafter", GetRunsText(harness.Editor.Document));
        Assert.Single(harness.Overlay.Children.OfType<NoteImageWidget>());
    }

    private static void RunLoadFormattingGapScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.Clear(raiseChanged: false);

        // Build the marker content under suppression (the app never builds markers outside
        // the guarded save/load windows - only tests do).
        harness.Coordinator.SuppressMarkerSweep = true;
        try
        {
            harness.Editor.Document.Blocks.Clear();
            harness.Editor.Document.Blocks.Add(new Paragraph(new Run("Text \uFFFCafter")));
        }
        finally
        {
            harness.Coordinator.SuppressMarkerSweep = false;
        }
        harness.Editor.UpdateLayout();

        var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.XamlPackage);

        var attachment = new NoteImageAttachment
        {
            Data = Convert.ToBase64String(File.ReadAllBytes(FixturePath)),
            Width = 200,
            Height = 150,
            Left = 40,
            Top = 300,
            WrapMode = NoteImageWrapMode.Inline,
            InlineAnchorOffset = "Text ".Length
        };

        harness.Coordinator.Clear(raiseChanged: false);
        harness.Editor.Document.Blocks.Clear();
        stream.Position = 0;

        // Mirror LoadFromDocument: the sweep is suppressed from the content load through the
        // font/theme application until Restore re-anchors - formatting changes fire
        // TextChanged and must not delete the freshly loaded markers.
        harness.Coordinator.SuppressMarkerSweep = true;
        try
        {
            var loadRange = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            loadRange.Load(stream, DataFormats.XamlPackage);
            harness.Editor.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            harness.Editor.FontSize = 16;
        }
        finally
        {
            harness.Coordinator.SuppressMarkerSweep = false;
        }

        harness.Coordinator.Restore(new[] { attachment });
        harness.Editor.UpdateLayout();

        Assert.NotNull(FindInlineElement(harness.Editor.Document));
        Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
        Assert.Equal("Text after", GetRunsText(harness.Editor.Document));
    }

    private static void RunSaveUndoCycleScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // A save cycle: markers in, flow serialized, markers out.
            harness.Coordinator.PrepareInlineImagesForSerialization();
            var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.XamlPackage);
            harness.Coordinator.RestoreInlineImagesAfterSerialization();
            harness.Editor.UpdateLayout();

            Assert.NotNull(FindInlineElement(harness.Editor.Document));
            Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));

            // Whatever the RichTextBox records in its undo stack, a single undo must never
            // resurrect the marker char next to the picture.
            if (harness.Editor.CanUndo)
                harness.Editor.Undo();
            harness.Editor.UpdateLayout();
            Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
        }
    }

    private static void RunPasteSanitizerScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        // Build clipboard data the way a copied selection from this editor looks: plain text
        // plus RTF that carries the in-line photo marker char.
        var dataObject = new DataObject();
        dataObject.SetText("before after", TextDataFormat.UnicodeText);
        var range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        harness.Coordinator.PrepareInlineImagesForSerialization();
        range.Save(stream, DataFormats.Rtf);
        harness.Coordinator.RestoreInlineImagesAfterSerialization();
        var rtf = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains("\\u-4", rtf, StringComparison.Ordinal);
        dataObject.SetData(DataFormats.Rtf, rtf);

        var containsMarkers = (bool)typeof(NoteEditorWindow).GetMethod(
            "ContainsInlineMarkers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { dataObject })!;
        Assert.True(containsMarkers);

        var sanitized = (DataObject)typeof(NoteEditorWindow).GetMethod(
            "SanitizePasteDataObject",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { dataObject })!;

        var sanitizedRtf = (string)sanitized.GetData(DataFormats.Rtf)!;
        Assert.DoesNotContain("\\u-4", sanitizedRtf, StringComparison.Ordinal);

        // Pasting the sanitized RTF into an editor must not produce the marker char.
        harness.Coordinator.Clear(raiseChanged: false);
        harness.Editor.Document.Blocks.Clear();
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(sanitizedRtf));
        LoadIntoEditor(harness, ms, DataFormats.Rtf);
        harness.Editor.UpdateLayout();
        Assert.DoesNotContain('\uFFFC', GetRunsText(harness.Editor.Document));
    }

    private static void RunEngineExclusionScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        Assert.DoesNotContain(harness.Coordinator.GetWrapSessions(), s => s.WrapMode == NoteImageWrapMode.Inline);

        // The wrap engine must not disturb the inline element.
        harness.Engine.Apply();
        harness.Editor.UpdateLayout();
        Assert.NotNull(FindInlineElement(harness.Editor.Document));
    }

    private static void RunDeleteScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();
        harness.Coordinator.SelectImage(harness.ImageId);
        Assert.True(harness.Coordinator.HasSelection);

        var key = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            new PresentationSource_Stub(),
            0,
            Key.Delete)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        Assert.True(harness.Coordinator.TryHandleKeyDown(key));
        harness.Editor.UpdateLayout();

        Assert.Null(FindInlineElement(harness.Editor.Document));
        Assert.False(harness.Coordinator.HasSelection);
        Assert.Empty(harness.Coordinator.BuildAttachments());
    }

    // ---------------------------------------------------------------- helpers

    private sealed class PresentationSource_Stub : PresentationSource
    {
        protected override CompositionTarget? GetCompositionTargetCore() => null;
        public override bool IsDisposed => false;
        public override Visual? RootVisual { get; set; }
    }

    private sealed class EditorHarness : IDisposable
    {
        public required Window Window { get; init; }
        public required RichTextBox Editor { get; init; }
        public required Canvas Overlay { get; init; }
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

    private static EditorHarness CreateHarness()
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
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // The real editor paints its surface with a transparent background (a null
            // background would make the whole surface invisible to hit-testing).
            Background = Brushes.Transparent
        };
        editor.Document.Blocks.Clear();
        var longText = string.Join(
            " ",
            Enumerable.Repeat(
                "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
                12));
        editor.Document.Blocks.Add(new Paragraph(new Run(longText)));
        editor.Document.Blocks.Add(new Paragraph(new Run("Short snippet at the bottom.")));

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
            Overlay = overlay,
            Coordinator = coordinator,
            Engine = engine,
            Load = load!,
            ImageId = imageId!.Value
        };
    }

    private static List<InlineUIContainer> FindAllInlineContainers(FlowDocument document)
    {
        var result = new List<InlineUIContainer>();
        foreach (Block block in document.Blocks)
            CollectInlineContainersInBlock(block, result);
        return result;
    }

    private static void CollectInlineContainersInBlock(Block block, List<InlineUIContainer> result)
    {
        switch (block)
        {
            case Paragraph paragraph:
                CollectInlineContainersInInlines(paragraph.Inlines, result);
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                    CollectInlineContainersInBlock(child, result);
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                        CollectInlineContainersInBlock(child, result);
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                                CollectInlineContainersInBlock(child, result);
                        }
                    }
                }
                break;
        }
    }

    private static void CollectInlineContainersInInlines(InlineCollection inlines, List<InlineUIContainer> result)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Child: InlineNoteImageElement } container:
                    result.Add(container);
                    break;
                case Span span:
                    CollectInlineContainersInInlines(span.Inlines, result);
                    break;
            }
        }
    }

    private static InlineUIContainer? FindInlineContainer(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (FindInlineContainerInBlock(block) is { } container)
                return container;
        }

        return null;
    }

    private static InlineUIContainer? FindInlineContainerInBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                return FindInlineContainerInInlines(paragraph.Inlines);
            case Section section:
                foreach (Block child in section.Blocks)
                {
                    if (FindInlineContainerInBlock(child) is { } found)
                        return found;
                }
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                    {
                        if (FindInlineContainerInBlock(child) is { } found)
                            return found;
                    }
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                            {
                                if (FindInlineContainerInBlock(child) is { } found)
                                    return found;
                            }
                        }
                    }
                }
                break;
        }

        return null;
    }

    private static InlineUIContainer? FindInlineContainerInInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Child: InlineNoteImageElement } container:
                    return container;
                case Span span:
                    if (FindInlineContainerInInlines(span.Inlines) is { } found)
                        return found;
                    break;
            }
        }

        return null;
    }

    private static InlineNoteImageElement? FindInlineElement(FlowDocument document)
        => FindInlineContainer(document)?.Child as InlineNoteImageElement;

    /// <summary>Selects the inline photo and returns its overlay selection chrome (the resize
    /// handles + chips live on the overlay, not in the text flow).</summary>
    private static InlineImageChrome SelectInlineAndGetChrome(EditorHarness harness)
    {
        harness.Coordinator.SelectImage(harness.ImageId);
        harness.Editor.UpdateLayout();
        return FindVisualChildren<InlineImageChrome>(harness.Overlay).Single();
    }

    /// <summary>Simulates a corner-handle drag on the overlay chrome.</summary>
    private static void DragChromeHandle(InlineImageChrome chrome, string tag, double dx, double dy)
    {
        var thumb = FindVisualChildren<Thumb>(chrome).First(t => Equals(t.Tag, tag));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(dx, dy) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static Paragraph? GetContainerParagraph(InlineUIContainer container)
    {
        for (DependencyObject? current = container; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is Paragraph paragraph)
                return paragraph;
        }

        return null;
    }

    private static string GetTextBeforeFirstInline(FlowDocument document)
    {
        var found = false;
        return CollectTextBefore(document, ref found);
    }

    private static string CollectTextBefore(FlowDocument document, ref bool found)
    {
        var builder = new System.Text.StringBuilder();
        foreach (Block block in document.Blocks)
            CollectTextBeforeInBlock(block, builder, ref found);
        return builder.ToString();
    }

    private static void CollectTextBeforeInBlock(Block block, System.Text.StringBuilder builder, ref bool found)
    {
        switch (block)
        {
            case Paragraph paragraph:
                CollectTextBeforeInInlines(paragraph.Inlines, builder, ref found);
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                    CollectTextBeforeInBlock(child, builder, ref found);
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                        CollectTextBeforeInBlock(child, builder, ref found);
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                                CollectTextBeforeInBlock(child, builder, ref found);
                        }
                    }
                }
                break;
        }
    }

    private static void CollectTextBeforeInInlines(InlineCollection inlines, System.Text.StringBuilder builder, ref bool found)
    {
        foreach (Inline inline in inlines)
        {
            if (found)
                return;

            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case InlineUIContainer { Child: InlineNoteImageElement }:
                    found = true;
                    return;
                case Span span:
                    CollectTextBeforeInInlines(span.Inlines, builder, ref found);
                    break;
            }
        }
    }

    private static string GetRunsText(FlowDocument document)
    {
        var builder = new System.Text.StringBuilder();
        foreach (Block block in document.Blocks)
            AppendRunsInBlock(block, builder);
        return builder.ToString();
    }

    private static void AppendRunsInBlock(Block block, System.Text.StringBuilder builder)
    {
        switch (block)
        {
            case Paragraph paragraph:
                AppendRunsInInlines(paragraph.Inlines, builder);
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                    AppendRunsInBlock(child, builder);
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                        AppendRunsInBlock(child, builder);
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                                AppendRunsInBlock(child, builder);
                        }
                    }
                }
                break;
        }
    }

    private static void AppendRunsInInlines(InlineCollection inlines, System.Text.StringBuilder builder)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case Span span:
                    AppendRunsInInlines(span.Inlines, builder);
                    break;
            }
        }
    }

    private static System.Collections.Generic.List<Paragraph> FindParagraphs(FlowDocument document)
    {
        var result = new System.Collections.Generic.List<Paragraph>();
        foreach (Block block in document.Blocks)
            CollectParagraphs(block, result);
        return result;
    }

    private static void CollectParagraphs(Block block, System.Collections.Generic.List<Paragraph> result)
    {
        switch (block)
        {
            case Paragraph paragraph:
                result.Add(paragraph);
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                    CollectParagraphs(child, result);
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                        CollectParagraphs(child, result);
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                                CollectParagraphs(child, result);
                        }
                    }
                }
                break;
        }
    }

    private static System.Collections.Generic.List<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new System.Collections.Generic.List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                result.Add(typed);
            result.AddRange(FindVisualChildren<T>(child));
        }

        return result;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
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
        done.Wait();

        if (failure is not null)
            throw failure;
    }
}
