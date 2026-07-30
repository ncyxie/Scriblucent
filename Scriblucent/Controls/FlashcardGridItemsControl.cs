using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Controls;

/// <summary>
/// <see cref="ItemsControl"/> that resets <see cref="FlashcardFlipHost"/>'s per-instance
/// animation/extent state before a virtualizing panel recycles a container for a different item.
/// Mirrors the reset done in <see cref="DashboardVirtualizedCardGrid"/>'s
/// ClearContainerForItemOverride for the dashboards' own virtualized card grids.
/// </summary>
public class FlashcardGridItemsControl : ItemsControl
{
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (FindVisualChild<FlashcardFlipHost>(element) is { } flipHost)
            flipHost.PrepareForReuse();

        base.ClearContainerForItemOverride(element, item);
    }

    private static T? FindVisualChild<T>(DependencyObject? source)
        where T : DependencyObject
    {
        if (source is null)
            return null;

        var childCount = VisualTreeHelper.GetChildrenCount(source);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }
}
