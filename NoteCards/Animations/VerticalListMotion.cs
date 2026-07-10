using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NoteCards.Animations;

/// <summary>
/// Pair-swap and enter motion for vertical <see cref="ItemsControl"/> lists (quiz editor, etc.).
/// </summary>
public static class VerticalListMotion
{
    public static void AnimateItemEnter(
        FrameworkElement host,
        ItemsControl itemsControl,
        object item,
        int durationMs = UiMotion.QuickMs,
        double offsetY = UiMotion.SubtleLiftY)
    {
        if (UiMotion.PrefersReducedMotion || host is null || itemsControl is null)
            return;

        void TryAnimate()
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                return;

            UiMotion.AnimateContentEnter(container, durationMs, offsetY);
        }

        host.Dispatcher.BeginInvoke(() =>
        {
            itemsControl.UpdateLayout();
            if (itemsControl.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement)
            {
                TryAnimate();
                return;
            }

            void OnStatusChanged(object? sender, EventArgs args)
            {
                if (itemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                    return;

                itemsControl.ItemContainerGenerator.StatusChanged -= OnStatusChanged;
                TryAnimate();
            }

            itemsControl.ItemContainerGenerator.StatusChanged += OnStatusChanged;
        }, DispatcherPriority.Loaded);
    }

    public static bool AnimatePairSwap(
        FrameworkElement host,
        ItemsControl itemsControl,
        object primaryItem,
        object secondaryItem,
        Action applyMove,
        Action? onComplete = null)
    {
        if (host is null || itemsControl is null)
        {
            applyMove();
            onComplete?.Invoke();
            return false;
        }

        if (UiMotion.PrefersReducedMotion)
        {
            applyMove();
            onComplete?.Invoke();
            return false;
        }

        itemsControl.UpdateLayout();
        if (!TryResolvePairContainers(itemsControl, primaryItem, secondaryItem, out var primary, out var secondary, out var parent))
        {
            applyMove();
            onComplete?.Invoke();
            return false;
        }

        var primaryOldY = MeasureTopOffset(primary, parent);
        var secondaryOldY = MeasureTopOffset(secondary, parent);
        applyMove();

        host.Dispatcher.BeginInvoke(() =>
        {
            itemsControl.UpdateLayout();
            if (!TryResolvePairContainers(itemsControl, primaryItem, secondaryItem, out primary, out secondary, out parent))
            {
                onComplete?.Invoke();
                return;
            }

            RunFlipPair(primary, secondary, parent, primaryOldY, secondaryOldY, onComplete);
        }, DispatcherPriority.Render);

        return true;
    }

    public static ItemsControl? FindNestedItemsControl(DependencyObject root)
    {
        if (root is null)
            return null;

        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is ItemsControl itemsControl)
                    return itemsControl;

                queue.Enqueue(child);
            }
        }

        return null;
    }

    private static bool TryResolvePairContainers(
        ItemsControl itemsControl,
        object primaryItem,
        object secondaryItem,
        out FrameworkElement primaryContainer,
        out FrameworkElement secondaryContainer,
        out UIElement parent)
    {
        primaryContainer = null!;
        secondaryContainer = null!;
        parent = null!;

        if (itemsControl.ItemContainerGenerator.ContainerFromItem(primaryItem) is not FrameworkElement primary)
            return false;

        if (itemsControl.ItemContainerGenerator.ContainerFromItem(secondaryItem) is not FrameworkElement secondary)
            return false;

        if (VisualTreeHelper.GetParent(primary) is not UIElement sharedParent
            || !ReferenceEquals(VisualTreeHelper.GetParent(secondary), sharedParent))
        {
            return false;
        }

        primaryContainer = primary;
        secondaryContainer = secondary;
        parent = sharedParent;
        return true;
    }

    private static void RunFlipPair(
        FrameworkElement first,
        FrameworkElement second,
        UIElement parent,
        double firstOldY,
        double secondOldY,
        Action? onComplete)
    {
        parent.UpdateLayout();
        var firstDelta = firstOldY - MeasureTopOffset(first, parent);
        var secondDelta = secondOldY - MeasureTopOffset(second, parent);

        var animations = 0;
        if (Math.Abs(firstDelta) >= 0.5)
            animations++;
        if (Math.Abs(secondDelta) >= 0.5)
            animations++;

        if (animations == 0)
        {
            onComplete?.Invoke();
            return;
        }

        var remaining = animations;
        void OnOneComplete()
        {
            remaining--;
            if (remaining > 0)
                return;

            onComplete?.Invoke();
        }

        AnimateFlipOffset(first, firstDelta, OnOneComplete);
        AnimateFlipOffset(second, secondDelta, OnOneComplete);
    }

    private static void AnimateFlipOffset(FrameworkElement element, double startOffset, Action? onComplete)
    {
        if (Math.Abs(startOffset) < 0.5)
        {
            onComplete?.Invoke();
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0);
        var translate = UiMotion.EnsureTranslate(element);
        UiMotion.Stop(element);
        UiMotion.Clear(element, UIElement.OpacityProperty);
        translate.Y = startOffset;

        UiMotion.RunComposited(
            element,
            new[]
            {
                UiMotion.Track(UiMotion.ReflowMs, UiMotion.OutQuad, startOffset, 0, value => translate.Y = value)
            },
            () =>
            {
                translate.Y = 0;
                onComplete?.Invoke();
            });
    }

    private static double MeasureTopOffset(FrameworkElement element, UIElement relativeTo)
    {
        try
        {
            return element.TransformToAncestor(relativeTo).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}