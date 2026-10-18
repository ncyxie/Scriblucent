using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Animations;

/// <summary>Suspension is scoped to its window; nested overlays cannot resume another window's content.</summary>
internal static class OverlayBackgroundSuspender
{
    private sealed class State
    {
        internal int Depth;
        internal readonly List<(FrameworkElement Element, Visibility Visibility)> Elements = new();
    }
    private static readonly ConditionalWeakTable<Window, State> States = new();
    internal static bool IsSuspended(Window? owner) => owner is not null && States.TryGetValue(owner, out var state) && state.Depth > 0;
    public static void TrySuspend(FrameworkElement overlayHost)
    {
        if (overlayHost.Parent is not Panel parent || Window.GetWindow(overlayHost) is not { } owner) return;
        var state = States.GetValue(owner, _ => new());
        if (state.Depth == 0)
        {
            state.Elements.Clear();
            foreach (UIElement child in parent.Children)
            {
                if (child is not FrameworkElement element || ReferenceEquals(element, overlayHost)
                    || Panel.GetZIndex(element) >= 200 || element.Visibility != Visibility.Visible) continue;
                state.Elements.Add((element, element.Visibility));
                element.Visibility = Visibility.Hidden;
            }
        }
        state.Depth++;
    }
    internal static T WithPageVisible<T>(Window? owner, Func<T> action)
    {
        if (owner is null || !States.TryGetValue(owner, out var state) || state.Depth == 0) return action();
        foreach (var (element, visibility) in state.Elements) element.Visibility = visibility;
        try { return action(); }
        finally { foreach (var (element, _) in state.Elements) element.Visibility = Visibility.Hidden; }
    }
    public static void TryResume(FrameworkElement overlayHost)
    {
        if (Window.GetWindow(overlayHost) is not { } owner || !States.TryGetValue(owner, out var state) || state.Depth == 0) return;
        if (--state.Depth > 0) return;
        foreach (var (element, visibility) in state.Elements) element.Visibility = visibility;
        state.Elements.Clear();
        OverlayBackdropCapture.Invalidate(owner);
    }
}
