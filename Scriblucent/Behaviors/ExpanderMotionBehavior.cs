using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

public static class ExpanderMotionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ExpanderMotionBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Expander expander) return;
        expander.Loaded -= Loaded; expander.Expanded -= Toggled; expander.Collapsed -= Toggled;
        if (e.NewValue is true) { expander.Loaded += Loaded; expander.Expanded += Toggled; expander.Collapsed += Toggled; }
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Apply((Expander)sender, false);
    private static void Toggled(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(sender, e.OriginalSource)) Apply((Expander)sender, true); }
    private static void Apply(Expander expander, bool animate)
    {
        expander.ApplyTemplate();
        if (expander.Template.FindName("DisclosureContent", expander) is FrameworkElement content)
            UiMotion.AnimateLayoutReveal(content, expander.IsExpanded, animate: animate);
        if (expander.Template.FindName("DisclosureChevron", expander) is FrameworkElement chevron)
        {
            var rotate = chevron.RenderTransform as RotateTransform ?? new RotateTransform();
            chevron.RenderTransform = rotate; chevron.RenderTransformOrigin = new(.5, .5);
            SurfaceMotion.AnimateTracks(chevron, MotionChannel.Interaction,
                [new(rotate, RotateTransform.AngleProperty, expander.IsExpanded ? 90 : 0, true)],
                180, UiMotion.InOutSine, instant: !animate);
        }
    }
}
