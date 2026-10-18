using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>Document content arrives beneath a stationary, connected tab/title/command header.</summary>
internal static class HostedEditorMotion
{
    private static IEnumerable<FrameworkElement> Groups(FrameworkElement root) => root is Grid grid
        ? grid.Children.OfType<FrameworkElement>().Where(element => Grid.GetRow(element) > 0
            && WindowMotion.GetRole(element) != WindowMotionRole.Chrome)
        : [];

    internal static void Prepare(FrameworkElement root, bool fresh)
    {
        foreach (var group in Groups(root).Where(element => element.Visibility == Visibility.Visible))
        {
            SurfaceMotion.Stop(group, MotionChannel.Presence);
            var (_, move) = UiMotion.EnsureScaleTranslateGroup(group);
            move.Y = MotionPolicy.Spatial && fresh ? WindowMotion.GetRole(group) == WindowMotionRole.Footer ? 2 : 4 : 0;
            group.Opacity = MotionPolicy.Quality != MotionQuality.Off ? fresh ? .65 : .82 : 1;
        }
    }

    internal static void Enter(FrameworkElement root, bool fresh)
    {
        foreach (var group in Groups(root).Where(element => element.Visibility == Visibility.Visible))
        {
            var (_, move) = UiMotion.EnsureScaleTranslateGroup(group);
            SurfaceMotion.Arrive(group, fresh ? 200 : 140, translate: move,
                startDelayMs: fresh && WindowMotion.GetRole(group) == WindowMotionRole.Footer ? 24 : 0);
        }
    }

    internal static void Settle(FrameworkElement root)
    {
        foreach (var group in Groups(root))
        {
            SurfaceMotion.Stop(group, MotionChannel.Presence);
            group.Opacity = 1;
            var (_, move) = UiMotion.EnsureScaleTranslateGroup(group);
            move.Y = 0;
        }
    }
}
