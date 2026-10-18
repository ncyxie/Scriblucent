using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>Keep a virtualized dropdown viewport steady for one opening, then measure afresh.</summary>
public static class PopupListSizeBehavior
{
    private sealed record SizeLock(double Width, double Height, bool HadLocalWidth, bool HadLocalHeight);
    private static readonly DependencyProperty LockProperty = DependencyProperty.RegisterAttached(
        "Lock", typeof(SizeLock), typeof(PopupListSizeBehavior), new PropertyMetadata(null));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PopupListSizeBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        viewer.Loaded -= Loaded;
        viewer.Unloaded -= Unloaded;
        Restore(viewer);
        if (args.NewValue is not true) return;
        viewer.Loaded += Loaded;
        viewer.Unloaded += Unloaded;
        if (viewer.IsLoaded) QueueLock(viewer);
    }

    private static void Loaded(object sender, RoutedEventArgs args) => QueueLock((ScrollViewer)sender);
    private static void QueueLock(ScrollViewer viewer) => viewer.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (!viewer.IsLoaded || !GetIsEnabled(viewer) || viewer.GetValue(LockProperty) is not null) return;
        viewer.UpdateLayout();
        if (viewer.ActualWidth <= 0 || viewer.ActualHeight <= 0) return;
        var state = new SizeLock(viewer.Width, viewer.Height,
            viewer.ReadLocalValue(FrameworkElement.WidthProperty) != DependencyProperty.UnsetValue,
            viewer.ReadLocalValue(FrameworkElement.HeightProperty) != DependencyProperty.UnsetValue);
        viewer.SetValue(LockProperty, state);
        // Only natural dimensions belong to this behavior; explicit sizes and MaxHeight remain authoritative.
        if (double.IsNaN(state.Width)) viewer.SetCurrentValue(FrameworkElement.WidthProperty, viewer.ActualWidth);
        if (double.IsNaN(state.Height)) viewer.SetCurrentValue(FrameworkElement.HeightProperty, viewer.ActualHeight);
    }));

    private static void Unloaded(object sender, RoutedEventArgs args) => Restore((ScrollViewer)sender);
    private static void Restore(ScrollViewer viewer)
    {
        if (viewer.GetValue(LockProperty) is not SizeLock state) return;
        viewer.ClearValue(LockProperty);
        if (double.IsNaN(state.Width))
        {
            if (state.HadLocalWidth) viewer.SetCurrentValue(FrameworkElement.WidthProperty, state.Width);
            else viewer.ClearValue(FrameworkElement.WidthProperty);
        }
        if (double.IsNaN(state.Height))
        {
            if (state.HadLocalHeight) viewer.SetCurrentValue(FrameworkElement.HeightProperty, state.Height);
            else viewer.ClearValue(FrameworkElement.HeightProperty);
        }
    }
}
