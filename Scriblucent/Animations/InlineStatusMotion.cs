using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Animations;

/// <summary>Small feedback settles beside the title without restarting as the user types.</summary>
internal static class InlineStatusMotion
{
    private sealed class State
    {
        internal bool Initialized, Visible, Running;
        internal int Revision;
        internal string? Message;
        internal Action? Completion;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    internal static void SetVisible(FrameworkElement element, bool visible, Action? onComplete, bool animate, string? message)
    {
        var state = States.GetValue(element, _ =>
        {
            var value = new State();
            element.Unloaded += (_, _) =>
            {
                SurfaceMotion.Stop(element, MotionChannel.Presence);
                UiMotion.ChannelTranslate(element, MotionChannel.Presence).Y = 0;
                value.Initialized = false;
                value.Running = false;
                value.Revision++;
                value.Completion = null;
                if (!value.Visible) element.Visibility = Visibility.Collapsed;
            };
            return value;
        });
        bool same = state.Initialized && state.Visible == visible && (!visible || state.Message == message);
        if (same && animate)
        {
            if (state.Running) state.Completion += onComplete;
            else onComplete?.Invoke();
            return;
        }
        bool newMessage = state.Initialized && state.Visible && visible && state.Message != message;
        state.Initialized = true; state.Visible = visible; state.Message = message;
        var revision = ++state.Revision;
        state.Completion = onComplete;
        SurfaceMotion.Stop(element, MotionChannel.Presence);
        var translate = UiMotion.ChannelTranslate(element, MotionChannel.Presence);
        if (visible)
        {
            if (element.Visibility != Visibility.Visible || newMessage)
            {
                element.Opacity = newMessage ? Math.Min(.2, element.Opacity) : 0;
                translate.Y = MotionPolicy.Spatial ? 2 : 0;
            }
            element.Visibility = Visibility.Visible;
        }
        void Complete()
        {
            state.Running = false;
            translate.Y = 0;
            if (!visible) element.Visibility = Visibility.Collapsed;
            var completion = state.Completion; state.Completion = null;
            completion?.Invoke();
        }
        if (!animate)
        {
            element.Opacity = visible ? 1 : 0;
            Complete(); return;
        }
        state.Running = true;
        void Start()
        {
            if (revision != state.Revision) return;
            SurfaceMotion.AnimateTracks(element, MotionChannel.Presence,
                [new(element, UIElement.OpacityProperty, visible ? 1 : 0),
                 new(translate, TranslateTransform.YProperty, 0, true)],
                visible ? 240 : 180, UiMotion.InOutSine, Complete);
        }
        // A previously collapsed label can receive Loaded only after this layout pass.
        // Starting its clock before that event would commit the end pose immediately.
        if (visible && !element.IsLoaded && MotionPolicy.Quality != MotionQuality.Off)
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Start);
        else Start();
    }
}
