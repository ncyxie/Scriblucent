using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>First presence and deliberate section reveals; scroll realization never replays entrances.</summary>
public static class RecentStripEnterMotionBehavior
{
    private sealed class State { internal bool Entered; }
    private static readonly ConditionalWeakTable<ItemsControl, State> States = new();
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(RecentStripEnterMotionBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.Loaded -= Loaded;
        if ((bool)e.NewValue)
        {
            list.Loaded += Loaded;
            ItemPresenceMotionBehavior.SetIsEnabled(list, true);
            if (list.IsLoaded) Loaded(list, new RoutedEventArgs());
        }
        else ItemPresenceMotionBehavior.SetIsEnabled(list, false);
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var list = (ItemsControl)sender;
        var state = States.GetValue(list, _ => new());
        if (state.Entered) return;
        state.Entered = true;
        list.Dispatcher.BeginInvoke(() => RequestEnterAnimation(list), DispatcherPriority.Loaded);
    }
    public static void RequestEnterAnimation(ItemsControl list)
    {
        if (!GetIsEnabled(list) || UiMotion.ShouldSkipDashboardRevealMotion() || !list.IsVisible) return;
        var elements = LayoutFlipTransition.Capture(list).Select(p => p.Element).ToArray();
        UiMotion.AnimateStaggeredContentEnter(elements, 220, 8, 30, enterScale: 1);
    }
    public static void TryRequestEnterAnimation(DependencyObject root)
    {
        if (root is ItemsControl list && GetIsEnabled(list)) RequestEnterAnimation(list);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) TryRequestEnterAnimation(VisualTreeHelper.GetChild(root, i));
    }
    public static void PrepEnterHold(ItemsControl list) { }
    public static void TryPrepEnterHold(DependencyObject root) { }
}
