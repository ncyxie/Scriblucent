using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Fold a group without resizing its cards; resync page slices after neighbouring groups move.</summary>
public sealed class DashboardGroupRevealHost : LayoutRevealHost
{
    public DashboardGroupRevealHost() => AnimateLargeExtent = true;

    protected override void OnRevealCompleted(bool stateChanged)
    {
        if (!stateChanged || !IsLoaded) return;
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ItemsControl owner)
            {
                Refresh(owner);
                break;
            }
    }

    private static void Refresh(DependencyObject parent)
    {
        if (parent is DashboardVirtualizationHost host)
        {
            host.RequestViewportRefresh();
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Refresh(VisualTreeHelper.GetChild(parent, i));
    }
}
