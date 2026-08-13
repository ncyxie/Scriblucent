using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Animations;

internal static class DashboardMotionCardCatalog
{
    public static bool TryGetItemId(FrameworkElement element, out Guid id)
    {
        id = Guid.Empty;
        if (TryGetIdFromObject(element.Tag, out id))
            return true;

        return TryGetIdFromObject(element.DataContext, out id);
    }

    public static bool TryGetItemIdFromGridItem(object? item, DashboardMotionScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope switch
        {
            DashboardMotionScope.Notes => item is NoteCardViewModel note && TryGetIdFromObject(note, out id),
            DashboardMotionScope.Flashcards => item is FlashcardSetViewModel set && TryGetIdFromObject(set, out id),
            DashboardMotionScope.Quizzes => item is QuizViewModel quiz && TryGetIdFromObject(quiz, out id),
            DashboardMotionScope.MindMaps => item is MindMapViewModel map && TryGetIdFromObject(map, out id),
            _ => false
        };
    }

    private static bool TryGetIdFromObject(object? value, out Guid id)
    {
        id = Guid.Empty;
        switch (value)
        {
            case NoteCardViewModel note:
                id = note.Document.Id;
                return true;
            case FlashcardSetViewModel set:
                id = set.Document.Id;
                return true;
            case QuizViewModel quiz:
                id = quiz.Document.Id;
                return true;
            case MindMapViewModel map:
                id = map.Document.Id;
                return true;
            default:
                return false;
        }
    }

    public static bool MatchesScope(FrameworkElement element, DashboardMotionScope scope)
    {
        if (!TryGetItemId(element, out _))
            return false;

        return scope switch
        {
            DashboardMotionScope.Notes => element is NoteCardControl,
            DashboardMotionScope.Flashcards => GetViewModelType(element) == typeof(FlashcardSetViewModel),
            DashboardMotionScope.Quizzes => GetViewModelType(element) == typeof(QuizViewModel),
            DashboardMotionScope.MindMaps => GetViewModelType(element) == typeof(MindMapViewModel),
            _ => false
        };
    }

    private static Type? GetViewModelType(FrameworkElement element)
    {
        if (TryGetIdFromObject(element.Tag, out _))
            return element.Tag?.GetType();

        if (TryGetIdFromObject(element.DataContext, out _))
            return element.DataContext?.GetType();

        return null;
    }

    public static FrameworkElement? ResolveLayoutRoot(FrameworkElement element, DashboardMotionScope scope)
    {
        if (element is NoteCardControl noteCard && scope == DashboardMotionScope.Notes)
            return noteCard;

        var current = element as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement framework
                && MatchesScope(framework, scope)
                && IsListingLayoutRoot(framework))
                return framework;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public static FrameworkElement ResolveMotionSurface(FrameworkElement layoutRoot, DashboardMotionScope scope)
    {
        if (scope == DashboardMotionScope.Notes)
            return layoutRoot;

        return FindListingMotionSurface(layoutRoot) ?? layoutRoot;
    }

    public static bool TryResolveCard(
        FrameworkElement element,
        DashboardMotionScope scope,
        out FrameworkElement layoutRoot,
        out FrameworkElement motionSurface,
        out Guid itemId)
    {
        layoutRoot = null!;
        motionSurface = null!;
        itemId = Guid.Empty;

        var resolved = ResolveLayoutRoot(element, scope);
        if (resolved is null || !TryGetItemId(resolved, out itemId))
            return false;

        layoutRoot = resolved;
        motionSurface = ResolveMotionSurface(layoutRoot, scope);
        return true;
    }

    private static bool IsListingLayoutRoot(FrameworkElement element)
    {
        if (element is not Border border)
            return false;

        // Must be resolvable from this element's OWN Tag, not an inherited DataContext.
        // Inner content borders (e.g. FlashcardSetCardBorder) don't set Tag themselves but
        // inherit the same DataContext as the true outer wrapper, and can independently satisfy
        // FindListingMotionSurface (e.g. via a nested tag-chip Border) - so the general
        // TryGetItemId (which falls back to DataContext) would wrongly match them too, making
        // the resolver cache the inner border as the "layout root" instead of the real wrapper.
        if (!TryGetIdFromObject(border.Tag, out _))
            return false;

        return FindListingMotionSurface(border) is not null || CardSurfaceMotionBehavior.GetIsEnabled(border);
    }

    private static FrameworkElement? FindListingMotionSurface(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border innerBorder)
                return innerBorder;

            // The nine-slice card shadow (CardShadowLayer) wraps the styled card border in a
            // Grid alongside the shadow layer, so the border is no longer the direct child -
            // look one level deeper rather than treat the wrapper itself as the motion surface.
            if (child is Panel panel)
            {
                var nestedCount = VisualTreeHelper.GetChildrenCount(panel);
                for (var j = 0; j < nestedCount; j++)
                {
                    if (VisualTreeHelper.GetChild(panel, j) is Border nestedBorder)
                        return nestedBorder;
                }
            }
        }

        return null;
    }

    public static (double Width, double MinHeight) GetListingLayoutDefaults(DashboardMotionScope scope, bool grouped = false)
        => scope switch
        {
            DashboardMotionScope.Flashcards => grouped ? (236, 112) : (320, 184),
            DashboardMotionScope.Quizzes => grouped ? (236, 112) : (320, 174),
            DashboardMotionScope.MindMaps => grouped ? (236, 136) : (350, 204),
            _ => grouped ? (236, 112) : (220, 140)
        };

    public static IEnumerable<FrameworkElement> EnumerateLayoutRoots(DependencyObject root, DashboardMotionScope scope)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement framework
                && MatchesScope(framework, scope)
                && (scope == DashboardMotionScope.Notes || IsListingLayoutRoot(framework)))
                yield return framework;

            foreach (var nested in EnumerateLayoutRoots(child, scope))
                yield return nested;
        }
    }
}