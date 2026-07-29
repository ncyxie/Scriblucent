using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Scriblucent.Animations;
using Scriblucent.Behaviors;

namespace Scriblucent.Controls;

/// <summary>
/// Detects active smooth-scroll compositor sessions and strips drop shadows from newly
/// virtualized dashboard cards so crowded scroll stays render-bound instead of shadow-bound.
/// </summary>
internal static class DashboardScrollRenderGate
{
    public static bool IsSmoothScrollRendering(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer && SmoothScrollCompositor.IsActive(scrollViewer))
                return true;

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    public static void ResetHoveredCards(DependencyObject root)
    {
        foreach (var element in EnumerateMotionElements(root))
        {
            if (CardSurfaceMotionBehavior.GetIsEnabled(element))
                CardSurfaceMotionBehavior.ForceRestHoverState(element);
            if (ChromeHoverMotionBehavior.GetIsEnabled(element))
                ChromeHoverMotionBehavior.ForceRestHoverState(element);
        }
    }

    public static void RestorePointerHoverStates(DependencyObject root)
    {
        foreach (var element in EnumerateMotionElements(root))
        {
            if (!element.IsMouseOver)
                continue;

            if (CardSurfaceMotionBehavior.GetIsEnabled(element))
                CardSurfaceMotionBehavior.SyncPointerState(element);
            if (ChromeHoverMotionBehavior.GetIsEnabled(element))
                ChromeHoverMotionBehavior.SyncPointerState(element);
        }
    }

    public static void StripRealizedCardShadows(IReadOnlyList<DashboardVirtualizationHost> hosts)
    {
        var reactivated = 0;
        foreach (var host in hosts)
        {
            if (host.Child is DashboardVirtualizedCardGrid grid)
                reactivated += StripRealizedContainers(grid);
        }

        if (reactivated > 0 && Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"shadowResweep\t{reactivated}");
    }

    private static readonly DependencyProperty CachedMotionCardProperty = DependencyProperty.RegisterAttached(
        "CachedMotionCard", typeof(FrameworkElement), typeof(DashboardScrollRenderGate), new PropertyMetadata(null));

    public static void TryStripContainerShadow(DependencyObject container)
        => TryStripContainerShadow(container, out _);

    /// <summary>
    /// Same descendant search as <see cref="FindMotionCard"/>, but resolved once per container
    /// instance and cached — recycled containers reuse a fixed ItemTemplate visual tree, so the
    /// resolved motion-card descendant never changes across item swaps.
    /// Matches CardSurfaceMotionBehavior OR ChromeHoverMotionBehavior; no current dashboard card
    /// template sets ChromeHoverMotionBehavior, so callers that only care about CardSurface cards
    /// must re-check CardSurfaceMotionBehavior.GetIsEnabled on the result themselves.
    /// </summary>
    internal static FrameworkElement? ResolveMotionCard(DependencyObject container)
    {
        if (container.ReadLocalValue(CachedMotionCardProperty) != DependencyProperty.UnsetValue)
            return (FrameworkElement?)container.GetValue(CachedMotionCardProperty);

        var card = FindMotionCard(container);
        container.SetValue(CachedMotionCardProperty, card);
        return card;
    }

    private static void TryStripContainerShadow(DependencyObject container, out bool wasLive)
    {
        wasLive = false;

        if (!IsSmoothScrollRendering(container))
            return;

        if (ResolveMotionCard(container) is not FrameworkElement card)
            return;

        if (ResolveShadowHost(card) is not FrameworkElement shadowHost)
            return;

        wasLive = shadowHost.Effect is DropShadowEffect;
        DashboardShadowSuspender.TrySuspendShadowHost(
            DashboardShadowSuspender.AnimatedScrollShadowScope,
            shadowHost);
        DashboardScrollSurfaceCache.TryQueue(shadowHost);
    }

    internal static FrameworkElement? ResolveShadowHost(FrameworkElement card)
    {
        if (ChromeHoverMotionBehavior.GetIsEnabled(card))
            return ResolveChromeShadowHost(card);

        var shadowName = CardSurfaceMotionBehavior.GetShadowElementName(card);
        if (!string.IsNullOrWhiteSpace(shadowName)
            && card.FindName(shadowName) is FrameworkElement named)
            return named;

        if (CardSurfaceMotionBehavior.GetProfile(card) == CardSurfaceMotionProfile.Listing
            && VisualTreeHelper.GetChildrenCount(card) > 0
            && VisualTreeHelper.GetChild(card, 0) is FrameworkElement inner)
            return inner;

        if (card.Effect is DropShadowEffect)
            return card;

        var childCount = VisualTreeHelper.GetChildrenCount(card);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(card, i) is FrameworkElement child
                && child.Effect is DropShadowEffect)
                return child;
        }

        return null;
    }

    private static FrameworkElement? ResolveChromeShadowHost(FrameworkElement card)
    {
        if (card is not Control control)
            return card.Effect is DropShadowEffect ? card : null;

        control.ApplyTemplate();
        var hostName = ChromeHoverMotionBehavior.GetMotionHostName(control);
        if (!string.IsNullOrWhiteSpace(hostName)
            && control.Template.FindName(hostName, control) is FrameworkElement host)
            return host.Effect is DropShadowEffect ? host : null;

        return card.Effect is DropShadowEffect ? card : null;
    }

    private static int StripRealizedContainers(DashboardVirtualizedCardGrid grid)
    {
        if (grid.ResolveItemsPanel() is not Panel panel)
            return 0;

        var reactivated = 0;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is DependencyObject container)
            {
                TryStripContainerShadow(container, out var wasLive);
                if (wasLive)
                    reactivated++;
            }
        }

        return reactivated;
    }

    private static FrameworkElement? FindMotionCard(DependencyObject root)
    {
        if (root is FrameworkElement element
            && (CardSurfaceMotionBehavior.GetIsEnabled(element)
                || ChromeHoverMotionBehavior.GetIsEnabled(element)))
            return element;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindMotionCard(VisualTreeHelper.GetChild(root, i)) is { } nested)
                return nested;
        }

        return null;
    }

    private static IEnumerable<FrameworkElement> EnumerateMotionElements(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            if (CardSurfaceMotionBehavior.GetIsEnabled(element))
                yield return element;
            if (ChromeHoverMotionBehavior.GetIsEnabled(element))
                yield return element;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateMotionElements(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }
}