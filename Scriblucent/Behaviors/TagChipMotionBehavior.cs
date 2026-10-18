using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Quiet hover; only a semantic tag addition gets an entrance.</summary>
public static class TagChipMotionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TagChipMotionBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    private static FrameworkElement Host(Border border) => border.Child as FrameworkElement ?? border;
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.Loaded -= Loaded; border.Unloaded -= Unloaded;
        border.MouseEnter -= Enter; border.MouseLeave -= Leave;
        if ((bool)e.NewValue)
        {
            border.Loaded += Loaded; border.Unloaded += Unloaded;
            border.MouseEnter += Enter; border.MouseLeave += Leave;
            if (border.IsLoaded) Loaded(border, new RoutedEventArgs());
        }
        else { UiMotion.Cleanup(border); SurfaceLight.Reset(border); }
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(border); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ItemsControl list) continue;
            ItemPresenceMotionBehavior.SetEnterDuration(list, 160);
            ItemPresenceMotionBehavior.SetRise(list, 4);
            ItemPresenceMotionBehavior.SetIsEnabled(list, true);
            break;
        }
        UiMotion.ChannelTranslate(Host(border), MotionChannel.Interaction).Y = 0;
    }
    private static void Unloaded(object sender, RoutedEventArgs e)
    { var border = (Border)sender; UiMotion.Cleanup(border); SurfaceLight.Reset(border); }
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e) => Hover((Border)sender, true);
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) => Hover((Border)sender, false);
    private static void Hover(Border border, bool enter)
    {
        if (!MotionPolicy.Spatial || Scriblucent.Controls.DashboardScrollRenderGate.IsSmoothScrollRendering(border)) return;
        // Light belongs to the chip surface; its label stays in place.
        var tracks = new List<NativeMotionTrack>();
        SurfaceLight.AddTracks(border, tracks, enter, .45, neutral: true);
        SurfaceMotion.AnimateTracks(border, MotionChannel.Interaction, tracks, enter ? 140 : 180, UiMotion.OutSine);
    }
}
