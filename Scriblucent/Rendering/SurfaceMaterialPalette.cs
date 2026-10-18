using System.Collections.Concurrent;
using System.Windows.Media;
using Scriblucent.Controls;

namespace Scriblucent.Rendering;

/// <summary>One immutable lighting recipe for live WPF surfaces and rasterized Skia tiles.</summary>
internal static class SurfaceMaterialPalette
{
    internal readonly record struct Colors(Color Top, Color Body, Color Bottom, Color EdgeTop, Color EdgeBottom);
    internal sealed record Paint(Colors Colors, Brush Fill, Brush Edge);
    private readonly record struct Key(Color Base, SurfaceMaterialKind Kind, bool Dark, Color? Foreground);
    private static readonly ConcurrentDictionary<Key, Paint> Cache = new();

    internal static double Contrast(Color first, Color second)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    internal static Color Mix(Color source, Color destination, double amount)
    {
        byte Part(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(source.A, Part(source.R, destination.R), Part(source.G, destination.G), Part(source.B, destination.B));
    }
    private static Color Readable(Color surface, Color? foreground)
    {
        if (foreground is not { A: 255 } text || surface.A != 255 || Contrast(surface, text) >= 4.5) return surface;
        var end = Contrast(ColorsBlack, text) >= Contrast(ColorsWhite, text) ? ColorsBlack : ColorsWhite;
        var low = 0d; var high = 1d;
        for (var i = 0; i < 18; i++)
        {
            var mid = (low + high) / 2;
            if (Contrast(Mix(surface, end, mid), text) >= 4.5) high = mid; else low = mid;
        }
        return Mix(surface, end, high);
    }
    private static readonly Color ColorsWhite = System.Windows.Media.Colors.White;
    private static readonly Color ColorsBlack = System.Windows.Media.Colors.Black;
    internal static Paint Get(Color source, SurfaceMaterialKind kind, bool dark, Color? foreground = null)
    {
        var key = new Key(source, kind, dark, foreground);
        if (Cache.TryGetValue(key, out var paint)) return paint;
        // UI palettes are small. Bound storage even if a caller supplies arbitrary colors.
        if (Cache.Count >= 512) Cache.Clear();
        return Cache.GetOrAdd(key, static key =>
        {
            var strength = key.Kind switch { SurfaceMaterialKind.Filled => 1d, SurfaceMaterialKind.Tinted => .6,
                SurfaceMaterialKind.Marker => .65, SurfaceMaterialKind.Data => .35, SurfaceMaterialKind.Neutral => .45, _ => 0 };
            var metric = key.Kind == SurfaceMaterialKind.Metric;
            var workspace = key.Kind == SurfaceMaterialKind.Workspace;
            // Metric colour communicates state; its neutral foundation and lighting stay consistent.
            var metricBase = metric && key.Foreground is { } tone ? Mix(key.Base, tone, key.Dark ? .10 : .06) : key.Base;
            var body = key.Kind == SurfaceMaterialKind.Raised ? key.Base : Readable(metricBase, key.Foreground);
            var top = Readable(Mix(body, ColorsWhite, workspace ? key.Dark ? .018 : .10 : metric ? key.Dark ? .025 : .02 : (key.Dark ? .12 : .10) * strength), key.Foreground);
            var bottom = metric ? body : Readable(Mix(body, ColorsBlack, workspace ? .012 : (key.Dark ? .16 : .10) * strength), key.Foreground);
            var edgeStrength = key.Kind == SurfaceMaterialKind.Data ? 0 : strength;
            var alpha = key.Base.A / 255d;
            var colors = new Colors(top, body, bottom,
                Color.FromArgb((byte)Math.Round((workspace ? key.Dark ? 26 : 140 : key.Kind is SurfaceMaterialKind.Raised or SurfaceMaterialKind.Metric ? key.Dark ? 16 : 24 : (key.Dark ? 72 : 56) * edgeStrength) * alpha), 255, 255, 255),
                Color.FromArgb((byte)Math.Round((workspace ? key.Dark ? 28 : 18 : key.Kind is SurfaceMaterialKind.Raised or SurfaceMaterialKind.Metric ? 8 : 20 * edgeStrength) * alpha), 0, 0, 0));
            static Brush Gradient(Color top, Color body, Color bottom)
            {
                var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
                brush.GradientStops.Add(new(top, 0)); brush.GradientStops.Add(new(body, .42)); brush.GradientStops.Add(new(bottom, 1));
                brush.Freeze(); return brush;
            }
            Brush fill;
            if (key.Kind == SurfaceMaterialKind.Raised) { fill = new SolidColorBrush(body); fill.Freeze(); }
            else fill = Gradient(top, body, bottom);
            return new(colors, fill, Gradient(colors.EdgeTop, System.Windows.Media.Colors.Transparent, colors.EdgeBottom));
        });
    }
}
