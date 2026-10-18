using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeDragScrolling(ItemsControl list, ScrollViewer viewer, CollectionSelectionDock dock,
        ICollectionSelectionScope selection, string capture)
    {
        var window = Window.GetWindow(list)!;
        var viewport = ControlTree(viewer).OfType<ScrollContentPresenter>().First();
        var handle = new WindowInteropHelper(window).Handle;
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previous);
        double previousOffset = viewer.VerticalOffset;
        double previousLeft = window.Left, previousTop = window.Top, previousHeight = window.Height;
        var report = new List<string>();
        try
        {
            // Leave actual desktop space above and below the fixture. Testing only
            // inside the viewport missed the old 24-DIP autoscroll cutoff.
            window.Left = SystemParameters.WorkArea.Left + 100;
            window.Top = SystemParameters.WorkArea.Top + 80;
            // WorkArea is in DIPs: a 900-DIP dashboard can otherwise extend below
            // the physical monitor at 125% scaling, making the requested point unreachable.
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height - 160);
            window.Activate(); SetForegroundWindow(handle); Pump(100);
            Require(GetForegroundWindow() == handle, "Native drag scrolling requires the fixture's interactive desktop.");
            foreach (bool multiple in new[] { false, true })
            {
                selection.End(); Pump(240);
                // Start away from either extent, with enough rows available in both directions.
                SmoothScrollBehavior.Interrupt(viewer);
                viewer.ScrollToHome(); Pump(240); window.UpdateLayout();
                FrameworkElement extent = list;
                for (var parent = VisualTreeHelper.GetParent(list); parent is not null && !ReferenceEquals(parent, viewer); parent = VisualTreeHelper.GetParent(parent))
                    if (parent is DashboardVirtualizationHost host) { extent = host; break; }
                // Dashboard grids represent a moving viewport slice; the host owns
                // the whole section's position, not the currently realized slice.
                double listTop = extent.TranslatePoint(new Point(), viewer).Y + viewer.VerticalOffset;
                viewer.ScrollToVerticalOffset(Math.Clamp(listTop + 360, 0, viewer.ScrollableHeight - 1600));
                Pump(240); window.UpdateLayout();
                Require(viewer.ScrollableHeight - viewer.VerticalOffset > 7500,
                    $"The drag-scroll fixture started too near the bottom: {viewer.VerticalOffset}/{viewer.ScrollableHeight}, section={listTop}.");
                var bounds = viewport.TransformToAncestor(window).TransformBounds(new Rect(viewport.RenderSize));
                var candidates = ItemReorderBehavior.Realized(list).Select(container =>
                {
                    var grip = ControlTree(container).OfType<ItemReorderGrip>().FirstOrDefault(button => button.IsVisible);
                    var rect = container.TransformToAncestor(window).TransformBounds(new Rect(container.RenderSize));
                    var point = grip is not null ? grip.TranslatePoint(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2), window)
                        : new Point(rect.Left + rect.Width * .4, Math.Min(rect.Bottom - 24, bounds.Bottom - 20));
                    return (container, grip, point, rect);
                }).Where(item => bounds.Contains(item.point) && item.rect.Contains(item.point)
                    && window.InputHitTest(item.point) is DependencyObject hit
                    && ReferenceEquals(ItemsControl.ContainerFromElement(list, hit), item.container))
                    .OrderByDescending(item => item.point.Y).ToArray();
                Require(candidates.Length >= 2, "No visible card available for the drag scrolling check.");
                var chosen = candidates[0];
                var moving = list.ItemContainerGenerator.ItemFromContainer(chosen.container);
                var frame = ControlTree(chosen.container).OfType<CollectionSelectionFrame>().First();
                if (multiple)
                {
                    selection.Begin(frame.ItemId);
                    selection.Toggle(ControlTree(candidates[1].container).OfType<CollectionSelectionFrame>().First().ItemId);
                    Pump(300); window.UpdateLayout();
                    Require(selection.Count == 2 && dock.IsVisible, "The selected-block drag has no floating island.");
                }
                var original = list.Items.Cast<object>().ToArray();
                FrameworkElement nativeSource = ItemReorderBehavior.GetKind(list) == ItemReorderKind.Flashcards ? frame
                    : moving is NoteCardViewModel ? ControlTree(chosen.container).OfType<NoteCardControl>().First()
                    : ControlTree(chosen.container).OfType<FrameworkElement>().First(item => ReferenceEquals(item.Tag, moving));
                var start = chosen.grip is not null ? chosen.grip.PointToScreen(new Point(chosen.grip.ActualWidth / 2, chosen.grip.ActualHeight / 2))
                    : window.PointToScreen(chosen.point);
                var bottom = viewport.PointToScreen(new Point(viewport.ActualWidth * .4, viewport.ActualHeight - 8));
                var middle = viewport.PointToScreen(new Point(viewport.ActualWidth * .4, viewport.ActualHeight / 2));
                var outsideBottom = window.PointToScreen(new Point(0, window.ActualHeight + 36));
                var outsideTop = window.PointToScreen(new Point(0, -36));
                outsideBottom.X = outsideTop.X = bottom.X;
                var island = multiple ? dock.PointToScreen(new Point(dock.ActualWidth / 2, dock.ActualHeight / 2)) : bottom;
                double startOffset = viewer.VerticalOffset, down = 0, pastIsland = 0, outsideStart = 0,
                    outsideEarly = 0, outsideLate = 0, up = 0, stopped = 0, wheel = 0;
                long insideBegan = 0, outsideBegan = 0, heldBegan = 0;
                double insideRate = 0, outsideRate = 0, heldRate = 0, heldLimit = 0;
                int phase = 0; bool started = false, cancel = false, islandCleared = false, physicallyOutside = false, outsideMarkerCleared = false;
                bool sourceReleased = false, returnedDimmed = false, unrelatedOpaque = false;
                QueryContinueDragEventHandler cancelDrag = (_, args) => { if (cancel) { args.Action = DragAction.Cancel; args.Handled = true; } };
                nativeSource.QueryContinueDrag += cancelDrag;
                var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
                using var watchdog = new System.Threading.Timer(_ => mouse_event(4, 0, 0, 0, UIntPtr.Zero), null, 10000, System.Threading.Timeout.Infinite);
                timer.Tick += (_, _) =>
                {
                    phase++;
                    if (phase == 1) MoveTestPointer(new Point(start.X + 12, start.Y));
                    if (phase == 2) { started = ItemReorderBehavior.IsDragging; insideBegan = Stopwatch.GetTimestamp(); MoveTestPointer(bottom); }
                    if (phase == 9)
                    {
                        down = viewer.VerticalOffset;
                        insideRate = (down - startOffset) / Stopwatch.GetElapsedTime(insideBegan).TotalSeconds;
                        MoveTestPointer(island);
                    }
                    if (phase == 13)
                    {
                        pastIsland = viewer.VerticalOffset;
                        islandCleared = !multiple || !dock.IsHitTestVisible && !dock.IsVisible;
                        MoveTestPointer(middle);
                    }
                    if (phase == 15) { outsideStart = viewer.VerticalOffset; outsideBegan = Stopwatch.GetTimestamp(); MoveTestPointer(outsideBottom); }
                    if (phase == 22)
                    {
                        outsideEarly = viewer.VerticalOffset;
                        heldBegan = Stopwatch.GetTimestamp();
                        outsideRate = (outsideEarly - outsideStart) / Stopwatch.GetElapsedTime(outsideBegan, heldBegan).TotalSeconds;
                        GetCursorPos(out var cursor);
                        physicallyOutside = cursor.Y > window.PointToScreen(new Point(0, window.ActualHeight)).Y + 24;
                        outsideMarkerCleared = !ItemReorderBehavior.IsInsertionActive;
                        sourceReleased = !frame.IsLoaded || !ReferenceEquals(frame.DataContext, moving);
                        var localBounds = viewport.TransformToAncestor(viewer).TransformBounds(new Rect(viewport.RenderSize));
                        heldLimit = Math.Abs(ItemDragScroll.TargetVelocity(viewer.PointFromScreen(outsideBottom), localBounds, 10));
                        report.Add($"Outside geometry: window={window.Left}/{window.Top}/{window.ActualWidth}/{window.ActualHeight}, state={window.WindowState}, requested={outsideBottom}, cursor={cursor.X}/{cursor.Y}, bottom={window.PointToScreen(new Point(0, window.ActualHeight))}, viewport={bounds}.");
                    }
                    if (phase == 29)
                    {
                        outsideLate = viewer.VerticalOffset;
                        heldRate = (outsideLate - outsideEarly) / Stopwatch.GetElapsedTime(heldBegan).TotalSeconds;
                        MoveTestPointer(outsideTop);
                    }
                    if (phase == 36) { up = viewer.VerticalOffset; MoveTestPointer(middle); }
                    if (phase == 39) stopped = viewer.VerticalOffset;
                    if (phase == 42)
                    {
                        if (Math.Abs(viewer.VerticalOffset - stopped) > 2) report.Add("Unexpected centre drift before wheel input.");
                        mouse_event(0x0800, 0, 0, unchecked((uint)-240), UIntPtr.Zero);
                    }
                    if (phase == 46)
                    {
                        wheel = viewer.VerticalOffset;
                        // Return to the original item without ending the actual OLE drag.
                        viewer.ScrollToVerticalOffset(startOffset);
                    }
                    if (phase == 49)
                    {
                        var returned = list.ItemContainerGenerator.ContainerFromItem(moving) as FrameworkElement;
                        var returnedFrame = returned is null ? null : ControlTree(returned).OfType<CollectionSelectionFrame>().FirstOrDefault();
                        double expected = UiMotion.PrefersReducedMotion ? 1 : moving is NoteCardViewModel ? .22 : .18;
                        returnedDimmed = returnedFrame is { IsLoaded: true } && Math.Abs(returnedFrame.Opacity - expected) < .001;
                        unrelatedOpaque = ControlTree(list).OfType<CollectionSelectionFrame>()
                            .Where(item => item.IsLoaded && !ReferenceEquals(item.DataContext, moving)).All(item => item.Opacity == 1);
                        cancel = true; MoveTestPointer(new Point(middle.X + 2, middle.Y));
                    }
                    if (phase >= 52) { timer.Stop(); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
                };
                try
                {
                    SetCursorPos((int)start.X, (int)start.Y); Pump(40);
                    Require(chosen.grip?.IsMouseOver ?? chosen.container.IsMouseOver,
                        $"The native scroll probe missed its source at {start}; hit={Mouse.DirectlyOver}.");
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); Pump(30);
                    timer.Start(); Pump(6200); Pump(350); window.UpdateLayout();
                    File.WriteAllLines(Path.Combine(_output, capture + "-progress.txt"), report.Append($"selected={multiple}, phases={phase}, started={started}, outside={physicallyOutside}, rates={insideRate}/{outsideRate}/{heldRate}, extent={viewer.ScrollableHeight}, offsets={startOffset}/{down}/{pastIsland}/{outsideStart}/{outsideEarly}/{outsideLate}/{up}/{stopped}/{wheel}"));
                    Require(started && down > startOffset + 80 && pastIsland > down + 35 && up < outsideLate - 80,
                        $"Drag scrolling stopped or failed to reverse ({capture}, selected={multiple}): {startOffset} -> {down} -> {pastIsland} -> {outsideLate} -> {up}, started={started}.");
                    Require(physicallyOutside && outsideRate > insideRate * 1.5,
                        $"Pushing beyond the window did not increase drag scrolling speed: inside={insideRate:F0} DIP/s, outside={outsideRate:F0} DIP/s, actual outside={physicallyOutside}.");
                    Require(outsideMarkerCleared, "Scrolling outside the window retained an invalid insertion marker.");
                    // Reduced motion applies speed immediately and can already reach
                    // the ceiling in the first interval; further acceleration is then bounded.
                    Require(heldRate > outsideRate * 1.1 || outsideRate >= heldLimit * .9 && heldRate >= heldLimit * .9,
                        $"Holding at the outside edge did not accelerate or sustain its ceiling: early={outsideRate:F0}, held={heldRate:F0}, ceiling={heldLimit:F0} DIP/s.");
                    Require(!report.Contains("Unexpected centre drift before wheel input."), "Drag scrolling continued in the middle of the viewport.");
                    Require(wheel > stopped + 30, $"Mouse wheel did not scroll during an active drag: {stopped} -> {wheel}.");
                    Require(islandCleared, "The floating selection island obstructed an active drag.");
                    Require(sourceReleased && returnedDimmed && unrelatedOpaque,
                        $"Source dimming did not survive a held-drag round trip ({capture}, selected={multiple}): recycled={sourceReleased}, returned dimmed={returnedDimmed}, unrelated opaque={unrelatedOpaque}.");
                    Require(!ItemReorderBehavior.IsDragging && !ItemReorderBehavior.IsInsertionActive && Mouse.Captured is null,
                        "Cancelling edge scrolling left drag, insertion or mouse-capture state behind.");
                    Require(original.SequenceEqual(list.Items.Cast<object>()), "The cancelled scrolling gesture changed authored order.");
                    Require(!multiple || selection.Count == 2 && dock.IsVisible && dock.IsHitTestVisible, "Selection/island did not return after dragging.");
                    Require(ControlTree(list).OfType<CollectionSelectionFrame>().All(item => item.Opacity == 1), "Cancelling left a card dimmed.");
                    if (multiple) ProbeWheelOverIsland(viewer, dock);
                    report.Add($"{(multiple ? "Two cards" : "One card")}: down {down - startOffset:F0} DIP; continued through island {pastIsland - down:F0} DIP; speed inside {insideRate:F0}, outside {outsideRate:F0}, held {heldRate:F0} DIP/s; reversed outside top {outsideLate - up:F0} DIP; wheel {wheel - stopped:F0} DIP; source recycled and returned with correct dimming; unrelated cards opaque; cancelled cleanly.");
                }
                finally { timer.Stop(); ReleaseTestMouse(); nativeSource.QueryContinueDrag -= cancelDrag; }
            }
            File.WriteAllLines(Path.Combine(_output, capture + "-scroll.txt"), report);
        }
        finally
        {
            selection.End(); ReleaseTestMouse(); SetCursorPos(previous.X, previous.Y);
            window.Left = previousLeft; window.Top = previousTop; window.Height = previousHeight;
            viewer.ScrollToVerticalOffset(previousOffset); Pump(220);
            if (previousWindow != IntPtr.Zero) SetForegroundWindow(previousWindow);
        }
    }

    private static void ReleaseTestMouse()
    {
        // Repeating a synthetic button-up over a card can look like a fresh click to WPF.
        if ((NativeGetAsyncKeyState(1) & 0x8000) != 0) mouse_event(4, 0, 0, 0, UIntPtr.Zero);
    }
    private static void MoveTestPointer(Point point)
    {
        // SetCursorPos can synchronously enter the native drag loop on this UI thread.
        // Physical input must originate independently of the timer driving the scenario.
        System.Threading.ThreadPool.QueueUserWorkItem(_ => SetCursorPos((int)point.X, (int)point.Y));
    }
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short NativeGetAsyncKeyState(int key);

    private static void ProbeWheelOverIsland(ScrollViewer viewer, CollectionSelectionDock dock)
    {
        double before = viewer.VerticalOffset;
        var button = (Button)dock.FindName("DoneButton");
        var down = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = button };
        button.RaiseEvent(down); Pump(600);
        Require(down.Handled && viewer.VerticalOffset > before + 20, "The floating island swallowed downward wheel input.");
        double after = viewer.VerticalOffset;
        var up = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = button };
        button.RaiseEvent(up); Pump(600);
        Require(up.Handled && viewer.VerticalOffset < after - 20, "The floating island swallowed upward wheel input.");
        Require(!SmoothScrollCompositor.IsActive(viewer) && !ScrollTileBehavior.GetIsScrolling(viewer), "Wheel input over the island left scrolling active.");
    }
}
