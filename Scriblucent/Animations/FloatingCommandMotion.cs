using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>Floating command surfaces settle from their anchored edge; their content is never height-wiped.</summary>
internal static class FloatingCommandMotion
{
    private sealed class State
    {
        internal bool Initialized, Visible;
        internal bool OriginalHitTest;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    internal static void SetVisible(FrameworkElement element, bool visible, bool animate = true, Action? onComplete = null, bool fromBottom = false)
    {
        var state = States.GetValue(element, _ =>
        {
            var value = new State { OriginalHitTest = element.IsHitTestVisible };
            element.Unloaded += (_, _) =>
            {
                SurfaceMotion.Stop(element);
                Settle(element, value.Visible);
                value.Initialized = false;
            };
            return value;
        });
        if (state.Initialized && state.Visible == visible && animate) return;
        state.Initialized = true; state.Visible = visible;
        bool fresh = element.Visibility != Visibility.Visible;
        SurfaceMotion.Stop(element);
        element.RenderTransformOrigin = new Point(fromBottom ? .5 : 1, fromBottom ? 1 : 0);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        if (visible)
        {
            // The entire floating slot is present before native paint-only motion starts.
            if (fresh) UiMotion.AnimateLayoutReveal(element, true, animate: false);
            if (fresh)
            {
                element.Opacity = 0;
                translate.Y = MotionPolicy.Spatial ? fromBottom ? 6 : -6 : 0;
                scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .985 : 1;
            }
        }
        element.IsHitTestVisible = visible && state.OriginalHitTest;
        void Complete()
        {
            Settle(element, visible);
            onComplete?.Invoke();
        }
        if (!animate) { Complete(); return; }
        SurfaceMotion.AnimateTracks(element, MotionChannel.Presence,
            [new(element, UIElement.OpacityProperty, visible ? 1 : 0,
                Profile: new(UiMotion.InOutSine, visible ? 220 : 180)),
             new(translate, TranslateTransform.YProperty, visible || !MotionPolicy.Spatial ? 0 : fromBottom ? 4 : -4, true),
             new(scale, ScaleTransform.ScaleXProperty, visible || !MotionPolicy.Spatial ? 1 : .985, true),
             new(scale, ScaleTransform.ScaleYProperty, visible || !MotionPolicy.Spatial ? 1 : .985, true)],
            visible ? 280 : 190, UiMotion.InOutSine, Complete);
    }

    private static void Settle(FrameworkElement element, bool visible)
    {
        if (!visible) UiMotion.AnimateLayoutReveal(element, false, animate: false);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        scale.ScaleX = scale.ScaleY = 1; translate.Y = 0;
        element.Opacity = visible ? 1 : 0;
    }
}
