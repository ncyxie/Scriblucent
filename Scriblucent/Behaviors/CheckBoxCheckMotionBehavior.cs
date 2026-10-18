using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Draws the template checkmark in/out via <see cref="UiMotion"/> so checkbox motion stays off WPF storyboards.
/// </summary>
public static class CheckBoxCheckMotionBehavior
{
    public const double PathLength = 14;
    private const double HiddenDashOffset = 16;
    private const int EnterMs = 160;
    private const int ExitMs = 100;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsReady;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public RoutedEventHandler? CheckedHandler;
        public RoutedEventHandler? UncheckedHandler;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(CheckBoxCheckMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(CheckBoxCheckMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Imperatively syncs the template checkmark with <see cref="CheckBox.IsChecked"/>.</summary>
    public static void SyncCheckBoxGlyph(CheckBox checkBox, bool animate = false)
    {
        if (!GetIsEnabled(checkBox))
            return;

        if (!checkBox.IsLoaded)
        {
            checkBox.Dispatcher.BeginInvoke(
                () => SyncGlyph(checkBox, animate),
                DispatcherPriority.Loaded);
            return;
        }

        SyncGlyph(checkBox, animate);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CheckBox checkBox)
            return;

        if (e.NewValue is true)
            Attach(checkBox);
        else
            Detach(checkBox);
    }

    // Loaded/Unloaded stay wired for the checkbox's whole lifetime (as long as IsEnabled=True);
    // Checked/Unchecked are re-subscribed on every Loaded and torn down on every Unloaded. A
    // checkbox living inside a Popup (e.g. a filters flyout) gets unloaded every time the popup
    // closes and reloaded every time it reopens - subscribing Checked/Unchecked only once here
    // (the original behavior) left them permanently unsubscribed after the first close, so the
    // checkmark stopped animating for the rest of the app's lifetime even though clicks still
    // worked fine.
    private static void Attach(CheckBox checkBox)
    {
        var state = GetOrCreateState(checkBox);
        if (state.IsAttached)
            return;

        state.LoadedHandler = (_, _) => OnLoaded(checkBox, state);
        state.UnloadedHandler = (_, _) => OnUnloaded(checkBox, state);

        checkBox.Loaded += state.LoadedHandler;
        checkBox.Unloaded += state.UnloadedHandler;
        state.IsAttached = true;

        if (checkBox.IsLoaded)
            OnLoaded(checkBox, state);
    }

    private static void Detach(CheckBox checkBox)
    {
        if (checkBox.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            checkBox.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler != null)
            checkBox.Unloaded -= state.UnloadedHandler;

        OnUnloaded(checkBox, state);
        state.IsAttached = false;
    }

    private static void OnLoaded(CheckBox checkBox, State state)
    {
        if (!state.IsReady)
        {
            state.CheckedHandler = (_, _) => AnimateGlyph(checkBox, isChecked: true);
            state.UncheckedHandler = (_, _) => AnimateGlyph(checkBox, isChecked: false);
            checkBox.Checked += state.CheckedHandler;
            checkBox.Unchecked += state.UncheckedHandler;
            state.IsReady = true;
        }

        SyncGlyph(checkBox, animate: false);
    }

    private static void OnUnloaded(CheckBox checkBox, State state)
    {
        if (!state.IsReady)
            return;

        if (state.CheckedHandler != null)
            checkBox.Checked -= state.CheckedHandler;
        if (state.UncheckedHandler != null)
            checkBox.Unchecked -= state.UncheckedHandler;
        state.CheckedHandler = null;
        state.UncheckedHandler = null;
        state.IsReady = false;

        if (TryResolveGlyph(checkBox) is { } glyph)
            UiMotion.Stop(glyph);
    }

    private static void SyncGlyph(CheckBox checkBox, bool animate)
    {
        if (TryResolveGlyph(checkBox) is not { } glyph)
            return;

        if (checkBox.IsChecked == true)
        {
            if (animate && !UiMotion.PrefersReducedMotion)
                AnimateGlyph(checkBox, isChecked: true);
            else
                ShowChecked(glyph);
            return;
        }

        HideUnchecked(glyph);
    }

    private static void AnimateGlyph(CheckBox checkBox, bool isChecked)
    {
        if (TryResolveGlyph(checkBox) is not { } glyph)
            return;

        UiMotion.Stop(glyph);

        if (UiMotion.PrefersReducedMotion)
        {
            if (isChecked)
                ShowChecked(glyph);
            else
                HideUnchecked(glyph);
            return;
        }

        if (isChecked)
        {
            glyph.Opacity = 1;
            glyph.StrokeDashOffset = PathLength;
            glyph.RenderTransformOrigin = new Point(.5, .5);
            var scale = UiMotion.EnsureScale(glyph); scale.ScaleX = scale.ScaleY = .86;
            SurfaceMotion.AnimateTracks(glyph, MotionChannel.Interaction,
                [new(glyph, Shape.StrokeDashOffsetProperty, 0, true),
                 new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.04, 1),
                 new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.04, 1)], EnterMs, UiMotion.OutCubic);
            return;
        }

        var from = glyph.StrokeDashOffset;
        glyph.Opacity = 1;
        UiMotion.Run(
            glyph,
            new[]
            {
                UiMotion.Track(checkBox is Scriblucent.Controls.CollectionSelectionToggle ? EnterMs : ExitMs, UiMotion.InOutSine, from, HiddenDashOffset, value => glyph.StrokeDashOffset = value),
                UiMotion.Track(checkBox is Scriblucent.Controls.CollectionSelectionToggle ? EnterMs : ExitMs, UiMotion.InOutSine, 1, 0, value => glyph.Opacity = value)
            },
            () => HideUnchecked(glyph));
    }

    private static void ShowChecked(Path glyph)
    {
        glyph.Opacity = 1;
        glyph.StrokeDashOffset = 0;
        var scale = UiMotion.EnsureScale(glyph); scale.ScaleX = scale.ScaleY = 1;
    }

    private static void HideUnchecked(Path glyph)
    {
        glyph.StrokeDashOffset = HiddenDashOffset;
        glyph.Opacity = 0;
    }

    private static Path? TryResolveGlyph(CheckBox checkBox)
    {
        checkBox.ApplyTemplate();
        return checkBox.Template?.FindName("CheckGlyph", checkBox) as Path;
    }

    private static State GetOrCreateState(CheckBox checkBox)
    {
        if (checkBox.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        checkBox.SetValue(StateProperty, state);
        return state;
    }
}
