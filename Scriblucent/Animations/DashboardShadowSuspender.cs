using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Scriblucent.Animations;

/// <summary>
/// Temporarily removes live <see cref="DropShadowEffect"/> instances from a scoped visual
/// subtree (e.g. on window minimize/restore) to avoid WPF's black shadow bitmap flash while
/// the compositor resizes.
/// <para>
/// Dashboard <b>tile</b> rest elevation uses bake-once <c>CardShadowLayer</c> nine-slices and
/// does not need this suspender for scroll performance. Prefer this helper for leftover live
/// effects (chrome, dialogs) and minimize/restore scopes — not as the primary scroll strategy
/// for listing cards.
/// </para>
/// </summary>
internal static class DashboardShadowSuspender
{
    internal static readonly object AnimatedScrollShadowScope = new();

    private const int ResumeBatchSize = 8;

    private static readonly Dictionary<FrameworkElement, (DropShadowEffect Effect, int RefCount)> Active = new();
    private static readonly Dictionary<object, HashSet<FrameworkElement>> ScopeElements = new();
    private static int _staggeredResumeGeneration;

    public static void TrySuspend(object scopeKey, DependencyObject? root)
    {
        if (root is null)
            return;

        if (!ScopeElements.TryGetValue(scopeKey, out var owned))
        {
            owned = new HashSet<FrameworkElement>();
            ScopeElements[scopeKey] = owned;
        }

        SuspendEffects(root, owned);
    }

    public static void BeginScope(object scopeKey)
    {
        // Cancels any staggered resume still in flight; elements not yet restored simply
        // stay suspended and remain owned by the (re-activated) scope.
        _staggeredResumeGeneration++;

        if (!ScopeElements.ContainsKey(scopeKey))
            ScopeElements[scopeKey] = new HashSet<FrameworkElement>();
    }

    public static void TryResume(object scopeKey)
    {
        _staggeredResumeGeneration++;

        if (!ScopeElements.TryGetValue(scopeKey, out var owned))
            return;

        foreach (var element in owned.ToList())
            ReleaseElement(element);

        owned.Clear();
        ScopeElements.Remove(scopeKey);
    }

    /// <summary>
    /// Restores shadows a few elements per dispatcher pass so the end of a scroll burst
    /// does not re-render every card's drop shadow in a single frame.
    /// </summary>
    public static void TryResumeStaggered(object scopeKey)
    {
        if (!ScopeElements.TryGetValue(scopeKey, out var owned) || owned.Count == 0)
        {
            ScopeElements.Remove(scopeKey);
            return;
        }

        var generation = ++_staggeredResumeGeneration;
        ResumeNextBatch(scopeKey, owned, generation);
    }

    private static void ResumeNextBatch(object scopeKey, HashSet<FrameworkElement> owned, int generation)
    {
        if (generation != _staggeredResumeGeneration)
            return;

        FrameworkElement? dispatcherSource = null;
        foreach (var element in owned.Take(ResumeBatchSize).ToList())
        {
            owned.Remove(element);
            ReleaseElement(element);
            dispatcherSource = element;
        }

        if (owned.Count == 0)
        {
            if (ScopeElements.TryGetValue(scopeKey, out var current) && ReferenceEquals(current, owned))
                ScopeElements.Remove(scopeKey);
            return;
        }

        dispatcherSource?.Dispatcher.BeginInvoke(
            () => ResumeNextBatch(scopeKey, owned, generation),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Strips a single shadow host during an active scoped session (e.g. newly virtualized cards).
    /// </summary>
    public static void TrySuspendShadowHost(object scopeKey, FrameworkElement shadowHost)
    {
        if (shadowHost.Effect is not DropShadowEffect effect)
            return;

        if (!ScopeElements.TryGetValue(scopeKey, out var owned))
            return;

        AcquireElement(shadowHost, effect, owned);
    }

    /// <summary>
    /// Takes ownership of an effect a different system (a completing <see cref="UiMotion"/>
    /// composited animation) was about to restore onto <paramref name="element"/>. Without this
    /// hand-off, that restore would stomp an in-progress scroll suspension — the element's
    /// shadow would go live again mid-gesture even though this scope still considers it
    /// suspended. The effect stays remembered here and is reapplied by the normal
    /// resume/staggered-resume path once this scope actually ends.
    /// </summary>
    public static void AdoptSuspendedEffect(object scopeKey, FrameworkElement element, DropShadowEffect effect)
    {
        if (!ScopeElements.TryGetValue(scopeKey, out var owned))
        {
            owned = new HashSet<FrameworkElement>();
            ScopeElements[scopeKey] = owned;
        }

        if (!owned.Add(element))
            return;

        if (Active.TryGetValue(element, out var state))
        {
            Active[element] = (state.Effect, state.RefCount + 1);
            return;
        }

        Active[element] = (effect, 1);
    }

    private static void SuspendEffects(DependencyObject root, HashSet<FrameworkElement> owned)
    {
        if (root is FrameworkElement { Effect: DropShadowEffect effect } element)
            AcquireElement(element, effect, owned);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            SuspendEffects(VisualTreeHelper.GetChild(root, i), owned);
    }

    private static void AcquireElement(FrameworkElement element, DropShadowEffect effect, HashSet<FrameworkElement> owned)
    {
        if (!owned.Add(element))
            return;

        if (Active.TryGetValue(element, out var state))
        {
            Active[element] = (state.Effect, state.RefCount + 1);
            return;
        }

        Active[element] = (effect, 1);
        element.Effect = null;
    }

    private static void ReleaseElement(FrameworkElement element)
    {
        if (!Active.TryGetValue(element, out var state))
            return;

        if (state.RefCount <= 1)
        {
            element.Effect = state.Effect;
            Active.Remove(element);
            return;
        }

        Active[element] = (state.Effect, state.RefCount - 1);
    }
}