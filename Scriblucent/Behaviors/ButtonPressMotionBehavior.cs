using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Subtle press motion for chrome buttons (section toggles, calendar days, dialog actions)
/// and pressable surfaces such as calendar event cards. Uses a downward nudge instead of scale
/// so label text stays sharp while pressed. Does not capture the mouse - capturing during
/// PreviewMouseLeftButtonDown breaks WPF Button.Click routing. Optional
/// <see cref="PressedBackgroundProperty"/> swaps fill for the press on non-Button surfaces.
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
        public KeyEventHandler? KeyDownHandler;
        public KeyEventHandler? KeyUpHandler;
        public KeyboardFocusChangedEventHandler? LostFocusHandler;
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

        state.KeyDownHandler = (_, e) =>
        {
            if (element is ButtonBase && e.Key is Key.Space or Key.Enter && !e.IsRepeat && element.IsEnabled)
            { state.IsPressed = true; PlayPress(element, true); }
        };
        state.KeyUpHandler = (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter && state.IsPressed)
            { state.IsPressed = false; PlayPress(element, false); }
        };
        state.LostFocusHandler = (_, _) =>
        {
            if (!state.IsPressed) return;
            state.IsPressed = false;
            PlayPress(element, false);
        };
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        element.PreviewKeyDown += state.KeyDownHandler;
        element.PreviewKeyUp += state.KeyUpHandler;
        element.LostKeyboardFocus += state.LostFocusHandler;

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

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !GetIsEnabled(element)) return;
        if (ResolveMotionHost(element) is Scriblucent.Controls.InteractionSurface || ChromeHoverMotionBehavior.GetIsEnabled(element))
            Detach(element);
        else Attach(element);
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

        element.PreviewKeyDown -= state.KeyDownHandler;
        element.PreviewKeyUp -= state.KeyUpHandler;
        element.LostKeyboardFocus -= state.LostFocusHandler;
        var host = ResolveMotionHost(element);
        UiMotion.Stop(UiMotion.Channel(host, MotionChannel.Interaction));
        var translate = UiMotion.ChannelTranslate(host, MotionChannel.Interaction);
        translate.X = 0;
        translate.Y = 0;
        ClearPressedBackground(element);

        state.IsAttached = false;
        state.IsPressed = false;
    }

    private static void PlayPress(FrameworkElement element, bool isPressed)
    {
        // Shared button faces and lifted chrome already own this interaction channel.
        if (ResolveMotionHost(element) is Scriblucent.Controls.InteractionSurface || ChromeHoverMotionBehavior.GetIsEnabled(element)) return;
        ApplyPressedBackground(element, isPressed);

        if (UiMotion.PrefersReducedMotion)
            return;

        var host = ResolveMotionHost(element);
        var translate = UiMotion.ChannelTranslate(host, MotionChannel.Interaction);

        var targetNudge = isPressed ? UiMotion.PressNudgeY : 0.0;
        var nudgeFrom = translate.Y;

        UiMotion.Run(
            host, MotionChannel.Interaction,
            new[]
            {
                UiMotion.Track(isPressed ? 80 : 120, UiMotion.OutCubic, nudgeFrom, targetNudge, value => translate.Y = value)
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
