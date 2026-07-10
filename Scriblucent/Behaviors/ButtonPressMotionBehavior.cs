using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Subtle press motion for chrome buttons (section toggles, calendar days, dialog actions).
/// Uses a downward nudge instead of scale so label text stays sharp while pressed.
/// </summary>
public static class ButtonPressMotionBehavior
{
    private sealed class State
    {
        public bool IsAttached;
        public bool IsPressed;
        public MouseButtonEventHandler? MouseDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(ButtonPressMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ButtonPressMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty MotionHostNameProperty = DependencyProperty.RegisterAttached(
        "MotionHostName",
        typeof(string),
        typeof(ButtonPressMotionBehavior),
        new PropertyMetadata(string.Empty));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetMotionHostName(DependencyObject element, string value)
        => element.SetValue(MotionHostNameProperty, value);

    public static string GetMotionHostName(DependencyObject element)
        => (string)element.GetValue(MotionHostNameProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.NewValue is true)
            Attach(element);
        else
            Detach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        if (state.IsAttached)
            return;

        state.MouseDownHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            state.IsPressed = true;
            PlayPress(element, isPressed: true);
        };
        state.MouseUpHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            state.IsPressed = false;
            PlayPress(element, isPressed: false);
        };

        element.Unloaded += (_, _) => Detach(element);
        element.PreviewMouseLeftButtonDown += state.MouseDownHandler;
        element.PreviewMouseLeftButtonUp += state.MouseUpHandler;
        state.IsAttached = true;
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.MouseDownHandler != null)
            element.PreviewMouseLeftButtonDown -= state.MouseDownHandler;
        if (state.MouseUpHandler != null)
            element.PreviewMouseLeftButtonUp -= state.MouseUpHandler;

        UiMotion.Stop(element);
        var host = ResolveMotionHost(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
        state.IsAttached = false;
        state.IsPressed = false;
    }

    private static void PlayPress(FrameworkElement element, bool isPressed)
    {
        if (UiMotion.PrefersReducedMotion)
            return;

        var host = ResolveMotionHost(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        var targetNudge = isPressed ? UiMotion.PressNudgeY : 0.0;
        var nudgeFrom = translate.Y;

        UiMotion.Run(
            element,
            new[]
            {
                UiMotion.Track(UiMotion.QuickMs, UiMotion.OutCubic, nudgeFrom, targetNudge, value => translate.Y = value)
            });
    }

    private static FrameworkElement ResolveMotionHost(FrameworkElement element)
    {
        if (element is not Control control)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            return element;
        }

        control.ApplyTemplate();
        var hostName = GetMotionHostName(element);
        if (!string.IsNullOrWhiteSpace(hostName)
            && control.Template.FindName(hostName, control) is FrameworkElement namedHost)
        {
            namedHost.RenderTransformOrigin = new Point(0.5, 0.5);
            return namedHost;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return element;
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }
}
