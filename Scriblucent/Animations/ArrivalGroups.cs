using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

public enum ArrivalRole { None, Heading, Content, Actions }

/// <summary>Small readable groups arrive in one bounded cadence; large editor bodies stay visible.</summary>
public static class ArrivalGroups
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(ArrivalRole), typeof(ArrivalGroups), new PropertyMetadata(ArrivalRole.None));
    public static void SetRole(DependencyObject target, ArrivalRole value) => target.SetValue(RoleProperty, value);
    public static ArrivalRole GetRole(DependencyObject target) => (ArrivalRole)target.GetValue(RoleProperty);
    private static readonly ConditionalWeakTable<FrameworkElement, List<FrameworkElement>> Active = new();
    private static List<FrameworkElement> Resolve(FrameworkElement root)
    {
        var groups = new List<FrameworkElement>(); int content = 0;
        void Visit(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is FrameworkElement element && EffectiveRole(element) is var role && role != ArrivalRole.None)
            {
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
                var visible = ReferenceEquals(root, element) || new Rect(root.RenderSize).IntersectsWith(
                    element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)));
                if (visible && (role != ArrivalRole.Content || content++ < 3)) groups.Add(element);
                return;
            }
            if (node is RichTextBox or ItemsPresenter) return;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root); return groups;
    }
    private static ArrivalRole EffectiveRole(FrameworkElement element) => GetRole(element) is var role && role != ArrivalRole.None
        ? role : WindowMotion.GetRole(element) switch
        { WindowMotionRole.Chrome => ArrivalRole.Heading, WindowMotionRole.Footer => ArrivalRole.Actions, _ => ArrivalRole.None };
    internal static void Start(FrameworkElement root, bool fresh)
    {
        Stop(root, settle: !MotionPolicy.ExpressiveExtras);
        if (!MotionPolicy.ExpressiveExtras) return;
        root.UpdateLayout();
        var groups = Resolve(root); Active.Add(root, groups);
        root.Unloaded -= Unloaded; root.Unloaded += Unloaded;
        foreach (var group in groups)
        {
            var role = EffectiveRole(group);
            var move = UiMotion.ChannelTranslate(group, MotionChannel.Presence);
            if (fresh && MotionPolicy.Spatial) { group.Opacity = .35; move.Y = role == ArrivalRole.Content ? 6 : 4; }
            SurfaceMotion.Arrive(group, 180, translate: move,
                startDelayMs: role == ArrivalRole.Heading ? 24 : role == ArrivalRole.Content ? 60 : 100);
        }
    }
    internal static void Stop(FrameworkElement root, bool settle = false)
    {
        if (!Active.TryGetValue(root, out var groups)) return;
        Active.Remove(root);
        foreach (var group in groups)
        {
            SurfaceMotion.Stop(group, MotionChannel.Presence);
            if (settle) { group.Opacity = 1; UiMotion.ChannelTranslate(group, MotionChannel.Presence).Y = 0; }
        }
        root.Unloaded -= Unloaded;
    }
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((FrameworkElement)sender, true);
}
