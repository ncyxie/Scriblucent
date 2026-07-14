using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Subtle press motion for chrome buttons (section toggles, calendar days, dialog actions)
/// and pressable surfaces such as calendar event cards.
/// Uses a downward nudge instead of scale so label text stays sharp while pressed.
/// <para>
/// Does not capture the mouse — capturing during PreviewMouseLeftButtonDown breaks WPF
/// Button.Click routing (sidebar / top-bar would stop responding).
/// </para>
/// Optional <see cref="PressedBackgroundProperty"/> swaps fill for the press on non-Button
/// surfaces (e.g. Border event cards), clearing the local value on release so style hover resumes.
/// </summary>
public static class ButtonPressMotionBehavior
{
    private sealed class State
    {
        public bool IsAttached;
        public bool IsPressed;
        public MouseButtonEventHandler? MouseDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
        public MouseEventHandler? MouseLeaveHandler;
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

    /// <summary>
    /// Optional fill applied while the pointer is pressed (Border / non-Button surfaces).
    /// Cleared on release so style triggers can drive the brush again.
    /// </summary>
    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground",
        typeof(Brush),
        typeof(ButtonPressMotionBehavior),
        new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetMotionHostName(DependencyObject element, string value)
        => element.SetValue(MotionHostNameProperty, value);

    public static string GetMotionHostName(DependencyObject element)
        => (string)element.GetValue(MotionHostNameProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value)
        => element.SetValue(PressedBackgroundProperty, value);

    public static Brush? GetPressedBackground(DependencyObject element)
        => element.GetValue(PressedBackgroundProperty) as Brush;

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
            if (e.ChangedButton != MouseButton.Left || !element.IsEnabled)
                return;

            state.IsPressed = true;
            PlayPress(element, isPressed: true);
        };
        state.MouseUpHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !state.IsPressed)
                return;

            state.IsPressed = false;
            PlayPress(element, isPressed: false);
        };
        state.MouseLeaveHandler = (_, _) =>
        {
            if (!state.IsPressed)
                return;

            // Pointer left while held — release visual without capturing the mouse.
            state.IsPressed = false;
            PlayPress(element, isPressed: false);
        };

        element.Unloaded += OnUnloaded;
        element.PreviewMouseLeftButtonDown += state.MouseDownHandler;
        element.PreviewMouseLeftButtonUp += state.MouseUpHandler;
        element.MouseLeave += state.MouseLeaveHandler;
        state.IsAttached = true;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            Detach(element);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        element.Unloaded -= OnUnloaded;
        if (state.MouseDownHandler != null)
            element.PreviewMouseLeftButtonDown -= state.MouseDownHandler;
        if (state.MouseUpHandler != null)
            element.PreviewMouseLeftButtonUp -= state.MouseUpHandler;
        if (state.MouseLeaveHandler != null)
            element.MouseLeave -= state.MouseLeaveHandler;

        UiMotion.Stop(element);
        var host = ResolveMotionHost(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
        ClearPressedBackground(element);

        state.IsAttached = false;
        state.IsPressed = false;
    }

    private static void PlayPress(FrameworkElement element, bool isPressed)
    {
        ApplyPressedBackground(element, isPressed);

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

    private static void ApplyPressedBackground(FrameworkElement element, bool isPressed)
    {
        var pressed = GetPressedBackground(element);
        if (pressed is null)
            return;

        // Never overwrite Control.Background on real Buttons — their templates own the chrome.
        if (element is ButtonBase)
            return;

        if (element is Border)
        {
            if (isPressed)
                element.SetValue(Border.BackgroundProperty, pressed);
            else
                ClearPressedBackground(element);
        }
    }

    private static void ClearPressedBackground(FrameworkElement element)
    {
        if (GetPressedBackground(element) is null)
            return;

        if (element is Border)
            element.ClearValue(Border.BackgroundProperty);
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
            && control.Template != null
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
