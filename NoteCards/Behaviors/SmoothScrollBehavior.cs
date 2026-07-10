using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NoteCards.Animations;
using NoteCards.Controls;

namespace NoteCards.Behaviors;

/// <summary>
/// Edge-style smooth scrolling for mouse wheel and scrollbar page/track jumps on <see cref="ScrollViewer"/> instances.
/// Middle-click autoscroll is handled separately by <see cref="MiddleClickScrollBehavior"/>.
/// </summary>
public static class SmoothScrollBehavior
{
    private const double WheelViewportRatio = 0.20;

    // Exponential "approach the target" smoothing instead of a fixed-duration eased tween.
    // Each wheel tick used to restart a 200ms cubic ease from elapsed=0, which snapped the
    // velocity back up to the curve's start value every time a tick landed — with real wheel
    // input (uneven tick spacing) that produced a visible velocity discontinuity on ~85% of
    // ticks, independent of render performance. Exponential smoothing has no "restart": a
    // moved target just changes where we're heading, current velocity carries over continuously,
    // and it composes exactly across variable frame deltas (no substep loop needed).
    private const double SmoothingRatePerMs = 0.014979; // ln(20)/200: ~95% settled after 200ms
    private const double DoneEpsilonPixels = 0.3;
    private const double MinFrameSeconds = 1.0 / 240.0;
    private const double MaxFrameSeconds = 1.0 / 45.0;
    private const double DirectScrollStepThreshold = 24;

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
        public MouseWheelEventHandler? MouseWheelHandler;
        public ScrollChangedEventHandler? ScrollChangedHandler;
        public SizeChangedEventHandler? SizeChangedHandler;
        public RoutedEventHandler? LoadedHandler;
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

        scrollViewer.PreviewMouseWheel += state.MouseWheelHandler;
        scrollViewer.ScrollChanged += state.ScrollChangedHandler;
        scrollViewer.SizeChanged += state.SizeChangedHandler;
        scrollViewer.Loaded += state.LoadedHandler;

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

        DetachScrollbarHooks(state);
        state.IsAttached = false;
    }

    private static void OnLoaded(State state)
    {
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

        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
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
        if (e.Handled || state.ExternalDrive || !CanScroll(state.ScrollViewer))
            return;

        e.Handled = true;
        var (deltaX, deltaY) = ComputeWheelDelta(state.ScrollViewer, e);
        if (NoteCards.Services.ScrollPerfDiag.Enabled)
        {
            NoteCards.Services.ScrollPerfDiag.Log(
                $"wheel\tdy={deltaY:F0}\tscrollable={state.ScrollViewer.ScrollableHeight:F0}\tviewport={state.ScrollViewer.ViewportHeight:F0}x{state.ScrollViewer.ViewportWidth:F0}");
        }

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
        if (vertical)
        {
            var baseTarget = state.ImpulseActiveY ? state.ImpulseTargetY : state.CurrentY;
            state.ImpulseTargetY = Clamp(baseTarget + delta, 0, state.ScrollViewer.ScrollableHeight);
            state.ImpulseActiveY = true;
            return;
        }

        var baseTargetX = state.ImpulseActiveX ? state.ImpulseTargetX : state.CurrentX;
        state.ImpulseTargetX = Clamp(baseTargetX + delta, 0, state.ScrollViewer.ScrollableWidth);
        state.ImpulseActiveX = true;
    }

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
            CompositionTarget.Rendering += _renderingHandler;
        }
    }

    private static void StopAnimating(State state)
    {
        ActiveStates.Remove(state);
        state.ImpulseActiveX = false;
        state.ImpulseActiveY = false;
        state.LastTickRenderingTime = TimeSpan.MinValue;
        EndScrollAnimationSession(state);

        if (ActiveStates.Count == 0 && _renderingHandler != null)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            _renderingHandler = null;
            _lastGlobalRenderingTime = TimeSpan.MinValue;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs renderingArgs
            && renderingArgs.RenderingTime == _lastGlobalRenderingTime)
            return;

        if (e is RenderingEventArgs re)
        {
            if (NoteCards.Services.ScrollPerfDiag.Enabled && _lastGlobalRenderingTime != TimeSpan.MinValue)
            {
                var frameMs = (re.RenderingTime - _lastGlobalRenderingTime).TotalMilliseconds;
                NoteCards.Services.ScrollPerfDiag.Log(
                    $"frame\t{frameMs:F2}\tgc={GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}\tcache={NoteCards.Controls.DashboardScrollSurfaceCache.AppliedCount},{NoteCards.Controls.DashboardScrollSurfaceCache.PendingCount}");
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
            CompositionTarget.Rendering -= _renderingHandler;
            _renderingHandler = null;
            _lastGlobalRenderingTime = TimeSpan.MinValue;
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

        if (state.ImpulseActiveY)
            state.CurrentY = ApproachTarget(state.CurrentY, state.ImpulseTargetY, smoothing, out state.ImpulseActiveY);

        if (state.ImpulseActiveX)
            state.CurrentX = ApproachTarget(state.CurrentX, state.ImpulseTargetX, smoothing, out state.ImpulseActiveX);

        ApplyOffset(state, state.CurrentX, state.CurrentY, forceCommit: false);

        if (compositorActive)
            DashboardScrollViewportHelper.NotifyVisualScrollOffsetChanged(state.ScrollViewer);

        if (NoteCards.Services.ScrollPerfDiag.Enabled)
            NoteCards.Services.ScrollPerfDiag.Log($"posY\t{state.CurrentY:F3}");
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

    private static double ApproachTarget(double current, double target, double smoothing, out bool stillActive)
    {
        var remaining = target - current;
        if (Math.Abs(remaining) <= DoneEpsilonPixels)
        {
            stillActive = false;
            return target;
        }

        stillActive = true;
        return current + remaining * smoothing;
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