using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

internal static class DashboardMotionViewport
{
    private const double VisibilityMargin = 64;

    public static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
                return scrollViewer;

            current = current switch
            {
                FrameworkElement { Parent: DependencyObject parent } => parent,
                FrameworkContentElement { Parent: DependencyObject contentParent } => contentParent,
                _ => VisualTreeHelper.GetParent(current)
            };
        }

        return null;
    }

    public static bool IntersectsViewport(FrameworkElement element, ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null)
            return true;

        if (!element.IsVisible || element.Opacity <= 0.01)
            return false;

        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        try
        {
            var bounds = element.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return IntersectsViewportRect(bounds, scrollViewer);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IntersectsViewportRect(Rect bounds, ScrollViewer scrollViewer)
    {
        var viewport = new Rect(
            0,
            0,
            scrollViewer.ViewportWidth > 0 ? scrollViewer.ViewportWidth : scrollViewer.ActualWidth,
            scrollViewer.ViewportHeight > 0 ? scrollViewer.ViewportHeight : scrollViewer.ActualHeight);
        viewport.Inflate(VisibilityMargin, VisibilityMargin);
        return viewport.IntersectsWith(bounds);
    }

    public static bool IsGridIndexLikelyVisible(
        int index,
        GridReflowMetrics metrics,
        ScrollViewer? scrollViewer,
        double contentOffsetY)
    {
        if (scrollViewer is null)
            return true;

        if (metrics.IsListLayout)
        {
            var top = index * metrics.CellHeight;
            var bottom = top + metrics.CellHeight;
            var viewportTop = contentOffsetY - VisibilityMargin;
            var viewportBottom = contentOffsetY
                + (scrollViewer.ViewportHeight > 0 ? scrollViewer.ViewportHeight : scrollViewer.ActualHeight)
                + VisibilityMargin;
            return bottom >= viewportTop && top <= viewportBottom;
        }

        var row = index / metrics.PerRow;
        var topY = row * metrics.CellHeight;
        var bottomY = topY + metrics.CellHeight;
        var viewTop = contentOffsetY - VisibilityMargin;
        var viewBottom = contentOffsetY
            + (scrollViewer.ViewportHeight > 0 ? scrollViewer.ViewportHeight : scrollViewer.ActualHeight)
            + VisibilityMargin;
        return bottomY >= viewTop && topY <= viewBottom;
    }

    public readonly record struct GridReflowMetrics(bool IsListLayout, double CellWidth, double CellHeight, int PerRow);
}