using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Moves one shared selection plate between destinations; standalone buttons retain a local fade.
/// </summary>
public static class SidebarNavMotionBehavior
{
    public static readonly DependencyProperty IsGroupHostProperty = DependencyProperty.RegisterAttached(
        "IsGroupHost", typeof(bool), typeof(SidebarNavMotionBehavior), new PropertyMetadata(false, OnGroupChanged));
    public static void SetIsGroupHost(DependencyObject target, bool value) => target.SetValue(IsGroupHostProperty, value);
    public static bool GetIsGroupHost(DependencyObject target) => (bool)target.GetValue(IsGroupHostProperty);
    private sealed class GroupState
    {
        internal Button? Active;
        internal bool Initialized;
    }
    private static readonly ConditionalWeakTable<Grid, GroupState> Groups = new();
    private static void OnGroupChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Grid group) return;
        group.SizeChanged -= GroupSizeChanged;
        group.Unloaded -= GroupUnloaded;
        if (e.NewValue is true) { group.SizeChanged += GroupSizeChanged; group.Unloaded += GroupUnloaded; }
    }
    private static void GroupUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid group && group.FindName("SidebarSelectionPlate") is FrameworkElement plate)
        { SurfaceMotion.Stop(plate); Groups.Remove(group); }
    }
    private static void GroupSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid group && Groups.TryGetValue(group, out var state) && state.Active is { } active)
            MovePlate(group, active, false);
    }
    private static void MovePlate(Grid group, Button button, bool animate)
    {
        if (group.FindName("SidebarSelectionPlate") is not FrameworkElement plate) return;
        var state = Groups.GetValue(group, _ => new());
        var top = button.TranslatePoint(new Point(), group);
        var move = UiMotion.EnsureTranslate(plate);
        plate.RenderTransformOrigin = new Point(.5, .5);
        plate.Width = button.ActualWidth; plate.Height = button.ActualHeight;
        plate.Visibility = Visibility.Visible; move.X = top.X;
        if (state.Initialized && animate && ReferenceEquals(state.Active, button)) return;
        if (!state.Initialized || !animate) { SurfaceMotion.Stop(plate); move.Y = top.Y; }
        else SurfaceMotion.AnimateTracks(plate, MotionChannel.Navigation,
            [new(move, TranslateTransform.YProperty, top.Y, true),
             new(UiMotion.EnsureScale(plate), ScaleTransform.ScaleYProperty, 1, true, 1.025, 1)], 220, UiMotion.InOutCubic);
        state.Active = button; state.Initialized = true;
    }
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
        DependencyObject? parent = VisualTreeHelper.GetParent(button);
        while (parent is not null)
        {
            if (parent is Grid group && GetIsGroupHost(group))
            {
                if (button.Template.FindName(ActiveIndicatorPartName, button) is FrameworkElement old) old.Opacity = 0;
                // Group initialization belongs to the plate, not to each destination.
                // A previously unselected button must animate on its first visit too.
                if (isActive) MovePlate(group, button, animate: true);
                return;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (button.Template.FindName(ActiveIndicatorPartName, button) is not FrameworkElement indicator)
            return;

        var targetOpacity = isActive ? 1.0 : 0.0;

        if (!animate || UiMotion.IsMotionDisabled)
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
