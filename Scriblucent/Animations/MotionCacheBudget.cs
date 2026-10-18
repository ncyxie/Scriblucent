using System.Windows;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>Account only temporary motion textures; no fullscreen or unbounded texture allocation.</summary>
internal static class MotionCacheBudget
{
    internal const double PerSurfacePixels = 2_000_000, TotalPixels = 8_000_000;
    private static double _reserved;
    private static readonly object Gate = new();
    internal static double ReservedPixels { get { lock (Gate) return _reserved; } }
    internal static double TryReserve(FrameworkElement element)
    {
        if (!MotionPolicy.Spatial || element.CacheMode is not null) return 0;
        var dpi = VisualTreeHelper.GetDpi(element);
        var pixels = Math.Ceiling(element.ActualWidth * dpi.DpiScaleX) * Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        return TryReservePixels(pixels);
    }
    internal static double TryReservePixels(double pixels, bool requireSpatial = true)
    {
        lock (Gate)
        {
            if ((requireSpatial && !MotionPolicy.Spatial) || !double.IsFinite(pixels) || pixels <= 0 || pixels > PerSurfacePixels || _reserved + pixels > TotalPixels) return 0;
            _reserved += pixels;
            return pixels;
        }
    }
    internal static void Release(double pixels) { lock (Gate) _reserved = Math.Max(0, _reserved - pixels); }
}
