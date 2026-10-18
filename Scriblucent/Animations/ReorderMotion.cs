using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.ViewModels;

namespace Scriblucent.Animations;

/// <summary>Retargetable settling on realized item containers; never changes collection layout during drag.</summary>
internal static class ReorderMotion
{
    private sealed record Running(Transform Original, TransformGroup Group, TranslateTransform Offset);
    private static readonly ConditionalWeakTable<FrameworkElement, Running> Active = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Generation> Generations = new();
    private sealed class Generation { public int Value; }
    public static bool Commit(ItemsControl list, Func<bool> mutation, IReadOnlySet<object>? incoming = null)
    {
        FrameworkElement scope = list;
        if (ItemReorderBehavior.GetKind(list) == ItemReorderKind.DashboardItems
            && Window.GetWindow(list) is MainWindow { DataContext: MainViewModel model } main
            && main.FindName(model.ActiveDashboard + "DashboardView") is FrameworkElement page) scope = page;
        var before = new Dictionary<object, Point>();
        foreach (var entry in Containers(scope, list))
        {
            before.TryAdd(entry.Item, entry.Container.TranslatePoint(new Point(), scope));
            Reset(entry.Container);
        }
        var generation = Generations.GetOrCreateValue(scope); int version = ++generation.Value;
        bool changed;
        DashboardMotionEngine.BeginPinReorder();
        try { changed = mutation(); }
        finally { DashboardMotionEngine.EndPinReorder(); }
        if (!changed || UiMotion.PrefersReducedMotion) return changed;
        list.Dispatcher.BeginInvoke(() =>
        {
            if (!scope.IsLoaded || generation.Value != version) return;
            scope.UpdateLayout();
            foreach (var entry in Containers(scope, list))
            {
                var (item, container) = entry;
                if (incoming?.Contains(item) == true || !before.TryGetValue(item, out var old)) continue;
                var current = container.TranslatePoint(new Point(), scope);
                var delta = old - current;
                if (delta.Length < 0.5 || Math.Abs(delta.Y) > Math.Max(800, scope.ActualHeight)) continue;
                var original = container.RenderTransform;
                var offset = new TranslateTransform(delta.X, delta.Y);
                var group = new TransformGroup(); group.Children.Add(original); group.Children.Add(offset);
                var running = new Running(original, group, offset);
                Active.Add(container, running); container.SetCurrentValue(UIElement.RenderTransformProperty, group);
                container.Unloaded += Unloaded;
                container.DataContextChanged += Recycled;
                var x = new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(UiMotion.ReflowMs)) { EasingFunction = UiMotion.OutCubic };
                var y = new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(UiMotion.ReflowMs)) { EasingFunction = UiMotion.OutCubic };
                y.Completed += (_, _) => { if (Active.TryGetValue(container, out var present) && ReferenceEquals(present, running)) Reset(container); };
                offset.BeginAnimation(TranslateTransform.XProperty, x); offset.BeginAnimation(TranslateTransform.YProperty, y);
            }
        }, DispatcherPriority.Render);
        return true;
    }
    private static IEnumerable<(object Item, FrameworkElement Container)> Containers(FrameworkElement scope, ItemsControl initiatingList)
    {
        if (ReferenceEquals(scope, initiatingList))
        {
            foreach (var container in ItemReorderBehavior.Realized(initiatingList))
                yield return (initiatingList.ItemContainerGenerator.ItemFromContainer(container), container);
            yield break;
        }
        // Prune offscreen visual branches before visiting their children. A cross-group
        // move settles visible neighbours in both lists without realizing any hidden cards.
        var viewport = new Rect(scope.RenderSize);
        var nodes = new Stack<DependencyObject>(); nodes.Push(scope);
        while (nodes.TryPop(out var node))
        {
            if (node is FrameworkElement element && !ReferenceEquals(element, scope))
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                var bounds = element.TransformToAncestor(scope).TransformBounds(new Rect(element.RenderSize));
                if (!bounds.IntersectsWith(viewport)) continue;
            }
            if (node is DashboardVirtualizedCardGrid grid)
            {
                foreach (var container in ItemReorderBehavior.Realized(grid))
                    if (container.TransformToAncestor(scope).TransformBounds(new Rect(container.RenderSize)).IntersectsWith(viewport))
                        yield return (grid.ItemContainerGenerator.ItemFromContainer(container), container);
                continue;
            }
            if (node is not Visual) continue;
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) nodes.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
    private static void Unloaded(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);
    private static void Recycled(object sender, DependencyPropertyChangedEventArgs args) => Reset((FrameworkElement)sender);
    private static void Reset(FrameworkElement container)
    {
        if (!Active.TryGetValue(container, out var running)) return;
        Active.Remove(container); container.Unloaded -= Unloaded;
        container.DataContextChanged -= Recycled;
        running.Offset.BeginAnimation(TranslateTransform.XProperty, null); running.Offset.BeginAnimation(TranslateTransform.YProperty, null);
        if (ReferenceEquals(container.RenderTransform, running.Group)) container.SetCurrentValue(UIElement.RenderTransformProperty, running.Original);
    }
}
