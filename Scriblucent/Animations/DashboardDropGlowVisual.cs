using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>
/// Shared drop-target glow used by note cards and dashboard listing cards.
/// </summary>
public static class DashboardDropGlowVisual
{
    public const string ListingGlowTag = "DashboardListingDropGlow";
    public const string ListingGlowOverlayHostTag = "DashboardListingDropGlowOverlayHost";

    public const double ActiveOpacity = 1;
    public const double ActiveBorderThickness = 2.5;
    private static readonly Color DropBorderColor = Color.FromRgb(0x5B, 0x7F, 0xED);

    public static readonly Brush DropBorderBrush = CreateFrozenBrush(DropBorderColor);
    private static readonly DependencyProperty TargetOpacityProperty = DependencyProperty.RegisterAttached(
        "TargetOpacity", typeof(double), typeof(DashboardDropGlowVisual), new PropertyMetadata(0d));

    public static bool IsAtActiveState(Border glow)
        => (double)glow.GetValue(TargetOpacityProperty) == ActiveOpacity
            && Math.Abs(glow.BorderThickness.Left - ActiveBorderThickness) < 0.05;

    public static void AnimateToActive(Border glow)
        => Animate(glow, ActiveOpacity, ActiveBorderThickness);

    public static void Reset(Border glow)
    {
        glow.SetValue(TargetOpacityProperty, 0d);
        UiMotion.Fade(glow, 0, 140, UiMotion.OutSine, () =>
        {
            glow.BorderThickness = new Thickness(0);
            glow.Background = Brushes.Transparent;
        });
    }

    public static void Animate(Border glow, double opacity, double thickness)
    {
        if ((double)glow.GetValue(TargetOpacityProperty) == opacity &&
            (Math.Abs(glow.Opacity - opacity) < .01 || NativeSurfaceMotion.IsRunning(glow, MotionChannel.Presence))) return;
        glow.SetValue(TargetOpacityProperty, opacity);
        glow.BorderThickness = new Thickness(thickness);
        glow.SetResourceReference(Border.BorderBrushProperty, "AccentRimBrush");
        glow.SetResourceReference(Border.BackgroundProperty, "DropTargetWash");
        UiMotion.Fade(glow, opacity, opacity > 0 ? 160 : 140);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
