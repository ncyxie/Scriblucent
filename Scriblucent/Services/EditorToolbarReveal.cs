using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Services;

/// <summary>Reveals contextual tools with a small local motion.</summary>
internal static class EditorToolbarReveal
{
    private sealed class State
    {
        internal bool Initialized;
        internal bool Visible;
        internal double FullWidth;
        internal readonly TranslateTransform Translate = new();
    }

    private static readonly ConditionalWeakTable<Border, State> States = new();
    private const double SlideDistance = 6;

    internal static void SetVisible(Border host, bool visible, bool animate = true)
    {
        if (host.Child is not FrameworkElement content) return;
        var state = States.GetValue(host, _ =>
        {
            var value = new State();
            // Only the dedicated slot content moves; button press motion stays intact.
            content.RenderTransform = value.Translate;
            host.Unloaded += (_, _) => { UiMotion.Stop(host); Finish(host, value); };
            return value;
        });
        if (state.Initialized && state.Visible == visible && animate) return;
        bool firstUpdate = !state.Initialized;
        state.Initialized = true;
        state.Visible = visible;
        state.FullWidth = content.Width + content.Margin.Left + content.Margin.Right;
        if (!double.IsFinite(state.FullWidth)) throw new InvalidOperationException("Toolbar reveal content requires a fixed width.");
        UiMotion.Stop(host);
        host.IsEnabled = visible;
        host.IsHitTestVisible = visible;
        if (firstUpdate || !animate || (host.Parent as UIElement)?.IsVisible != true || UiMotion.PrefersReducedMotion)
        {
            Finish(host, state);
            return;
        }
        bool wasCollapsed = host.Visibility == Visibility.Collapsed;
        double opacityFrom = wasCollapsed ? 0 : content.Opacity;
        double offsetFrom = wasCollapsed ? SlideDistance : state.Translate.X;
        // Settle the layout once. Animating the whole slot's width makes adjacent
        // actions sweep sideways and turns a selection change into a presentation.
        host.Width = state.FullWidth;
        host.Visibility = Visibility.Visible;
        host.ClipToBounds = true;
        UiMotion.Run(host,
            [
                UiMotion.Track(visible ? 160 : 100, visible ? UiMotion.OutSine : UiMotion.InSine, opacityFrom, visible ? 1 : 0,
                    opacity => content.Opacity = opacity),
                UiMotion.Track(visible ? 160 : 100, visible ? UiMotion.InOutSine : UiMotion.InQuad, offsetFrom, visible ? 0 : SlideDistance,
                    offset => state.Translate.X = offset)
            ], () => Finish(host, state));
    }

    private static void Finish(Border host, State state)
    {
        host.Width = state.Visible ? state.FullWidth : 0;
        host.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        state.Translate.X = state.Visible ? 0 : SlideDistance;
        if (host.Child is UIElement content) content.Opacity = state.Visible ? 1 : 0;
        host.IsEnabled = host.IsHitTestVisible = state.Visible;
    }
}
