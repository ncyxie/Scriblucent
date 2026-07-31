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
    private const double ActiveBackgroundAlpha = 0.07;
    private static readonly Color DropBorderColor = Color.FromRgb(0x5B, 0x7F, 0xED);
    private static readonly Color DropFillColor = Color.FromRgb(74, 110, 224);

    public static readonly Brush DropBorderBrush = CreateFrozenBrush(DropBorderColor);

    public static bool IsAtActiveState(Border glow)
        => glow.Opacity >= ActiveOpacity - 0.05
            && Math.Abs(glow.BorderThickness.Left - ActiveBorderThickness) < 0.05;

    public static void AnimateToActive(Border glow)
        => Animate(glow, ActiveOpacity, ActiveBorderThickness);

    public static void Reset(Border glow)
    {
        UiMotion.Stop(glow);
        glow.Opacity = 0;
        glow.BorderThickness = new Thickness(0);
        glow.Background = Brushes.Transparent;
    }

    public static void Animate(Border glow, double opacity, double thickness)
    {
        if (opacity >= ActiveOpacity - 0.05
            && Math.Abs(thickness - ActiveBorderThickness) < 0.05
            && IsAtActiveState(glow))
        {
            return;
        }

        var fromOpacity = glow.Opacity;
        var fromThickness = glow.BorderThickness;
        var targetThickness = new Thickness(thickness);
        var fromBackgroundAlpha = glow.Background is SolidColorBrush fromBrush
            ? fromBrush.Color.A / 255d
            : 0d;

        // Reuse one mutable brush per glow instead of allocating a new SolidColorBrush every
        // animation frame - only the alpha channel changes, so mutating Color in place is enough.
        if (glow.Background is not SolidColorBrush fillBrush || fillBrush.IsFrozen)
        {
            fillBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(fromBackgroundAlpha * 255), DropFillColor.R, DropFillColor.G, DropFillColor.B));
            glow.Background = fillBrush;
        }

        UiMotion.Run(
            glow,
            new[]
            {
                UiMotion.Track(UiMotion.QuickMs, UiMotion.OutQuad, fromOpacity, opacity, value => glow.Opacity = value),
                UiMotion.Track(
                    UiMotion.QuickMs,
                    UiMotion.OutQuad,
                    fromBackgroundAlpha,
                    opacity > 0 ? ActiveBackgroundAlpha : 0,
                    value => fillBrush.Color = Color.FromArgb(
                        (byte)Math.Round(value * 255),
                        DropFillColor.R,
                        DropFillColor.G,
                        DropFillColor.B)),
                new RenderTransitionClock.Track(
                    UiMotion.QuickMs,
                    UiMotion.OutQuad,
                    eased => glow.BorderThickness = new Thickness(
                        UiMotion.Lerp(fromThickness.Left, targetThickness.Left, eased),
                        UiMotion.Lerp(fromThickness.Top, targetThickness.Top, eased),
                        UiMotion.Lerp(fromThickness.Right, targetThickness.Right, eased),
                        UiMotion.Lerp(fromThickness.Bottom, targetThickness.Bottom, eased)))
            });
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}