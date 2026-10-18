using System.Windows;

namespace Scriblucent.Controls;

/// <summary>Presentation shared by native status badges and their cached rendering.</summary>
public static class StatusChipMetrics
{
    public const double FontSize = 13, Height = 30, HorizontalPadding = 12, VerticalPadding = 6, Gap = 14, Radius = 7;
    public const int FontWeightValue = 700;
    public static FontWeight TextWeight => FontWeights.Bold;
    public static Thickness Padding => new(HorizontalPadding, VerticalPadding, HorizontalPadding, VerticalPadding);
    public static CornerRadius CornerRadius => new(Radius);
}
