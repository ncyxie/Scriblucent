using System.IO;
using System.Linq;
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
    public void ResizeInlineImage_UpdatesSessionAndAttachment()
        => RunOnSta(RunResizeScenario);

    [Fact]
    public void SaveLoadRoundTrip_PreservesAnchorAndText()
        => RunOnSta(RunRoundTripScenario);

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
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
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

        // Inline elements flip the same way when their anchor is at the very top.
        harness.Coordinator.SetWrapMode(topId!.Value, NoteImageWrapMode.Inline);
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();
        var topElement = FindAllInlineContainers(harness.Editor.Document)
            .Select(c => (InlineNoteImageElement)c.Child)
            .First(e => e.ImageId == topId);
        Assert.True(topElement.ButtonsFlipped);

        var midElement = FindAllInlineContainers(harness.Editor.Document)
            .Select(c => (InlineNoteImageElement)c.Child)
            .First(e => e.ImageId == harness.ImageId);
        Assert.False(midElement.ButtonsFlipped);
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

        // Opening the menu syncs the checkmarks from the current wrap mode.
        InvokeMenuOpening(element, menu);
        Assert.True(GetItem(menu, "Inline").IsChecked, "Inline should be checked when the menu opens");
        Assert.False(GetItem(menu, "Square").IsChecked);
        Assert.False(GetItem(menu, "TopAndBottom").IsChecked);
        Assert.False(GetItem(menu, "None").IsChecked);

        // Clicking "Square" converts to floating and moves the checkmark.
        GetItem(menu, "Square").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(NoteImageWrapMode.Square, element.WrapMode);
        Assert.Single(harness.Overlay.Children); // converted to a floating widget
        Assert.True(GetItem(menu, "Square").IsChecked, "Square should be checked after the click");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        // Reopening keeps the checkmark on the active mode (via the menu's Opened backup too).
        InvokeMenuOpening(element, menu);
        Assert.True(GetItem(menu, "Square").IsChecked, "Square should stay checked on reopen");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(GetItem(menu, "Square").IsChecked, "Opened-event backup must keep the checkmark");
        Assert.False(GetItem(menu, "Inline").IsChecked);

        // Radio semantics: clicking the already-active item keeps its checkmark.
        GetItem(menu, "Square").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        InvokeMenuOpening(element, menu);
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

    private static void InvokeMenuOpening(InlineNoteImageElement element, ContextMenu menu)
    {
        var method = typeof(InlineNoteImageElement).GetMethod(
            "ContextMenu_Opening",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(element, new object[] { menu, null! });
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
            range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
            range.Load(stream, DataFormats.XamlPackage);
            harness.Coordinator.Restore(attachments);
            harness.Editor.UpdateLayout();

            // Exactly one representation per session - no duplication, no leftover markers.
            var inlineCount = attachments.Count(a => a.WrapMode == NoteImageWrapMode.Inline);
            Assert.Equal(inlineCount, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount, harness.Overlay.Children.Count);
            Assert.DoesNotContain('\uFFFC', new TextRange(
                harness.Editor.Document.ContentStart,
                harness.Editor.Document.ContentEnd).Text);

            // Floating -> inline and back again: never two representations.
            var anyId = harness.Coordinator.BuildAttachments().First().Id;
            harness.Coordinator.SetWrapMode(anyId, NoteImageWrapMode.Square);
            harness.Editor.UpdateLayout();
            Assert.Equal(inlineCount - 1, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount + 1, harness.Overlay.Children.Count);

            var floatingId = harness.Coordinator.BuildAttachments()
                .First(a => a.WrapMode != NoteImageWrapMode.Inline).Id;
            harness.Coordinator.SetWrapMode(floatingId, NoteImageWrapMode.Inline);
            harness.Editor.UpdateLayout();
            Assert.Equal(inlineCount, FindAllInlineContainers(harness.Editor.Document).Count);
            Assert.Equal(attachments.Count - inlineCount, harness.Overlay.Children.Count);
        }
    }

    [Fact]
    public void ClickingInlineImage_SelectsIt()
        => RunOnSta(RunClickScenario);

    [Fact]
    public void InlineImage_UnderFixedLineSpacing_GrowsTheLine()
        => RunOnSta(RunLineGrowthScenario);

    [Fact]
    public void InlineImage_MenuDoesNotInheritParagraphLineHeight_AndHasChips()
        => RunOnSta(RunMenuAndChipsScenario);

    private static void RunMenuAndChipsScenario()
    {
        // The element lives inside the flow: the paragraph's LineHeight/BlockLineHeight must
        // NOT leak into the context menu (it would stretch every menu row), and the selection
        // chrome keeps the lock/delete chips of the floating widget.
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

        Assert.Contains(FindVisualChildren<Button>(element!), b => b.Name == "LockAspectButton");
        Assert.Contains(FindVisualChildren<Button>(element!), b => b.Name == "DeleteButton");

        // The lock chip toggles the session's aspect lock.
        Assert.True(harness.Coordinator.BuildAttachments().Single().LockAspectRatio);
        var lockButton = FindVisualChildren<Button>(element!).First(b => b.Name == "LockAspectButton");
        lockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(harness.Coordinator.BuildAttachments().Single().LockAspectRatio);
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
        Assert.Empty(harness.Overlay.Children); // no canvas widget anymore
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
        Assert.Single(harness.Overlay.Children);
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

    private static void RunResizeScenario()
    {
        using var harness = CreateHarness();
        harness.Coordinator.SetWrapMode(harness.ImageId, NoteImageWrapMode.Inline);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);

        // Grab the bottom-right resize thumb and simulate a drag.
        var thumb = FindVisualChildren<Thumb>(element!).First(t => Equals(t.Tag, "BR"));
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(40, 30) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

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
        range = new TextRange(harness.Editor.Document.ContentStart, harness.Editor.Document.ContentEnd);
        range.Load(stream, DataFormats.XamlPackage);
        harness.Coordinator.Restore(attachments);
        harness.Editor.UpdateLayout();

        var element = FindInlineElement(harness.Editor.Document);
        Assert.NotNull(element);
        Assert.Equal(beforeText, GetTextBeforeFirstInline(harness.Editor.Document));
        Assert.Equal(originalText, GetRunsText(harness.Editor.Document)); // no text loss, no leftover markers
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
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
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
