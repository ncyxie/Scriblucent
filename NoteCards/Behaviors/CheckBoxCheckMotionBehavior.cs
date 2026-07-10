using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using NoteCards.Animations;

namespace NoteCards.Behaviors;

/// <summary>
/// Draws the template checkmark in/out via <see cref="UiMotion"/> so checkbox motion stays off WPF storyboards.
/// </summary>
public static class CheckBoxCheckMotionBehavior
{
    public const double PathLength = 14;
    private const double HiddenDashOffset = 16;
    private const int EnterMs = 200;
    private const int ExitMs = 180;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsReady;
        public RoutedEventHandler? LoadedHandler;
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

    private static void Attach(CheckBox checkBox)
    {
        var state = GetOrCreateState(checkBox);
        if (state.IsAttached)
            return;

        state.LoadedHandler = (_, _) =>
        {
            state.IsReady = true;
            SyncGlyph(checkBox, animate: false);
        };
        state.CheckedHandler = (_, _) =>
        {
            if (state.IsReady)
                AnimateGlyph(checkBox, isChecked: true);
        };
        state.UncheckedHandler = (_, _) =>
        {
            if (state.IsReady)
                AnimateGlyph(checkBox, isChecked: false);
        };

        checkBox.Loaded += state.LoadedHandler;
        checkBox.Checked += state.CheckedHandler;
        checkBox.Unchecked += state.UncheckedHandler;
        checkBox.Unloaded += (_, _) => Detach(checkBox);
        state.IsAttached = true;

        if (checkBox.IsLoaded)
        {
            state.IsReady = true;
            SyncGlyph(checkBox, animate: false);
        }
    }

    private static void Detach(CheckBox checkBox)
    {
        if (checkBox.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            checkBox.Loaded -= state.LoadedHandler;
        if (state.CheckedHandler != null)
            checkBox.Checked -= state.CheckedHandler;
        if (state.UncheckedHandler != null)
            checkBox.Unchecked -= state.UncheckedHandler;

        if (TryResolveGlyph(checkBox) is { } glyph)
            UiMotion.Stop(glyph);

        state.IsAttached = false;
        state.IsReady = false;
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
            UiMotion.Run(
                glyph,
                new[]
                {
                    UiMotion.Track(EnterMs, UiMotion.OutCubic, PathLength, 0, value => glyph.StrokeDashOffset = value)
                });
            return;
        }

        var from = glyph.StrokeDashOffset;
        glyph.Opacity = 1;
        UiMotion.Run(
            glyph,
            new[]
            {
                UiMotion.Track(ExitMs, UiMotion.InOutSine, from, HiddenDashOffset, value => glyph.StrokeDashOffset = value),
                UiMotion.Track(ExitMs, UiMotion.InOutSine, 1, 0, value => glyph.Opacity = value)
            },
            () => HideUnchecked(glyph));
    }

    private static void ShowChecked(Path glyph)
    {
        glyph.Opacity = 1;
        glyph.StrokeDashOffset = 0;
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