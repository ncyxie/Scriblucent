using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Paint feedback for dense rows, with no travel and no replay when a container is recycled.</summary>
public static class StateSurfaceMotionBehavior
{
    public static readonly DependencyProperty ActiveStateProperty = DependencyProperty.RegisterAttached(
        "ActiveState", typeof(bool?), typeof(StateSurfaceMotionBehavior), new PropertyMetadata(null, Selected));
    public static void SetActiveState(DependencyObject target, bool? value) => target.SetValue(ActiveStateProperty, value);
    public static bool? GetActiveState(DependencyObject target) => (bool?)target.GetValue(ActiveStateProperty);
    private static readonly HashSet<Border> LoadedSurfaces = new();
    static StateSurfaceMotionBehavior()
    {
        MotionPolicy.Changed += RefreshPolicy;
        MotionPolicy.DecorationBudgetChanged += RefreshPolicy;
    }
    private static void RefreshPolicy()
    { foreach (var border in LoadedSurfaces.ToArray()) Update(border, false); }
    internal static void RefreshPaint(Border border) => Update(border, false);
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StateSurfaceMotionBehavior), new PropertyMetadata(false, Enabled));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(StateSurfaceMotionBehavior), new PropertyMetadata(false, Selected));
    public static void SetIsSelected(DependencyObject target, bool value) => target.SetValue(IsSelectedProperty, value);
    public static bool GetIsSelected(DependencyObject target) => (bool)target.GetValue(IsSelectedProperty);
    public static readonly DependencyProperty SelectionThicknessProperty = DependencyProperty.RegisterAttached(
        "SelectionThickness", typeof(double), typeof(StateSurfaceMotionBehavior), new PropertyMetadata(1d, Selected));
    public static void SetSelectionThickness(DependencyObject target, double value) => target.SetValue(SelectionThicknessProperty, value);
    public static double GetSelectionThickness(DependencyObject target) => (double)target.GetValue(SelectionThicknessProperty);
    private static void Enabled(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Border border) return;
        border.Loaded -= Loaded; border.Unloaded -= Unloaded; border.MouseEnter -= Hover; border.MouseLeave -= Hover;
        border.DataContextChanged -= Rebound; border.IsKeyboardFocusWithinChanged -= Focus;
        border.IsEnabledChanged -= Focus;
        if (e.NewValue is true)
        {
            border.Loaded += Loaded; border.Unloaded += Unloaded; border.MouseEnter += Hover; border.MouseLeave += Hover;
            border.DataContextChanged += Rebound; border.IsKeyboardFocusWithinChanged += Focus;
            border.IsEnabledChanged += Focus;
        }
        else
        {
            LoadedSurfaces.Remove(border);
            if (border is not Scriblucent.Controls.InteractionSurface) CommandLightBehavior.ReleaseControlLight(border);
            Clear(border);
        }
        if (border.IsLoaded && e.NewValue is true) Loaded(border, new RoutedEventArgs());
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        if (border is not Scriblucent.Controls.InteractionSurface) CommandLightBehavior.EnsureControlLight(border, CommandLightRole.Item);
        LoadedSurfaces.Add(border); Update(border, false);
    }
    private static void Unloaded(object sender, RoutedEventArgs e)
    { LoadedSurfaces.Remove((Border)sender); Clear((Border)sender); }
    private static void Rebound(object sender, DependencyPropertyChangedEventArgs e)
    {
        var border = (Border)sender; Update(border, false);
        var identity = e.NewValue;
        border.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
        { if (border.IsLoaded && ReferenceEquals(border.DataContext, identity)) Update(border, false); });
    }
    private static void Focus(object sender, DependencyPropertyChangedEventArgs e) => Update((Border)sender, true);
    private static void Hover(object sender, MouseEventArgs e) => Update((Border)sender, true, e.RoutedEvent == Mouse.MouseEnterEvent);
    private static void Selected(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { if (target is Border { IsLoaded: true } border && GetIsEnabled(border)) Update(border, true); }
    private static void Update(Border border, bool animate, bool? hovered = null)
    {
        var selected = GetIsSelected(border);
        SurfaceFill.Select(border, selected, animate, "SelectionBackground");
        AccentMotion.Set(border, selected, border.TryFindResource("AccentRimBrush") as System.Windows.Media.Brush,
            border.CornerRadius.TopLeft, GetSelectionThickness(border), animate);
        // Button surfaces already own their hover/press clocks. Add only selection
        // here so the two controllers never compete for the interaction channel.
        if (border is Scriblucent.Controls.InteractionSurface) return;
        var active = border.IsEnabled && (GetActiveState(border) ?? ((hovered ?? border.IsMouseOver) || border.IsKeyboardFocusWithin));
        var tracks = new List<NativeMotionTrack>();
        if (SurfaceFill.Get(border, SurfaceFill.Role.Hover, active) is { } fill)
        { fill.SetResourceReference(SurfaceFill.Layer.BrushProperty, MotionPolicy.Spatial ? "ControlHoverSurfaceWash" : "HoverBackground"); tracks.Add(new(fill, UIElement.OpacityProperty, active ? .6 : 0)); }
        if (CommandLightBehavior.LightFor(border) is not null) SurfaceLight.Reset(border);
        else SurfaceLight.AddTracks(border, tracks, active, neutral: true);
        if (animate) SurfaceMotion.AnimateTracks(border, MotionChannel.Interaction, tracks, active ? 140 : 160, UiMotion.OutSine);
        else { SurfaceMotion.Stop(border, MotionChannel.Interaction); foreach (var track in tracks) track.Target.SetCurrentValue(track.Property, track.To); }
    }
    private static void Clear(Border border)
    {
        SurfaceFill.ClearSelection(border); AccentMotion.Clear(border);
        if (border is Scriblucent.Controls.InteractionSurface) return;
        SurfaceMotion.Stop(border, MotionChannel.Interaction); SurfaceLight.Reset(border);
        if (SurfaceFill.Get(border, SurfaceFill.Role.Hover, false) is { } fill) fill.Opacity = 0;
    }
}
