using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

/// <summary>Measure before/after once; interpolate visible item offsets without remeasuring each frame.</summary>
internal static class LayoutFlipTransition
{
    internal readonly record struct Position(FrameworkElement Element, Point Point);
    internal static List<Position> Capture(FrameworkElement root)
    {
        var result = new List<Position>(MotionTokens.MaxItems);
        if (!MotionPolicy.Spatial || !root.IsLoaded) return result;
        Walk(root, root, result);
        return result;
    }
    private static void Walk(DependencyObject node, FrameworkElement root, List<Position> result)
    {
        if (result.Count >= MotionTokens.MaxItems) return;
        // Dashboard grids own card reflow and container recycling. Section reveal must
        // not add a second layout offset to those same cards' outer containers.
        if (node is DashboardVirtualizedCardGrid) return;
        if (node is FrameworkElement element && !ReferenceEquals(element, root)
            && element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0
            && ItemsControl.ItemsControlFromItemContainer(element) is not null)
        {
            var point = element.TranslatePoint(new Point(), root);
            if (new Rect(point, element.RenderSize).IntersectsWith(new Rect(root.RenderSize)))
                result.Add(new Position(element, point));
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node) && result.Count < MotionTokens.MaxItems; i++)
            Walk(VisualTreeHelper.GetChild(node, i), root, result);
    }
    internal static void Play(FrameworkElement root, List<Position> before)
    {
        if (!MotionPolicy.Spatial || before.Count == 0) return;
        root.UpdateLayout();
        foreach (var position in before)
        {
            var element = position.Element;
            if (!element.IsLoaded || !element.IsVisible) continue;
            var after = element.TranslatePoint(new Point(), root);
            var translate = UiMotion.ChannelTranslate(element, MotionChannel.Layout);
            // Capture includes the current visual offset. Preserve that position on
            // interruption, but always return to the newly arranged slot (zero).
            translate.X += position.Point.X - after.X;
            translate.Y += position.Point.Y - after.Y;
            UiMotion.Run(element, MotionChannel.Layout, new[]
            {
                UiMotion.Track(MotionTokens.Layout, UiMotion.InOutCubic, translate.X, 0, v => translate.X = v),
                UiMotion.Track(MotionTokens.Layout, UiMotion.InOutCubic, translate.Y, 0, v => translate.Y = v)
            });
        }
    }
}
