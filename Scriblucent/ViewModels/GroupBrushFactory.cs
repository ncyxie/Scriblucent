using System.Windows;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

internal static class GroupBrushFactory
{
    private const double LightLuminanceThreshold = 145;

    public static Brush Create(string backgroundColor)
    {
        if (ColorConverter.ConvertFromString(backgroundColor) is Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Return theme-aware default background instead of hardcoded white
        if (Application.Current.Resources.Contains("CardBackground"))
            return (Brush)Application.Current.Resources["CardBackground"];

        return Brushes.White;
    }

    /// <summary>
    /// Black or white text for readable contrast on the group fill (same luminance rule as mind-map nodes).
    /// </summary>
    public static Brush CreateForeground(string backgroundColor)
    {
        if (ColorConverter.ConvertFromString(backgroundColor) is Color color)
        {
            var brush = new SolidColorBrush(IsLightColor(color) ? Colors.Black : Colors.White);
            brush.Freeze();
            return brush;
        }

        if (Application.Current.Resources.Contains("TextColor"))
            return (Brush)Application.Current.Resources["TextColor"];

        return Brushes.Black;
    }

    private static bool IsLightColor(Color color)
    {
        var luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        return luminance >= LightLuminanceThreshold;
    }
}
