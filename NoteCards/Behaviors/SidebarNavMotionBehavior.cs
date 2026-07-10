using System.Windows;
using System.Windows.Controls;
using NoteCards.Animations;

namespace NoteCards.Behaviors;

/// <summary>
/// Fades the sidebar active indicator when dashboard selection changes.
/// </summary>
public static class SidebarNavMotionBehavior
{
    private const string ActiveIndicatorPartName = "ActiveIndicator";

    private sealed class State
    {
        public bool HasInitialized;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(SidebarNavMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(SidebarNavMotionBehavior),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static void SetIsActive(DependencyObject element, bool value)
        => element.SetValue(IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject element)
        => (bool)element.GetValue(IsActiveProperty);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
            return;

        if (!button.IsLoaded)
        {
            button.Loaded -= OnTargetLoaded;
            button.Loaded += OnTargetLoaded;
            return;
        }

        UpdateActiveState(button, e.NewValue is true);
    }

    private static void OnTargetLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        button.Loaded -= OnTargetLoaded;
        UpdateActiveState(button, GetIsActive(button));
    }

    private static void UpdateActiveState(Button button, bool isActive)
    {
        var state = GetOrCreateState(button);
        Apply(button, isActive, animate: state.HasInitialized);
        state.HasInitialized = true;
    }

    private static void Apply(Button button, bool isActive, bool animate)
    {
        button.ApplyTemplate();
        if (button.Template.FindName(ActiveIndicatorPartName, button) is not FrameworkElement indicator)
            return;

        var targetOpacity = isActive ? 1.0 : 0.0;

        if (!animate || UiMotion.PrefersReducedMotion)
        {
            UiMotion.Stop(button);
            indicator.Opacity = targetOpacity;
            return;
        }

        var fromOpacity = indicator.Opacity;
        UiMotion.Stop(button);
        UiMotion.Run(
            button,
            new[]
            {
                UiMotion.Track(
                    UiMotion.FastMs,
                    UiMotion.OutCubic,
                    fromOpacity,
                    targetOpacity,
                    value => indicator.Opacity = value)
            });
    }

    private static State GetOrCreateState(Button button)
    {
        if (button.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        button.SetValue(StateProperty, state);
        return state;
    }
}