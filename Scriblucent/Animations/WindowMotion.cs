using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

public enum WindowMotionRole { None, Chrome, Footer }

/// <summary>A visible stage and its real chrome share one interruptible entrance.</summary>
public static class WindowMotion
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(WindowMotionRole), typeof(WindowMotion), new PropertyMetadata(WindowMotionRole.None));
    public static void SetRole(DependencyObject target, WindowMotionRole value) => target.SetValue(RoleProperty, value);
    public static WindowMotionRole GetRole(DependencyObject target) => (WindowMotionRole)target.GetValue(RoleProperty);
    private sealed record Parts(FrameworkElement Stage, FrameworkElement[] Chrome, FrameworkElement[] Footer);
    private static readonly ConditionalWeakTable<FrameworkElement, Parts> Active = new();

    private static Parts Resolve(FrameworkElement root)
    {
        var stage = root;
        if (root is FeatureWindowShell shell)
        {
            shell.ApplyTemplate();
            stage = shell.Template.FindName("MotionStage", shell) as FrameworkElement ?? root;
        }
        var chrome = new List<FrameworkElement>(); var footer = new List<FrameworkElement>();
        void Visit(DependencyObject current)
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return;
            if (current is FrameworkElement element && GetRole(element) is { } role && role != WindowMotionRole.None)
            {
                if (element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
                    (role == WindowMotionRole.Chrome ? chrome : footer).Add(element);
                return;
            }
            if (current is System.Windows.Controls.RichTextBox or System.Windows.Controls.ItemsPresenter
                or System.Windows.Controls.ScrollViewer) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) Visit(VisualTreeHelper.GetChild(current, i));
        }
        Visit(stage);
        return new(stage, chrome.ToArray(), footer.ToArray());
    }

    internal static void Prepare(FrameworkElement root)
    {
        root.SetCurrentValue(UIElement.OpacityProperty, 1d);
        var parts = Resolve(root);
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(parts.Stage);
        scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .985 : 1;
        move.Y = MotionPolicy.Spatial ? 18 : 0;
    }

    internal static void Stop(FrameworkElement root)
    {
        if (!Active.TryGetValue(root, out var parts)) return;
        Active.Remove(root);
        SurfaceMotion.Stop(parts.Stage);
        ArrivalGroups.Stop(parts.Stage, settle: !MotionPolicy.ExpressiveExtras);
        foreach (var element in parts.Chrome.Concat(parts.Footer)) SurfaceMotion.Stop(element, MotionChannel.Presence);
    }

    internal static void Enter(FrameworkElement root, Action? completed)
    {
        Stop(root);
        var parts = Resolve(root); Active.Add(root, parts);
        root.SetCurrentValue(UIElement.OpacityProperty, 1d);
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(parts.Stage);
        if (MotionPolicy.Spatial && move.Y == 0) { move.Y = 18; scale.ScaleX = scale.ScaleY = .985; }
        ArrivalGroups.Start(parts.Stage, fresh: true);
        SurfaceMotion.AnimateTracks(parts.Stage, MotionChannel.Presence,
            [new(move, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.004, 1, MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.004, 1, MotionProfiles.Arrival)], 300, UiMotion.InOutSine,
            () => { Active.Remove(root); completed?.Invoke(); });
    }

    internal static void Exit(FrameworkElement root, Action? completed)
    {
        Stop(root);
        var parts = Resolve(root); Active.Add(root, parts);
        foreach (var element in parts.Chrome.Concat(parts.Footer))
            SurfaceMotion.Animate(element, 0, 120, UiMotion.InQuad, false, cache: true);
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(parts.Stage);
        SurfaceMotion.AnimateTracks(parts.Stage, MotionChannel.Presence,
            [new(move, TranslateTransform.YProperty, 6, true),
             new(scale, ScaleTransform.ScaleXProperty, .99, true),
             new(scale, ScaleTransform.ScaleYProperty, .99, true)], 140, UiMotion.InQuad,
            () => { Active.Remove(root); completed?.Invoke(); });
    }
}
