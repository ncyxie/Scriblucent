using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using NoteCards.Animations;

namespace NoteCards.Behaviors;

/// <summary>
/// Fades the template radio dot in on user selection. Uncheck is instant to avoid binding/group churn glitches.
/// </summary>
public static class RadioButtonSelectionMotionBehavior
{
    private const int EnterMs = 200;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsReady;
        public bool PendingUserSelection;
        public RoutedEventHandler? LoadedHandler;
        public MouseButtonEventHandler? PreviewMouseDownHandler;
        public RoutedEventHandler? CheckedHandler;
        public RoutedEventHandler? UncheckedHandler;
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
            if (e.ChangedButton != MouseButton.Left)
                return;

            state.PendingUserSelection = radioButton.IsChecked != true;
        };
        state.LoadedHandler = (_, _) =>
        {
            state.IsReady = true;
            QueueRestSync(radioButton);
        };
        state.CheckedHandler = (_, _) =>
        {
            if (!state.IsReady)
                return;

            if (state.PendingUserSelection)
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
        radioButton.Unloaded += (_, _) => Detach(radioButton);
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

        if (TryResolveGlyph(radioButton) is { } glyph)
            UiMotion.Stop(glyph);

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

        if (UiMotion.PrefersReducedMotion)
        {
            ShowChecked(glyph);
            return;
        }

        glyph.Opacity = 0;
        UiMotion.Run(
            glyph,
            new[]
            {
                UiMotion.Track(EnterMs, UiMotion.OutCubic, 0, 1, value => glyph.Opacity = value)
            });
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
        return radioButton.Template?.FindName("RadioGlyph", radioButton) as Ellipse;
    }

    private static State GetOrCreateState(RadioButton radioButton)
    {
        if (radioButton.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        radioButton.SetValue(StateProperty, state);
        return state;
    }
}