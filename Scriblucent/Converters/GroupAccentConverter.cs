using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Scriblucent.Converters;

/// <summary>Saved pastel colours identify a group; the theme owns its neutral body and readable text.</summary>
public sealed class GroupAccentConverter : IMultiValueConverter
{
    private static readonly ConcurrentDictionary<(Color Source, Color Primary, bool Dark, bool Wash), Brush> Cache = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter as string == "Selected")
            return values.ElementAtOrDefault(0) is SolidColorBrush selected && selected.Color == ReadColour(values.ElementAtOrDefault(1));
        var primary = (values.ElementAtOrDefault(2) as SolidColorBrush ?? Application.Current?.TryFindResource("AccentPrimary") as SolidColorBrush)?.Color
            ?? Color.FromRgb(67, 92, 236);
        var source = ReadColour(values.ElementAtOrDefault(0));
        // Menus supply their live foreground; headers supply their live background.
        // Both update on theme changes without reaching across a popup's visual tree.
        var dark = values.ElementAtOrDefault(1) is SolidColorBrush surface
            ? (.2126 * surface.Color.R + .7152 * surface.Color.G + .0722 * surface.Color.B < 128) != (parameter as string == "Swatch")
            : values.ElementAtOrDefault(1) is bool mode ? mode : Application.Current?.TryFindResource("SurfaceMaterialIsDark") is true;
        var wash = parameter as string == "Wash";
        if (Cache.Count >= 256) Cache.Clear();
        return Cache.GetOrAdd((source, primary, dark, wash), static key =>
        {
            var tone = Accent(key.Source, key.Primary, key.Dark);
            Brush result;
            if (key.Wash)
            {
                var gradient = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
                gradient.GradientStops.Add(new(Color.FromArgb(key.Dark ? (byte)40 : (byte)28, tone.R, tone.G, tone.B), 0));
                gradient.GradientStops.Add(new(Color.FromArgb(key.Dark ? (byte)18 : (byte)12, tone.R, tone.G, tone.B), .45));
                gradient.GradientStops.Add(new(Color.FromArgb(0, tone.R, tone.G, tone.B), 1));
                result = gradient;
            }
            else result = new SolidColorBrush(tone);
            result.Freeze();
            return result;
        });
    }

    private static Color ReadColour(object? value)
    {
        if (value is SolidColorBrush brush) return brush.Color;
        if (value is string hex)
        {
            try { if (ColorConverter.ConvertFromString(hex) is Color colour) return colour; }
            catch (FormatException) { }
            catch (NotSupportedException) { }
        }
        return Color.FromRgb(248, 250, 255);
    }

    private static Color Accent(Color source, Color primary, bool dark)
    {
        // Preserve the two established blue choices as the app's actual brand blue.
        if (source == Color.FromRgb(248, 250, 255) || source == Color.FromRgb(238, 240, 254)) return primary;
        if (source == Color.FromRgb(248, 250, 252)) return dark ? Color.FromRgb(148, 163, 184) : Color.FromRgb(100, 116, 139);
        var r = source.R / 255d; var g = source.G / 255d; var b = source.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        if (delta < .015) return dark ? Color.FromRgb(148, 163, 184) : Color.FromRgb(100, 116, 139);
        var lightness = (max + min) / 2;
        var saturation = Math.Clamp(delta / Math.Max(.001, 1 - Math.Abs(2 * lightness - 1)), .55, .78);
        var hue = max == r ? ((g - b) / delta + 6) % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        var l = dark ? .68 : .44;
        var chroma = (1 - Math.Abs(2 * l - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1)); var m = l - chroma / 2;
        var (rr, gg, bb) = hue switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((rr + m) * 255), (byte)Math.Round((gg + m) * 255), (byte)Math.Round((bb + m) * 255));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
