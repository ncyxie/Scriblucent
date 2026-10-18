using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Behaviors;

/// <summary>
/// Edge-style smooth scrolling for mouse wheel and scrollbar page/track jumps on <see cref="ScrollViewer"/> instances.
/// Middle-click autoscroll is handled separately by <see cref="MiddleClickScrollBehavior"/>.
/// </summary>
public static class SmoothScrollBehavior
{
    private const double WheelViewportRatio = 0.23;

    // Exponential "approach the target" smoothing instead of a fixed-duration eased tween: a
    // fixed tween restarts its ease curve on every wheel tick, causing a visible velocity
    // discontinuity with real (uneven) wheel input. Exponential smoothing has no "restart" -
    // current velocity carries over continuously and composes across variable frame deltas.
    private const double SmoothingRatePerMs = 0.014979; // ln(20)/200: ~95% settled after 200ms
    // Opening speed of one notch. The uncapped curve is fastest on frame one (the shove).
    // A longer gesture queues more distance and this ceiling rises with it, so a flick
    // speeds up without the flat cap that felt like lag.
    private const double WheelCoastViewportsPerSecond = 1.7;
    private const double WheelAccelViewportsPerSecond = 5.4;
    private const double DoneEpsilonPixels = 0.3;
    private const double MinFrameSeconds = 1.0 / 240.0;
    // A maximized window composites more cards per frame, so a late render tick (>22ms) is
    // common there, not pathological. Clamping delta below the real elapsed time made the
    // animation under-advance and visibly lag; 200ms covers realistic late-tick ranges while
    // still snapping near-instantly for a true multi-hundred-ms stall.
    private const double MaxFrameSeconds = 1.0 / 5.0;
    private const double DirectScrollStepThreshold = 24;
    // A UI-thread stall (e.g. a big virtualization realize pass) can queue several wheel
    // impulses; with no bound, the target gets pushed far ahead and the post-stall snap
    // triggers another expensive realize pass, which can starve the thread again - a
    // self-reinforcing cascade. The cap must stay close to what the realized-card cache can
    // absorb via recycling so each post-stall snap stays cheap. Wide layouts (many ~244px
    // Notes columns) scale the lead down so the visual target cannot run as far ahead of
    // realizes when each viewport of content is denser.
    private const double MaxImpulseLeadViewports = 0.6;
    private const double MinImpulseLeadPixels = 240;
    private const double DenseViewportWidthThreshold = 1200;
    private const double DenseImpulseLeadFactor = 0.8;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(null));

    private static readonly List<State> ActiveStates = new();
    private static EventHandler? _renderingHandler;
    private static TimeSpan _lastGlobalRenderingTime = TimeSpan.MinValue;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Category navigation shares the wheel controller and can be interrupted by normal input.</summary>
    internal static void ScrollTo(ScrollViewer viewer, double verticalOffset)
    {
        SetIsEnabled(viewer, true);
        var state = GetOrCreateState(viewer);
        if (state is null) return;
        StopAnimating(state);
        SyncFromViewer(state);
        if (!MotionPolicy.Spatial) { ApplyOffset(state, state.CurrentX, Clamp(verticalOffset, 0, viewer.ScrollableHeight)); return; }
        state.ProgrammaticTarget = true;
        StartImpulseTo(state, true, verticalOffset);
        EnsureAnimating(state);
    }

    internal static void CancelTarget(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is not State { ProgrammaticTarget: true } state) return;
        // WPF queues ScrollTo offsets until layout. Replace the last queued command
        // with the currently visible offset, so cancellation cannot advance a frame later.
        var x = viewer.HorizontalOffset; var y = viewer.VerticalOffset;
        StopAnimating(state); ApplyOffset(state, x, y);
    }

    /// <summary>End a glide at its visible position before routing a card interaction.</summary>
    internal static void Interrupt(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is not State state || !state.ScrollAnimationSessionActive) return;
        var x = state.CurrentX; var y = state.CurrentY;
        StopAnimating(state); ApplyOffset(state, x, y);
    }

    internal static void SetExternalDrive(ScrollViewer scrollViewer, bool active)
    {
        if (scrollViewer.GetValue(StateProperty) is State state)
            state.ExternalDrive = active;
    }

    internal static void NotifyOffsetChanged(ScrollViewer scrollViewer, double x, double y)
    {
        if (scrollViewer.GetValue(StateProperty) is not State state)
            return;

        state.CurrentX = x;
        state.CurrentY = y;
    }

    private sealed class State
    {
        public required ScrollViewer ScrollViewer { get; init; }
        public bool IsAttached;
        public bool ExternalDrive;
        public bool ProgrammaticTarget;
        public double CurrentX;
        public double CurrentY;
        public double ImpulseTargetX;
        public double ImpulseTargetY;
        public bool ImpulseActiveX;
        public bool ImpulseActiveY;
        public bool DirectThumbDrag;
        public bool ApplyingOffset;
        public bool ScrollAnimationSessionActive;
        public TimeSpan LastTickRenderingTime = TimeSpan.MinValue;
        public ScrollBar? VerticalScrollBar;
        public ScrollBar? HorizontalScrollBar;
        public List<(ScrollBar Bar, MouseWheelEventHandler Handler)> ExternalScrollBars { get; } = new();
        public MouseWheelEventHandler? MouseWheelHandler;
        public ScrollChangedEventHandler? ScrollChangedHandler;
        public SizeChangedEventHandler? SizeChangedHandler;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public Window? OwnerWindow;
        public EventHandler? OwnerClosedHandler;
        public MouseButtonEventHandler? VerticalBarMouseDownHandler;
        public MouseButtonEventHandler? HorizontalBarMouseDownHandler;

        public bool NeedsAnimation => ImpulseActiveX || ImpulseActiveY;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if (e.NewValue is true)
            Attach(scrollViewer);
        else
            Detach(scrollViewer);
    }

    private static void Attach(ScrollViewer scrollViewer)
    {
        if (GetOrCreateState(scrollViewer) is not { IsAttached: false } state)
            return;

        state.MouseWheelHandler = (_, args) => OnPreviewMouseWheel(state, args);
        state.ScrollChangedHandler = (_, args) => OnScrollChanged(state, args);
        state.SizeChangedHandler = (_, _) => OnViewerSizeChanged(state);
        state.LoadedHandler = (_, _) => OnLoaded(state);
        state.UnloadedHandler = (_, _) => { StopAnimating(state); DetachScrollbarHooks(state); };

        scrollViewer.PreviewMouseWheel += state.MouseWheelHandler;
        scrollViewer.ScrollChanged += state.ScrollChangedHandler;
        scrollViewer.SizeChanged += state.SizeChangedHandler;
        scrollViewer.Loaded += state.LoadedHandler;
        scrollViewer.Unloaded += state.UnloadedHandler;

        if (scrollViewer.IsLoaded)
            OnLoaded(state);

        state.IsAttached = true;
    }

    private static void Detach(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        StopAnimating(state);

        if (state.MouseWheelHandler != null)
            scrollViewer.PreviewMouseWheel -= state.MouseWheelHandler;
        if (state.ScrollChangedHandler != null)
            scrollViewer.ScrollChanged -= state.ScrollChangedHandler;
        if (state.SizeChangedHandler != null)
            scrollViewer.SizeChanged -= state.SizeChangedHandler;
        if (state.LoadedHandler != null)
            scrollViewer.Loaded -= state.LoadedHandler;

        scrollViewer.Unloaded -= state.UnloadedHandler;
        if (state.OwnerWindow is { } owner) owner.Closed -= state.OwnerClosedHandler;
        state.OwnerWindow = null;
        DetachScrollbarHooks(state);

        foreach (var (bar, handler) in state.ExternalScrollBars)
            bar.PreviewMouseWheel -= handler;
        state.ExternalScrollBars.Clear();

        state.IsAttached = false;
    }

    private static void OnLoaded(State state)
    {
        if (Window.GetWindow(state.ScrollViewer) is { } owner && !ReferenceEquals(owner, state.OwnerWindow))
        {
            if (state.OwnerWindow is { } previous) previous.Closed -= state.OwnerClosedHandler;
            state.OwnerWindow = owner;
            owner.Closed += state.OwnerClosedHandler = (_, _) => StopAnimating(state);
        }
        SyncFromViewer(state);
        AttachScrollbarHooks(state);
    }

    private static void OnViewerSizeChanged(State state)
    {
        RefreshImpulseTargets(state);

        if (!state.NeedsAnimation)
            SyncFromViewer(state);
    }

    private static void AttachScrollbarHooks(State state)
    {
        DetachScrollbarHooks(state);

        state.ScrollViewer.ApplyTemplate();
        state.VerticalScrollBar =
            state.ScrollViewer.Template.FindName("PART_VerticalScrollBar", state.ScrollViewer) as ScrollBar;
        state.HorizontalScrollBar =
            state.ScrollViewer.Template.FindName("PART_HorizontalScrollBar", state.ScrollViewer) as ScrollBar;

        if (state.VerticalScrollBar != null)
        {
            state.VerticalBarMouseDownHandler = (_, args) => OnScrollBarMouseDown(state, args, vertical: true);
            state.VerticalScrollBar.PreviewMouseLeftButtonDown += state.VerticalBarMouseDownHandler;
            state.VerticalScrollBar.PreviewMouseLeftButtonUp += (_, _) => state.DirectThumbDrag = false;
            state.VerticalScrollBar.LostMouseCapture += (_, _) => state.DirectThumbDrag = false;
        }

        if (state.HorizontalScrollBar != null)
        {
            state.HorizontalBarMouseDownHandler = (_, args) => OnScrollBarMouseDown(state, args, vertical: false);
            state.HorizontalScrollBar.PreviewMouseLeftButtonDown += state.HorizontalBarMouseDownHandler;
            state.HorizontalScrollBar.PreviewMouseLeftButtonUp += (_, _) => state.DirectThumbDrag = false;
            state.HorizontalScrollBar.LostMouseCapture += (_, _) => state.DirectThumbDrag = false;
        }
    }

    private static void DetachScrollbarHooks(State state)
    {
        if (state.VerticalScrollBar != null)
        {
            if (state.VerticalBarMouseDownHandler != null)
                state.VerticalScrollBar.PreviewMouseLeftButtonDown -= state.VerticalBarMouseDownHandler;
        }

        if (state.HorizontalScrollBar != null)
        {
            if (state.HorizontalBarMouseDownHandler != null)
                state.HorizontalScrollBar.PreviewMouseLeftButtonDown -= state.HorizontalBarMouseDownHandler;
        }

        state.VerticalScrollBar = null;
        state.HorizontalScrollBar = null;
        state.VerticalBarMouseDownHandler = null;
        state.HorizontalBarMouseDownHandler = null;
    }

    private static void OnScrollBarMouseDown(State state, MouseButtonEventArgs e, bool vertical)
    {
        var bar = vertical ? state.VerticalScrollBar : state.HorizontalScrollBar;
        if (bar != null && IsScrollbarThumb(bar, e))
            state.DirectThumbDrag = true;
    }

    private static bool IsScrollbarThumb(ScrollBar bar, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return false;

        for (var current = source; current != null; current = GetParentObject(current))
        {
            if (current is Thumb)
                return true;
            if (ReferenceEquals(current, bar))
                return false;
        }

        return false;
    }

    private static bool IsMouseOverScrollbar(State state)
        => state.VerticalScrollBar?.IsMouseOver == true || state.HorizontalScrollBar?.IsMouseOver == true;

    private static void OnPreviewMouseWheel(State state, MouseWheelEventArgs e)
    {
        if (e.Handled || state.ExternalDrive)
            return;

        // Hovering a specific scrollbar must drive that bar's axis — even if nested content
        // would otherwise own the wheel, or vertical scroll would normally win.
        if (TryScrollHoveredScrollbar(state, e))
            return;

        if (!CanScroll(state.ScrollViewer))
            return;

        // Nested ScrollViewers: tunnel hits the outer viewer first. Defer when the cursor is
        // over a deeper scrollable viewer so wheel targets what the user is actually hovering.
        if (ShouldDeferToDescendantScrollViewer(state.ScrollViewer, e))
            return;

        // Ctrl+wheel is reserved for zoom (e.g. mind-map canvas); do not consume it for scrolling.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || Keyboard.IsKeyDown(Key.LeftCtrl)
            || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            return;
        }

        e.Handled = true;
        var (deltaX, deltaY) = ComputeWheelDelta(state.ScrollViewer, e);
        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
        {
            Scriblucent.Services.ScrollPerfDiag.Log(
                $"wheel\tdy={deltaY:F0}\tscrollable={state.ScrollViewer.ScrollableHeight:F0}\tviewport={state.ScrollViewer.ViewportHeight:F0}x{state.ScrollViewer.ViewportWidth:F0}");
        }

        ApplyWheelDeltas(state, deltaX, deltaY);
    }

    /// <summary>
    /// If the pointer is over this viewer's vertical or horizontal scrollbar, apply wheel to
    /// that axis only and mark handled.
    /// </summary>
    private static bool TryScrollHoveredScrollbar(State state, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return false;

        ScrollBar? hoveredBar = null;
        for (var current = source; current != null; current = GetParentObject(current))
        {
            if (current is ScrollBar bar)
            {
                hoveredBar = bar;
                break;
            }

            if (ReferenceEquals(current, state.ScrollViewer))
                break;
        }

        if (hoveredBar is null)
            return false;

        // Nested viewers: their scrollbars are visual descendants of the outer ScrollViewer.
        // Only handle when this viewer is the immediate ScrollViewer owner of the bar.
        if (!ReferenceEquals(FindOwningScrollViewer(hoveredBar), state.ScrollViewer))
            return false;

        var ticks = e.Delta / 120.0;
        double deltaX = 0;
        double deltaY = 0;

        if (hoveredBar.Orientation == Orientation.Horizontal && state.ScrollViewer.ScrollableWidth > 0)
            deltaX = -ticks * Math.Max(1, state.ScrollViewer.ViewportWidth) * WheelViewportRatio;
        else if (hoveredBar.Orientation == Orientation.Vertical && state.ScrollViewer.ScrollableHeight > 0)
            deltaY = -ticks * Math.Max(1, state.ScrollViewer.ViewportHeight) * WheelViewportRatio;
        else
            return false;

        e.Handled = true;
        ApplyWheelDeltas(state, deltaX, deltaY);
        return true;
    }

    private static ScrollViewer? FindOwningScrollViewer(DependencyObject from)
    {
        for (var current = GetParentObject(from); current != null; current = GetParentObject(current))
        {
            if (current is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }

    /// <summary>
    /// Registers a standalone scrollbar that is not one of the viewer's template bars (e.g.
    /// the note editor's unscaled mirror scrollbars) so hovering it and using the mouse
    /// wheel drives the viewer's smooth scroll on that bar's axis - the same behavior the
    /// viewer's own scrollbars have.
    /// </summary>
    internal static void RegisterExternalBar(ScrollViewer scrollViewer, ScrollBar bar)
    {
        if (bar is null)
            return;

        var state = GetOrCreateState(scrollViewer);
        if (state.ExternalScrollBars.Exists(entry => ReferenceEquals(entry.Bar, bar)))
            return;

        MouseWheelEventHandler handler = (_, args) => OnExternalScrollBarWheel(state, bar, args);
        state.ExternalScrollBars.Add((bar, handler));
        bar.PreviewMouseWheel += handler;
    }

    private static void OnExternalScrollBarWheel(State state, ScrollBar bar, MouseWheelEventArgs e)
    {
        if (e.Handled || state.ExternalDrive || !CanScroll(state.ScrollViewer))
            return;

        var ticks = e.Delta / 120.0;
        double deltaX = 0;
        double deltaY = 0;

        if (bar.Orientation == Orientation.Horizontal && state.ScrollViewer.ScrollableWidth > 0)
            deltaX = -ticks * Math.Max(1, state.ScrollViewer.ViewportWidth) * WheelViewportRatio;
        else if (bar.Orientation == Orientation.Vertical && state.ScrollViewer.ScrollableHeight > 0)
            deltaY = -ticks * Math.Max(1, state.ScrollViewer.ViewportHeight) * WheelViewportRatio;
        else
            return;

        e.Handled = true;
        ApplyWheelDeltas(state, deltaX, deltaY);
    }

    /// <summary>
    /// Walk parents from wheel/mouse hit-test sources. FlowDocument content (<see cref="System.Windows.Documents.Run"/>,
    /// etc.) is not a <see cref="Visual"/>, so <see cref="VisualTreeHelper.GetParent"/> throws —
    /// fall back to the logical tree for those nodes.
    /// </summary>
    private static DependencyObject? GetParentObject(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);

        return LogicalTreeHelper.GetParent(current);
    }

    private static void ApplyWheelDeltas(State state, double deltaX, double deltaY)
    {
        if (state.ProgrammaticTarget) { StopAnimating(state); SyncFromViewer(state); }
        if (deltaX == 0 && deltaY == 0)
            return;

        if (!state.ImpulseActiveY && !state.ImpulseActiveX)
            SyncFromViewer(state);

        if (ReducedMotionPreferences.ShouldReduceMotion)
        {
            ApplyOffset(
                state,
                Clamp(state.CurrentX + deltaX, 0, state.ScrollViewer.ScrollableWidth),
                Clamp(state.CurrentY + deltaY, 0, state.ScrollViewer.ScrollableHeight));
            return;
        }

        if (deltaY != 0)
            AddImpulse(state, vertical: true, deltaY);
        if (deltaX != 0)
            AddImpulse(state, vertical: false, deltaX);

        EnsureAnimating(state);
    }

    /// <summary>
    /// True when the wheel hit-test lives under a descendant <see cref="ScrollViewer"/> that can
    /// still scroll — including when the cursor is on that descendant's scrollbar.
    /// </summary>
    private static bool ShouldDeferToDescendantScrollViewer(ScrollViewer self, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return false;

        for (var current = source; current != null; current = GetParentObject(current))
        {
            if (ReferenceEquals(current, self))
                return false;

            if (current is ScrollViewer nested && CanScroll(nested))
                return true;
        }

        return false;
    }

    private static (double DeltaX, double DeltaY) ComputeWheelDelta(ScrollViewer scrollViewer, MouseWheelEventArgs e)
    {
        var ticks = e.Delta / 120.0;
        var horizontalPrimary = scrollViewer.ScrollableHeight <= 0 && scrollViewer.ScrollableWidth > 0;
        var shiftHorizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && scrollViewer.ScrollableWidth > 0;

        double deltaY = 0;
        double deltaX = 0;

        if (horizontalPrimary || shiftHorizontal)
            deltaX = -ticks * Math.Max(1, scrollViewer.ViewportWidth) * WheelViewportRatio;
        else if (scrollViewer.ScrollableHeight > 0)
            deltaY = -ticks * Math.Max(1, scrollViewer.ViewportHeight) * WheelViewportRatio;

        return (deltaX, deltaY);
    }

    private static void AddImpulse(State state, bool vertical, double delta)
    {
        var leadFactor = ResolveImpulseLeadFactor(state.ScrollViewer);
        if (vertical)
        {
            var baseTarget = state.ImpulseActiveY ? state.ImpulseTargetY : state.CurrentY;
            var maxLead = Math.Max(
                state.ScrollViewer.ViewportHeight * MaxImpulseLeadViewports * leadFactor,
                MinImpulseLeadPixels);
            var proposed = Clamp(baseTarget + delta, state.CurrentY - maxLead, state.CurrentY + maxLead);
            state.ImpulseTargetY = Clamp(proposed, 0, state.ScrollViewer.ScrollableHeight);
            state.ImpulseActiveY = true;
            return;
        }

        var baseTargetX = state.ImpulseActiveX ? state.ImpulseTargetX : state.CurrentX;
        var maxLeadX = Math.Max(
            state.ScrollViewer.ViewportWidth * MaxImpulseLeadViewports * leadFactor,
            MinImpulseLeadPixels);
        var proposedX = Clamp(baseTargetX + delta, state.CurrentX - maxLeadX, state.CurrentX + maxLeadX);
        state.ImpulseTargetX = Clamp(proposedX, 0, state.ScrollViewer.ScrollableWidth);
        state.ImpulseActiveX = true;
    }

    private static double ResolveImpulseLeadFactor(ScrollViewer scrollViewer)
        => scrollViewer.ViewportWidth >= DenseViewportWidthThreshold
            ? DenseImpulseLeadFactor
            : 1.0;

    private static void OnScrollChanged(State state, ScrollChangedEventArgs e)
    {
        if (state.ApplyingOffset || state.ExternalDrive)
            return;

        if (state.ImpulseActiveX || state.ImpulseActiveY)
            return;

        if (state.DirectThumbDrag)
        {
            SyncFromViewer(state);
            return;
        }

        if (ReducedMotionPreferences.ShouldReduceMotion)
        {
            SyncFromViewer(state);
            return;
        }

        var jumpedY = Math.Abs(e.VerticalChange) > 0.01;
        var jumpedX = Math.Abs(e.HorizontalChange) > 0.01;
        if (!jumpedY && !jumpedX)
            return;

        if (Math.Abs(e.VerticalChange) < DirectScrollStepThreshold
            && Math.Abs(e.HorizontalChange) < DirectScrollStepThreshold)
        {
            SyncFromViewer(state);
            return;
        }

        if (!IsMouseOverScrollbar(state))
        {
            SyncFromViewer(state);
            return;
        }

        var targetY = state.ScrollViewer.VerticalOffset;
        var targetX = state.ScrollViewer.HorizontalOffset;
        ApplyOffset(state, state.CurrentX, state.CurrentY);

        if (jumpedY)
            StartImpulseTo(state, vertical: true, targetY);
        if (jumpedX)
            StartImpulseTo(state, vertical: false, targetX);

        EnsureAnimating(state);
    }

    private static void StartImpulseTo(State state, bool vertical, double target)
    {
        if (vertical)
        {
            state.ImpulseTargetY = Clamp(target, 0, state.ScrollViewer.ScrollableHeight);
            state.ImpulseActiveY = true;
            return;
        }

        state.ImpulseTargetX = Clamp(target, 0, state.ScrollViewer.ScrollableWidth);
        state.ImpulseActiveX = true;
    }

    private static void RefreshImpulseTargets(State state)
    {
        if (state.ImpulseActiveY)
            state.ImpulseTargetY = Clamp(state.ImpulseTargetY, 0, state.ScrollViewer.ScrollableHeight);

        if (state.ImpulseActiveX)
            state.ImpulseTargetX = Clamp(state.ImpulseTargetX, 0, state.ScrollViewer.ScrollableWidth);
    }

    private static void EnsureAnimating(State state)
    {
        if (!state.NeedsAnimation || ActiveStates.Contains(state))
            return;

        BeginScrollAnimationSession(state);
        state.LastTickRenderingTime = TimeSpan.MinValue;
        ActiveStates.Add(state);
        if (_renderingHandler == null)
        {
            _renderingHandler = OnRendering;
            RenderTransitionClock.AddFrameObserver(_renderingHandler);
        }
    }

    private static void StopAnimating(State state)
    {
        state.ProgrammaticTarget = false;
        ActiveStates.Remove(state);
        state.ImpulseActiveX = false;
        state.ImpulseActiveY = false;
        state.LastTickRenderingTime = TimeSpan.MinValue;
        EndScrollAnimationSession(state);

        if (ActiveStates.Count == 0 && _renderingHandler != null)
        {
            RenderTransitionClock.RemoveFrameObserver(_renderingHandler);
            _renderingHandler = null;
            // Deliberately NOT resetting _lastGlobalRenderingTime here: the frame-diag log
            // must stay continuous across sessions (a reset makes every session's first
            // callback skip the frame sample, which blanked the whole diag frame stream for
            // the scripted wheel probes).
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs renderingArgs
            && renderingArgs.RenderingTime == _lastGlobalRenderingTime)
            return;

        if (e is RenderingEventArgs re)
        {
            if (Scriblucent.Services.ScrollPerfDiag.Enabled && _lastGlobalRenderingTime != TimeSpan.MinValue)
            {
                var frameMs = (re.RenderingTime - _lastGlobalRenderingTime).TotalMilliseconds;
                Scriblucent.Services.ScrollPerfDiag.LogFrame(
                    frameMs,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2),
                    Scriblucent.Controls.DashboardScrollSurfaceCache.AppliedCount,
                    Scriblucent.Controls.DashboardScrollSurfaceCache.PendingCount);
            }

            _lastGlobalRenderingTime = re.RenderingTime;
        }

        for (var i = ActiveStates.Count - 1; i >= 0; i--)
        {
            var state = ActiveStates[i];
            var deltaSeconds = ResolveDeltaSeconds(state, e);
            Tick(state, deltaSeconds);

            if (!state.NeedsAnimation)
            {
                ActiveStates.RemoveAt(i);
                CompleteImpulse(state);
                EndScrollAnimationSession(state);
            }
        }

        if (ActiveStates.Count == 0 && _renderingHandler != null)
        {
            RenderTransitionClock.RemoveFrameObserver(_renderingHandler);
            _renderingHandler = null;
            // Also no reset here (see the StopAnimating comment): the frame-diag stream must
            // stay continuous across sessions, or per-tick wheel sessions never log a frame.
        }
    }

    private static double ResolveDeltaSeconds(State state, EventArgs e)
    {
        if (e is not RenderingEventArgs re)
            return 1.0 / 60.0;

        double deltaSeconds = 1.0 / 60.0;
        if (state.LastTickRenderingTime != TimeSpan.MinValue)
        {
            deltaSeconds = (re.RenderingTime - state.LastTickRenderingTime).TotalSeconds;
            deltaSeconds = Math.Clamp(deltaSeconds, MinFrameSeconds, MaxFrameSeconds);
        }

        state.LastTickRenderingTime = re.RenderingTime;
        return deltaSeconds;
    }

    private static void Tick(State state, double deltaSeconds)
    {
        RefreshImpulseTargets(state);

        var compositorActive = SmoothScrollCompositor.IsActive(state.ScrollViewer);
        var smoothing = 1.0 - Math.Exp(-SmoothingRatePerMs * deltaSeconds * 1000.0);
        var previousY = state.CurrentY;

        if (state.ImpulseActiveY)
        {
            state.CurrentY = ApproachTarget(
                state.CurrentY,
                state.ImpulseTargetY,
                smoothing,
                state.ProgrammaticTarget ? double.PositiveInfinity : MaxWheelStep(state.ScrollViewer.ViewportHeight, state.ImpulseTargetY - state.CurrentY, deltaSeconds),
                out state.ImpulseActiveY);
        }

        if (state.ImpulseActiveX)
        {
            state.CurrentX = ApproachTarget(
                state.CurrentX,
                state.ImpulseTargetX,
                smoothing,
                MaxWheelStep(state.ScrollViewer.ViewportWidth, state.ImpulseTargetX - state.CurrentX, deltaSeconds),
                out state.ImpulseActiveX);
        }

        ApplyOffset(state, state.CurrentX, state.CurrentY, forceCommit: false);

        if (compositorActive)
        {
            // Sourced from state.CurrentY's own frame-to-frame delta - the value that DRIVES
            // SmoothScrollCompositor's visual position, not something re-derived from reading
            // the ScrollViewer/compositor back afterward. That avoids a real hazard on the
            // read-back side: SmoothScrollCompositor.Commit sets its CommittedY immediately but
            // ScrollViewer.VerticalOffset only catches up on the next Render-priority layout
            // pass, so sampling position from outside briefly dips backward then jumps forward
            // across a commit. This signal has no such artifact.
            var velocityPxPerMs = deltaSeconds > 0 ? (state.CurrentY - previousY) / (deltaSeconds * 1000.0) : 0;
            DashboardScrollViewportHelper.NotifyVisualScrollOffsetChanged(state.ScrollViewer, velocityPxPerMs);
        }

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.LogPosY(state.CurrentY);
    }

    private static void CompleteImpulse(State state)
    {
        if (state.ImpulseActiveY)
            state.CurrentY = state.ImpulseTargetY;
        if (state.ImpulseActiveX)
            state.CurrentX = state.ImpulseTargetX;

        state.ImpulseActiveY = false;
        state.ImpulseActiveX = false;
        ApplyOffset(state, state.CurrentX, state.CurrentY, forceCommit: true);
        // Do not SyncFromViewer here — ScrollToVerticalOffset may not have updated
        // VerticalOffset yet while drift translate is cleared, which would pass a stale
        // offset into EndAnimatedScroll and scroll content back by leftover drift.
    }

    private static double MaxWheelStep(double viewport, double remaining, double deltaSeconds)
    {
        if (viewport <= 1)
            return double.PositiveInfinity;

        var fraction = Math.Min(Math.Abs(remaining) / viewport, 1);
        var viewportsPerSecond = WheelCoastViewportsPerSecond + fraction * WheelAccelViewportsPerSecond;
        return Math.Max(1, viewport * viewportsPerSecond * deltaSeconds);
    }

    private static double ApproachTarget(double current, double target, double smoothing, double maxStep, out bool stillActive)
    {
        var remaining = target - current;
        if (Math.Abs(remaining) <= DoneEpsilonPixels)
        {
            stillActive = false;
            return target;
        }

        stillActive = true;
        var step = remaining * smoothing;
        if (Math.Abs(step) > maxStep)
            step = Math.CopySign(maxStep, remaining);

        return current + step;
    }

    private static void ApplyOffset(State state, double x, double y, bool forceCommit = true)
    {
        var clampedX = state.ScrollViewer.ScrollableWidth > 0
            ? Clamp(x, 0, state.ScrollViewer.ScrollableWidth)
            : 0;
        var clampedY = state.ScrollViewer.ScrollableHeight > 0
            ? Clamp(y, 0, state.ScrollViewer.ScrollableHeight)
            : 0;

        state.CurrentX = clampedX;
        state.CurrentY = clampedY;
        state.ApplyingOffset = true;

        if (SmoothScrollCompositor.TryApply(
                state.ScrollViewer,
                clampedX,
                clampedY,
                forceCommit,
                out _,
                out _))
        {
            state.ApplyingOffset = false;
            return;
        }

        if (state.ScrollViewer.ScrollableWidth > 0)
            state.ScrollViewer.ScrollToHorizontalOffset(clampedX);
        if (state.ScrollViewer.ScrollableHeight > 0)
            state.ScrollViewer.ScrollToVerticalOffset(clampedY);

        state.ApplyingOffset = false;
    }

    private static void SyncFromViewer(State state)
    {
        state.CurrentX = state.ScrollViewer.HorizontalOffset;
        state.CurrentY = state.ScrollViewer.VerticalOffset;
    }

    private static bool CanScroll(ScrollViewer scrollViewer)
        => scrollViewer.ScrollableHeight > 0
           || scrollViewer.ScrollableWidth > 0
           || scrollViewer.ExtentHeight > scrollViewer.ViewportHeight + 1
           || scrollViewer.ExtentWidth > scrollViewer.ViewportWidth + 1;

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    private static void BeginScrollAnimationSession(State state)
    {
        if (state.ScrollAnimationSessionActive)
            return;

        DashboardScrollViewportHelper.BeginAnimatedScroll(state.ScrollViewer);
        state.ScrollAnimationSessionActive = true;
    }

    private static void EndScrollAnimationSession(State state)
    {
        if (!state.ScrollAnimationSessionActive)
            return;

        state.ScrollAnimationSessionActive = false;

        DashboardScrollViewportHelper.EndAnimatedScroll(
            state.ScrollViewer,
            state.CurrentX,
            state.CurrentY);
    }

    private static State GetOrCreateState(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is State state)
            return state;

        state = new State { ScrollViewer = scrollViewer };
        scrollViewer.SetValue(StateProperty, state);
        return state;
    }
}
