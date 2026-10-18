using SkiaSharp;

namespace Scriblucent.Rendering;

internal static class DashboardSelectionPaint
{
    internal static System.Windows.Media.Color ResolveColor(string key, System.Windows.Media.Color fallback)
    {
        var value = System.Windows.Application.Current?.TryFindResource(key);
        return value switch
        {
            System.Windows.Media.SolidColorBrush solid => solid.Color,
            System.Windows.Media.LinearGradientBrush gradient when gradient.GradientStops.Count > 0 =>
                gradient.GradientStops.OrderBy(s => Math.Abs(s.Offset - .42)).First().Color,
            _ => fallback
        };
    }
    internal static SKShader Shader(SKRect bounds, SKColor top, SKColor body, SKColor bottom)
        => SKShader.CreateLinearGradient(new(bounds.MidX, bounds.Top), new(bounds.MidX, bounds.Bottom),
            [top.Alpha == 0 ? body : top, body, bottom.Alpha == 0 ? body : bottom], [0f, .42f, 1f], SKShaderTileMode.Clamp);
}
