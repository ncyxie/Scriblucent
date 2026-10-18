using System.Windows;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

public static class LoadContinuityBehavior
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.RegisterAttached(
        "IsLoading", typeof(bool), typeof(LoadContinuityBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsLoading(DependencyObject target, bool value) => target.SetValue(IsLoadingProperty, value);
    public static bool GetIsLoading(DependencyObject target) => (bool)target.GetValue(IsLoadingProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        SurfaceMotion.Stop(element, MotionChannel.Presence);
        element.Opacity = 1;
        if (e.OldValue is true && e.NewValue is false && element.IsLoaded)
        { element.Opacity = MotionPolicy.Spatial ? .7 : 1; SurfaceMotion.Animate(element, 1, 120, UiMotion.OutSine, false); }
    }
}
