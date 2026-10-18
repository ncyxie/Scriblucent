using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

namespace Scriblucent.Animations;

internal static class LayoutRevealMotion
{
    private sealed class State
    {
        internal bool Initialized, Visible, Running, RefreshQueued;
        internal bool Soften;
        internal int? Duration;
        internal bool OriginalClip, OriginalHitTest, OriginalEnabled;
        internal double Target;
        internal Action? Completion;
        internal Geometry? OriginalGeometry;
    }

    private static readonly ConditionalWeakTable<LayoutRevealHost, State> States = new();

    internal static void SetVisible(FrameworkElement content, bool show, Orientation axis,
        bool animate, Action? onComplete, RowDefinition? row, bool soften = false, int? durationMs = null)
    {
        if (content.Parent is not LayoutRevealHost host)
            throw new InvalidOperationException($"{content.Name} needs a LayoutRevealHost for layout reveal motion.");
        var state = States.GetValue(host, _ =>
        {
            var value = new State
            {
                OriginalClip = host.ClipToBounds,
                OriginalGeometry = host.Clip,
                OriginalHitTest = host.ReadLocalValue(UIElement.IsHitTestVisibleProperty) is not false,
                OriginalEnabled = host.ReadLocalValue(UIElement.IsEnabledProperty) is not false
            };
            host.Unloaded += (_, _) => { UiMotion.Stop(host); UiMotion.Stop(content); Finish(host, content, value); };
            host.NaturalExtentChanged += () => QueueRetarget(host, content, value);
            return value;
        });
        if (row is not null) row.SetCurrentValue(RowDefinition.HeightProperty, GridLength.Auto);
        host.Axis = axis;
        bool same = state.Initialized && state.Visible == show;
        state.Initialized = true;
        state.Visible = show;
        state.Soften = soften;
        state.Duration = durationMs;
        host.SetCurrentValue(UIElement.IsEnabledProperty, show && state.OriginalEnabled);
        if (same && animate)
        {
            if (state.Running && onComplete is not null) state.Completion = onComplete;
            else if (!state.Running) onComplete?.Invoke();
            return;
        }
        UiMotion.Stop(host);
        UiMotion.Stop(content);
        state.Completion = onComplete;
        double from = host.Visibility == Visibility.Collapsed || content.Visibility == Visibility.Collapsed ? 0
            : CurrentExtent(host);
        if (!soften) content.SetCurrentValue(UIElement.OpacityProperty, 1.0);
        else if (show && from == 0) content.SetCurrentValue(UIElement.OpacityProperty, 0.0);
        if (!animate || !host.IsLoaded || (host.Parent as UIElement)?.IsVisible != true || UiMotion.PrefersReducedMotion)
        {
            Finish(host, content, state);
            return;
        }
        host.Extent = from;
        host.ClipToBounds = true;
        host.IsHitTestVisible = false;
        host.Visibility = content.Visibility = Visibility.Visible;
        MeasureNatural(host);
        Start(host, content, state, from);
    }

    private static void MeasureNatural(LayoutRevealHost host)
    {
        double width = (host.Parent as FrameworkElement)?.ActualWidth ?? host.ActualWidth;
        double height = (host.Parent as FrameworkElement)?.ActualHeight ?? host.ActualHeight;
        host.InvalidateMeasure();
        host.Measure(new Size(width > 0 ? width : double.PositiveInfinity,
            height > 0 ? height : double.PositiveInfinity));
    }

    private static double CurrentExtent(LayoutRevealHost host)
        => !double.IsNaN(host.Extent) ? host.Extent
            : host.Clip is RectangleGeometry clip ? (host.Axis == Orientation.Vertical ? clip.Rect.Height : clip.Rect.Width)
            : host.Axis == Orientation.Vertical ? host.ActualHeight : host.ActualWidth;

    private static void Start(LayoutRevealHost host, FrameworkElement content, State state, double from)
    {
        UiMotion.Stop(host);
        state.Target = state.Visible ? host.NaturalExtent : 0;
        if (Math.Abs(from - state.Target) < .5) { Finish(host, content, state); return; }
        state.Running = true;
        if (host.Axis == Orientation.Vertical && host.NaturalExtent > 320 && !host.AnimateLargeExtent)
        {
            StartLarge(host, content, state, from);
            return;
        }
        int duration = state.Duration ?? (state.Soften ? state.Visible ? 280 : 200 : state.Visible ? 220 : 140);
        var ease = state.Soften || state.Visible ? UiMotion.InOutSine : UiMotion.InQuad;
        if (state.Soften)
        {
            SurfaceMotion.AnimateTracks(host, MotionChannel.Layout,
                [new(host, LayoutRevealHost.ExtentProperty, state.Target, true),
                 new(content, UIElement.OpacityProperty, state.Visible ? 1 : 0,
                     Profile: new(UiMotion.InOutSine, state.Visible ? 220 : 180))],
                duration, ease, () => Finish(host, content, state));
            return;
        }
        var tracks = new List<RenderTransitionClock.Track>
        {
            UiMotion.Track(duration, ease, from, state.Target, extent => host.Extent = extent)
        };
        UiMotion.Run(host, tracks, () => Finish(host, content, state));
    }

    private static void StartLarge(LayoutRevealHost host, FrameworkElement content, State state, double from)
    {
        var root = host.Parent as FrameworkElement;
        var before = root is null ? null : LayoutFlipTransition.Capture(root);
        if (!state.Visible)
        {
            UiMotion.Fade(content, 0, 80, UiMotion.InQuad, () =>
            {
                Finish(host, content, state);
                content.Opacity = 1;
                if (root is not null && before is not null) LayoutFlipTransition.Play(root, before);
            });
            return;
        }
        host.Extent = double.NaN;
        host.UpdateLayout();
        var width = Math.Max(1, host.ActualWidth);
        var clip = new RectangleGeometry(new Rect(0, 0, width, Math.Max(0, from)));
        host.Clip = clip;
        if (root is not null && before is not null) LayoutFlipTransition.Play(root, before);
        UiMotion.Run(host, [UiMotion.Track(220, UiMotion.InOutSine, from, state.Target,
            extent => clip.Rect = new Rect(0, 0, width, Math.Max(0, extent)))], () => Finish(host, content, state));
    }

    private static void QueueRetarget(LayoutRevealHost host, FrameworkElement content, State state)
    {
        if (!state.Running || !state.Visible || state.RefreshQueued) return;
        state.RefreshQueued = true;
        host.Dispatcher.BeginInvoke(() =>
        {
            state.RefreshQueued = false;
            if (state.Running && state.Visible && Math.Abs(state.Target - host.NaturalExtent) > .5)
                Start(host, content, state, CurrentExtent(host));
        }, DispatcherPriority.Render);
    }

    private static void Finish(LayoutRevealHost host, FrameworkElement content, State state)
    {
        state.Running = false;
        host.Clip = state.OriginalGeometry;
        host.Visibility = content.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (state.Soften) content.SetCurrentValue(UIElement.OpacityProperty, 1.0);
        host.Extent = double.NaN;
        host.ClipToBounds = state.OriginalClip;
        host.IsHitTestVisible = state.OriginalHitTest;
        host.SetCurrentValue(UIElement.IsEnabledProperty, state.Visible && state.OriginalEnabled);
        var completion = state.Completion;
        state.Completion = null;
        completion?.Invoke();
    }
}
