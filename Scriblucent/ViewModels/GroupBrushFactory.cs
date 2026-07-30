using System.Windows;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

internal static class GroupBrushFactory
{
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
}
