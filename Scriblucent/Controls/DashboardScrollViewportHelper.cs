using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Controls;

/// <summary>
/// Coordinates dashboard virtualization hosts with animated scroll sessions so viewport
/// remeasure does not run on every compositor frame.
/// </summary>
internal static class DashboardScrollViewportHelper
{
    private static readonly DependencyProperty SessionDepthProperty = DependencyProperty.RegisterAttached(
        "SmoothScrollSessionDepth",
        typeof(int),
        typeof(DashboardScrollViewportHelper),
        new PropertyMetadata(0));

    private static readonly DependencyProperty SessionHostsProperty = DependencyProperty.RegisterAttached(
        "SmoothScrollSessionHosts",
        typeof(List<DashboardVirtualizationHost>),
        typeof(DashboardScrollViewportHelper),
        new PropertyMetadata(null));

    public static void BeginAnimatedScroll(ScrollViewer scrollViewer)
    {
        var depth = (int)scrollViewer.GetValue(SessionDepthProperty) + 1;
        scrollViewer.SetValue(SessionDepthProperty, depth);
        if (depth != 1)
            return;

        // Snapshot hosts once per session; per-frame offset notifications and session
        // teardown must not walk the full visual tree.
        var hosts = EnumerateHosts(scrollViewer).ToList();
        scrollViewer.SetValue(SessionHostsProperty, hosts);

        SmoothScrollCompositor.Begin(scrollViewer, hosts);

        foreach (var host in hosts)
            host.NotifySmoothScrollStarted();
    }

    public static void NotifyVisualScrollOffsetChanged(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(SessionHostsProperty) is not List<DashboardVirtualizationHost> hosts)
            return;

        foreach (var host in hosts)
            host.NotifyVisualScrollOffsetChanged();
    }

    public static void EndAnimatedScroll(ScrollViewer scrollViewer, double? finalX = null, double? finalY = null)
    {
        var depth = (int)scrollViewer.GetValue(SessionDepthProperty);
        if (depth <= 0)
            return;

        depth--;
        scrollViewer.SetValue(SessionDepthProperty, depth);
        if (depth != 0)
            return;

        SmoothScrollCompositor.End(
            scrollViewer,
            finalX ?? scrollViewer.HorizontalOffset,
            finalY ?? scrollViewer.VerticalOffset);

        if (scrollViewer.GetValue(SessionHostsProperty) is List<DashboardVirtualizationHost> hosts)
        {
            scrollViewer.ClearValue(SessionHostsProperty);
            scrollViewer.Dispatcher.BeginInvoke(() =>
            {
                foreach (var host in hosts)
                    host.NotifySmoothScrollEnded();
            }, DispatcherPriority.Loaded);
        }
    }

    private static IEnumerable<DashboardVirtualizationHost> EnumerateHosts(DependencyObject? root)
    {
        if (root is null)
            yield break;

        if (root is DashboardVirtualizationHost host)
            yield return host;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateHosts(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }
}
