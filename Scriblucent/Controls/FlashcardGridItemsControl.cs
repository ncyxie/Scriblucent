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
    private ScrollViewer? _viewer;
    private int _cacheColumns;
    public override void OnApplyTemplate()
    {
        if (_viewer is { } previous) previous.ScrollChanged -= ViewportChanged;
        base.OnApplyTemplate();
        // This viewport recycles rows as its offset changes. Commit every eased position
        // instead of translating it between the dashboard compositor's delayed commits.
        if (GetTemplateChild("PART_ScrollViewer") is ScrollViewer viewer)
        {
            _viewer = viewer; _cacheColumns = 0;
            viewer.ScrollChanged += ViewportChanged;
            SmoothScrollCompositor.SetDirectOffsetMode(viewer, true);
            Scriblucent.Behaviors.ScrollTileBehavior.SetIsEnabled(viewer, true);
            UpdateRowCache();
        }
    }

    private void ViewportChanged(object sender, ScrollChangedEventArgs args)
    { if (args.ViewportWidthChange != 0) UpdateRowCache(); }
    private void UpdateRowCache()
    {
        if (_viewer is not { ViewportWidth: > 0 } viewer) return;
        var columns = Math.Max(1, (int)(viewer.ViewportWidth / 332));
        if (_cacheColumns == columns) return;
        _cacheColumns = columns;
        // WPF coerces Pixel cache lengths to Item for this ItemsControl. Express one
        // row in items so a 248-DIP request cannot accidentally realize 248 cards.
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(columns, columns));
    }

    internal void ReleaseScrollTiles()
    {
        if (_viewer is { } viewer)
        {
            Scriblucent.Behaviors.SmoothScrollBehavior.Interrupt(viewer);
            Scriblucent.Behaviors.ScrollTileBehavior.SetIsEnabled(viewer, false);
        }
        ReleaseHosts(this);
        static void ReleaseHosts(DependencyObject root)
        {
            if (root is DashboardTileHost host) { host.ReleaseScrollTile(); return; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ReleaseHosts(VisualTreeHelper.GetChild(root, i));
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (FindVisualChild<FlashcardFlipHost>(element) is { } flipHost)
            flipHost.PrepareForReuse();

        base.ClearContainerForItemOverride(element, item);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        using var timing = Scriblucent.Rendering.FlashcardScrollTrace.Measure(Scriblucent.Rendering.FlashcardScrollTrace.Kind.Prepare, item);
        base.PrepareContainerForItemOverride(element, item);
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
