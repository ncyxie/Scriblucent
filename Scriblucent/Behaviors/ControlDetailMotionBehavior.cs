using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Finite paint/transform feedback. Native controls retain their input and automation contracts.</summary>
public static class ControlDetailMotionBehavior
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(ControlDetailMotionBehavior), new PropertyMetadata(false, Expanded));
    public static void SetIsExpanded(DependencyObject target, bool value) => target.SetValue(IsExpandedProperty, value);
    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);
    private static void Expanded(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if (element.RenderTransform is not RotateTransform rotation || rotation.IsFrozen)
            element.RenderTransform = rotation = new RotateTransform();
        element.Unloaded -= DetailUnloaded; element.Unloaded += DetailUnloaded;
        SurfaceMotion.AnimateTracks(element, MotionChannel.Navigation,
            new[] { new NativeMotionTrack(rotation, RotateTransform.AngleProperty, e.NewValue is true ? 180 : 0, true) },
            160, UiMotion.InOutSine, instant: !element.IsLoaded);
    }
    private static void DetailUnloaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        SurfaceMotion.Stop(element);
        if (element.ReadLocalValue(IsExpandedProperty) != DependencyProperty.UnsetValue && element.RenderTransform is RotateTransform rotation)
            rotation.Angle = GetIsExpanded(element) ? 180 : 0;
        if (element.ReadLocalValue(IsOnProperty) != DependencyProperty.UnsetValue)
            UiMotion.ChannelTranslate(element, MotionChannel.Selection).X = GetIsOn(element) ? 18 : 0;
        if (element.ReadLocalValue(IsLitProperty) != DependencyProperty.UnsetValue)
            element.SetCurrentValue(UIElement.OpacityProperty, GetIsLit(element) ? 1d : 0d);
    }

    public static readonly DependencyProperty IsLitProperty = DependencyProperty.RegisterAttached(
        "IsLit", typeof(bool), typeof(ControlDetailMotionBehavior), new PropertyMetadata(false, Lit));
    public static void SetIsLit(DependencyObject target, bool value) => target.SetValue(IsLitProperty, value);
    public static bool GetIsLit(DependencyObject target) => (bool)target.GetValue(IsLitProperty);
    private static void Lit(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        element.Unloaded -= DetailUnloaded; element.Unloaded += DetailUnloaded;
        SurfaceMotion.AnimateTracks(element, MotionChannel.Selection,
            new[] { new NativeMotionTrack(element, UIElement.OpacityProperty, e.NewValue is true ? 1 : 0) },
            180, UiMotion.OutSine, instant: !element.IsLoaded);
    }

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(ControlDetailMotionBehavior), new PropertyMetadata(false, Switched));
    public static void SetIsOn(DependencyObject target, bool value) => target.SetValue(IsOnProperty, value);
    public static bool GetIsOn(DependencyObject target) => (bool)target.GetValue(IsOnProperty);
    private static void Switched(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement thumb) return;
        var translate = UiMotion.ChannelTranslate(thumb, MotionChannel.Selection);
        thumb.Unloaded -= DetailUnloaded; thumb.Unloaded += DetailUnloaded;
        // Reduced motion changes position immediately; only the native track fill may fade.
        SurfaceMotion.AnimateTracks(thumb, MotionChannel.Selection,
            new[] { new NativeMotionTrack(translate, TranslateTransform.XProperty, e.NewValue is true ? 18 : 0, true) },
            180, UiMotion.InOutSine, instant: !thumb.IsLoaded);
    }
}
