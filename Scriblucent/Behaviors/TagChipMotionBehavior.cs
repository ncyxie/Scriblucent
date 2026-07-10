using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Subtle enter and hover motion for dashboard tag chips.
/// </summary>
public static class TagChipMotionBehavior
{
    private const int EnterMs = 220;
    private const int HoverMs = 160;
    private const double EnterLiftY = 4;
    private const double HoverScale = 1.03;

    private sealed class State
    {
        public bool IsAttached;
        public RoutedEventHandler? LoadedHandler;
        public MouseEventHandler? MouseEnterHandler;
        public MouseEventHandler? MouseLeaveHandler;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(TagChipMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(TagChipMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        if (e.NewValue is true)
            Attach(border);
        else
            Detach(border);
    }

    private static void Attach(Border border)
    {
        var state = GetOrCreateState(border);
        if (state.IsAttached)
            return;

        border.RenderTransformOrigin = new Point(0.5, 0.5);

        state.LoadedHandler = (_, _) =>
        {
            ResetRest(border);
            if (border.IsVisible)
                PlayEnter(border);
        };
        state.MouseEnterHandler = (_, _) => PlayHover(border, isEnter: true);
        state.MouseLeaveHandler = (_, _) => PlayHover(border, isEnter: false);

        border.Loaded += state.LoadedHandler;
        border.Unloaded += (_, _) => Detach(border);
        border.MouseEnter += state.MouseEnterHandler;
        border.MouseLeave += state.MouseLeaveHandler;
        state.IsAttached = true;

        if (border.IsLoaded)
        {
            ResetRest(border);
            if (border.IsVisible)
                PlayEnter(border);
        }
    }

    private static void Detach(Border border)
    {
        if (border.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            border.Loaded -= state.LoadedHandler;
        if (state.MouseEnterHandler != null)
            border.MouseEnter -= state.MouseEnterHandler;
        if (state.MouseLeaveHandler != null)
            border.MouseLeave -= state.MouseLeaveHandler;

        UiMotion.Stop(border);
        state.IsAttached = false;
    }

    private static void ResetRest(Border border)
    {
        UiMotion.Stop(border);
        border.Opacity = 1;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(border);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private static void PlayEnter(Border border)
    {
        if (UiMotion.PrefersReducedMotion)
        {
            ResetRest(border);
            return;
        }

        UiMotion.Stop(border);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(border);
        border.Opacity = 0;
        translate.Y = EnterLiftY;
        scale.ScaleX = UiMotion.StaggerEnterScale;
        scale.ScaleY = UiMotion.StaggerEnterScale;

        UiMotion.Run(
            border,
            new[]
            {
                UiMotion.Track(EnterMs, UiMotion.OutCubic, 0, 1, value => border.Opacity = value),
                UiMotion.Track(EnterMs, UiMotion.OutBack, EnterLiftY, 0, value => translate.Y = value),
                UiMotion.Track(EnterMs, UiMotion.OutBack, UiMotion.StaggerEnterScale, 1, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                })
            });
    }

    private static void PlayHover(Border border, bool isEnter)
    {
        var (scale, _) = UiMotion.EnsureScaleTranslateGroup(border);
        var targetScale = isEnter ? HoverScale : 1.0;
        var scaleFrom = scale.ScaleX;

        UiMotion.Run(
            border,
            new[]
            {
                UiMotion.Track(HoverMs, UiMotion.OutCubic, scaleFrom, targetScale, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                })
            });
    }

    private static State GetOrCreateState(Border border)
    {
        if (border.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        border.SetValue(StateProperty, state);
        return state;
    }
}