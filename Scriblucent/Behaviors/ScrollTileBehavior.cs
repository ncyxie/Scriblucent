using Scriblucent.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>
/// Flips hosted card rows into Skia tile mode while their scroll viewer pans (any axis) and
/// back to live when it settles - the same session model as the dashboard grids. Attached to
/// ScrollViewers whose content items are DashboardTileHost-wrapped rows (dashboard recent
/// strips, calendar rails, window lists).
/// </summary>
public static class ScrollTileBehavior
{
    private const int SettleMs = 250;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ScrollTileBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private sealed class State
    {
        public ScrollChangedEventHandler? Handler;
        public DispatcherTimer? SettleTimer;
        public bool BitmapActive;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(ScrollTileBehavior),
        new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;

        if (e.NewValue is true)
            Attach(viewer);
        else
            Detach(viewer);
    }

    private static void Attach(ScrollViewer viewer)
    {
        var state = new State();
        viewer.SetValue(StateProperty, state);

        state.Handler = (_, args) =>
        {
            if (args.HorizontalChange == 0 && args.VerticalChange == 0)
                return;

            if (!state.BitmapActive)
            {
                state.BitmapActive = true;
                SetScrollBitmapMode(viewer, true);
            }

            state.SettleTimer?.Stop();
            state.SettleTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(SettleMs)
            };
            state.SettleTimer.Tick += (_, _) =>
            {
                state.SettleTimer.Stop();
                state.SettleTimer = null;
                state.BitmapActive = false;
                SetScrollBitmapMode(viewer, false);
            };
            state.SettleTimer.Start();
        };

        viewer.ScrollChanged += state.Handler;
        viewer.Unloaded += OnViewerUnloaded;
    }

    private static void Detach(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is not State state)
            return;

        if (state.Handler is not null)
            viewer.ScrollChanged -= state.Handler;

        state.SettleTimer?.Stop();
        viewer.Unloaded -= OnViewerUnloaded;
        viewer.ClearValue(StateProperty);
        SetScrollBitmapMode(viewer, false);
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
            Detach(viewer);
    }

    private static void SetScrollBitmapMode(ScrollViewer viewer, bool bitmapMode)
    {
        if (viewer.Content is not ItemsControl items)
            return;

        var panel = VisualTreeHelper.GetChildrenCount(items) > 0
            ? VisualTreeHelper.GetChild(items, 0) as Panel
            : null;
        if (panel is null)
            return;

        foreach (var child in panel.Children)
        {
            if (child is ContentPresenter { Content: DashboardTileHost host1 })
                host1.IsBitmapMode = bitmapMode;
            else if (child is ContentControl { Content: DashboardTileHost host2 })
                host2.IsBitmapMode = bitmapMode;
        }
    }
}
