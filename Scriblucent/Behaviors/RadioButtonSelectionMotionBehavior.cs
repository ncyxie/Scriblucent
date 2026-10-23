using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Fades the template radio dot in on user selection. Uncheck is instant to avoid binding/group churn glitches.
/// </summary>
public static class RadioButtonSelectionMotionBehavior
{
    private const int EnterMs = 120;
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, int>> KeyboardIntents = new();
    private static int _keyboardRevision;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsReady;
        public bool PendingUserSelection;
        public RoutedEventHandler? LoadedHandler;
        public MouseButtonEventHandler? PreviewMouseDownHandler;
        public RoutedEventHandler? CheckedHandler;
        public RoutedEventHandler? UncheckedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public KeyEventHandler? KeyDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
        public MouseEventHandler? MouseLeaveHandler;
        public Ellipse? Glyph;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(RadioButtonSelectionMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(RadioButtonSelectionMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadioButton radioButton)
            return;

        if (e.NewValue is true)
            Attach(radioButton);
        else
            Detach(radioButton);
    }

    private static void Attach(RadioButton radioButton)
    {
        var state = GetOrCreateState(radioButton);
        if (state.IsAttached)
            return;

        state.PreviewMouseDownHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !state.IsReady || !radioButton.IsEnabled)
                return;

            state.PendingUserSelection = radioButton.IsChecked != true;
        };
        state.LoadedHandler = (_, _) =>
        {
            state.IsReady = true;
            QueueRestSync(radioButton);
        };
        state.UnloadedHandler = (_, _) =>
        {
            state.IsReady = false; state.PendingUserSelection = false;
            if (state.Glyph is { } glyph) { SurfaceMotion.Stop(glyph); UiMotion.Stop(glyph); }
            state.Glyph = null;
        };
        state.KeyDownHandler = (_, e) =>
        {
            if (!state.IsReady || !radioButton.IsEnabled || e.IsRepeat
                || e.Key is not (Key.Space or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down)) return;
            var (scope, group) = KeyboardGroup(radioButton);
            var intents = KeyboardIntents.GetValue(scope, _ => new());
            var revision = ++_keyboardRevision; intents[group] = revision;
            radioButton.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            { if (intents.TryGetValue(group, out var current) && current == revision) intents.Remove(group); });
        };
        state.MouseUpHandler = (_, _) => radioButton.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () => state.PendingUserSelection = false);
        state.MouseLeaveHandler = (_, _) => state.PendingUserSelection = false;
        state.CheckedHandler = (_, _) =>
        {
            if (!state.IsReady)
                return;

            var (scope, group) = KeyboardGroup(radioButton);
            if (state.PendingUserSelection || KeyboardIntents.TryGetValue(scope, out var intents) && intents.ContainsKey(group))
            {
                state.PendingUserSelection = false;
                AnimateChecked(radioButton);
                return;
            }

            ShowChecked(radioButton);
        };
        state.UncheckedHandler = (_, _) =>
        {
            if (!state.IsReady)
                return;

            state.PendingUserSelection = false;
            HideUnchecked(radioButton);
        };

        radioButton.PreviewMouseLeftButtonDown += state.PreviewMouseDownHandler;
        radioButton.Loaded += state.LoadedHandler;
        radioButton.Checked += state.CheckedHandler;
        radioButton.Unchecked += state.UncheckedHandler;
        radioButton.Unloaded += state.UnloadedHandler;
        radioButton.PreviewKeyDown += state.KeyDownHandler;
        radioButton.PreviewMouseLeftButtonUp += state.MouseUpHandler;
        radioButton.MouseLeave += state.MouseLeaveHandler;
        state.IsAttached = true;

        if (radioButton.IsLoaded)
        {
            state.IsReady = true;
            QueueRestSync(radioButton);
        }
    }

    private static void Detach(RadioButton radioButton)
    {
        if (radioButton.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.PreviewMouseDownHandler != null)
            radioButton.PreviewMouseLeftButtonDown -= state.PreviewMouseDownHandler;
        if (state.LoadedHandler != null)
            radioButton.Loaded -= state.LoadedHandler;
        if (state.CheckedHandler != null)
            radioButton.Checked -= state.CheckedHandler;
        if (state.UncheckedHandler != null)
            radioButton.Unchecked -= state.UncheckedHandler;
        radioButton.Unloaded -= state.UnloadedHandler;
        radioButton.PreviewKeyDown -= state.KeyDownHandler;
        radioButton.PreviewMouseLeftButtonUp -= state.MouseUpHandler;
        radioButton.MouseLeave -= state.MouseLeaveHandler;

        if (state.Glyph is { } glyph) { SurfaceMotion.Stop(glyph); UiMotion.Stop(glyph); }
        state.Glyph = null;

        state.IsAttached = false;
        state.IsReady = false;
        state.PendingUserSelection = false;
    }

    private static void QueueRestSync(RadioButton radioButton)
    {
        radioButton.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => RestSync(radioButton)));
    }

    private static void RestSync(RadioButton radioButton)
    {
        if (radioButton.GetValue(StateProperty) is not State { IsReady: true })
            return;

        if (TryResolveGlyph(radioButton) is not { } glyph)
            return;

        UiMotion.Stop(glyph);
        if (radioButton.IsChecked == true)
            ShowChecked(glyph);
        else
            HideUnchecked(glyph);
    }

    private static void AnimateChecked(RadioButton radioButton)
    {
        if (TryResolveGlyph(radioButton) is not { } glyph)
            return;

        UiMotion.Stop(glyph);

        if (UiMotion.IsMotionDisabled)
        {
            ShowChecked(glyph);
            return;
        }

        glyph.Opacity = 0;
        SurfaceMotion.Animate(glyph, 1, EnterMs, UiMotion.OutSine, false);
    }

    private static void ShowChecked(RadioButton radioButton)
    {
        if (TryResolveGlyph(radioButton) is not { } glyph)
            return;

        UiMotion.Stop(glyph);
        ShowChecked(glyph);
    }

    private static void ShowChecked(Ellipse glyph)
    {
        glyph.Opacity = 1;
    }

    private static void HideUnchecked(RadioButton radioButton)
    {
        if (TryResolveGlyph(radioButton) is not { } glyph)
            return;

        UiMotion.Stop(glyph);
        HideUnchecked(glyph);
    }

    private static void HideUnchecked(Ellipse glyph)
    {
        glyph.Opacity = 0;
    }

    private static Ellipse? TryResolveGlyph(RadioButton radioButton)
    {
        if (!radioButton.IsLoaded)
            return null;

        radioButton.ApplyTemplate();
        var glyph = radioButton.Template?.FindName("RadioGlyph", radioButton) as Ellipse;
        GetOrCreateState(radioButton).Glyph = glyph;
        return glyph;
    }

    private static (DependencyObject Scope, string Group) KeyboardGroup(RadioButton radio)
        => (string.IsNullOrEmpty(radio.GroupName) ? radio.Parent ?? radio :
            (DependencyObject?)Window.GetWindow(radio) ?? PresentationSource.FromVisual(radio)?.RootVisual ?? radio,
            radio.GroupName ?? "");

    private static State GetOrCreateState(RadioButton radioButton)
    {
        if (radioButton.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        radioButton.SetValue(StateProperty, state);
        return state;
    }
}
