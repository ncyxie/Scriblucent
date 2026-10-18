using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Animates dashboard section chevron rotation when expand state changes.
/// </summary>
public static class SectionChevronMotionBehavior
{
    private const double ExpandedAngle = 0;
    private const double CollapsedAngle = -90;

    private sealed class State
    {
        public bool HasInitialized;
        public bool InitializationPending;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(SectionChevronMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded",
        typeof(bool?),
        typeof(SectionChevronMotionBehavior),
        new PropertyMetadata(null, OnIsExpandedChanged));

    public static void SetIsExpanded(DependencyObject element, bool value)
        => element.SetValue(IsExpandedProperty, value);

    public static bool GetIsExpanded(DependencyObject element)
        => element.GetValue(IsExpandedProperty) is true;

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (!element.IsLoaded)
        {
            element.Loaded -= OnTargetLoaded;
            element.Loaded += OnTargetLoaded;
            return;
        }

        UpdateExpandedState(element, e.NewValue is true);
    }

    private static void OnTargetLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        UpdateExpandedState(element, GetIsExpanded(element));
    }

    private static void UpdateExpandedState(FrameworkElement element, bool expanded)
    {
        var state = GetOrCreateState(element);
        Apply(element, expanded, animate: state.HasInitialized);
        if (state.HasInitialized || state.InitializationPending) return;
        state.InitializationPending = true;
        // Ancestor state bindings can resolve after a child's Loaded event. Finish
        // that initial binding batch before allowing later changes to animate.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            state.InitializationPending = false;
            if (!element.IsLoaded) return;
            Apply(element, GetIsExpanded(element), animate: false);
            state.HasInitialized = true;
        });
    }

    private static void Apply(FrameworkElement element, bool expanded, bool animate)
    {
        if (element is Control control)
        {
            control.ApplyTemplate();
            if (control.Template?.FindName("DisclosureChevron", control) is FrameworkElement glyph) element = glyph;
        }
        var rotate = EnsureRotate(element);
        var target = expanded ? ExpandedAngle : CollapsedAngle;

        if (!animate || UiMotion.PrefersReducedMotion)
        {
            UiMotion.Stop(element);
            rotate.Angle = target;
            return;
        }

        var from = rotate.Angle;
        UiMotion.Stop(element);
        UiMotion.Run(
            element,
            new[]
            {
                UiMotion.Track(
                    UiMotion.SectionMs,
                    UiMotion.OutCubic,
                    from,
                    target,
                    value => rotate.Angle = value)
            });
    }

    private static RotateTransform EnsureRotate(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        if (element.RenderTransform is RotateTransform existing && !existing.IsFrozen)
            return existing;

        var angle = element.RenderTransform is RotateTransform current ? current.Angle : 0;
        var rotate = new RotateTransform(angle);
        element.RenderTransform = rotate;
        return rotate;
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        element.SetValue(StateProperty, state);
        element.Loaded -= OnTargetLoaded;
        element.Loaded += OnTargetLoaded;
        element.Unloaded += (_, _) => state.HasInitialized = false;
        return state;
    }
}
