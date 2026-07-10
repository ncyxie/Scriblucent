using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Animations;

internal static class OverlayBackgroundSuspender
{
    private const int OverlayZIndexThreshold = 200;

    private static readonly List<(FrameworkElement Element, Visibility Visibility)> Suspended = new();
    private static int _depth;

    public static void TrySuspend(FrameworkElement overlayHost)
    {
        if (overlayHost.Parent is not Panel parent)
            return;

        if (_depth == 0)
        {
            Suspended.Clear();
            foreach (UIElement child in parent.Children)
            {
                if (child is not FrameworkElement element)
                    continue;
                if (ShouldLeaveVisible(element, overlayHost))
                    continue;
                if (element.Visibility != Visibility.Visible)
                    continue;

                Suspended.Add((element, element.Visibility));
                element.Visibility = Visibility.Hidden;
            }
        }

        _depth++;
    }

    public static void TryResume()
    {
        if (_depth == 0)
            return;

        _depth--;
        if (_depth > 0)
            return;

        foreach (var (element, visibility) in Suspended)
            element.Visibility = visibility;

        Suspended.Clear();
    }

    private static bool ShouldLeaveVisible(FrameworkElement element, FrameworkElement overlayHost)
    {
        if (ReferenceEquals(element, overlayHost))
            return true;

        return Panel.GetZIndex(element) >= OverlayZIndexThreshold;
    }
}
