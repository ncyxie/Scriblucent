using System.Windows;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>
/// Builds <see cref="BitmapCache"/> instances that rasterize at the visual's device DPI.
/// A default 1.0-scale cache looks soft/pixelated on high-DPI displays once motion stops.
/// Pass <paramref name="contentScale"/> when the element will be shown under a
/// <see cref="ScaleTransform"/> (e.g. hover pop) so the cache has enough pixels after scale.
/// </summary>
internal static class DpiAwareBitmapCache
{
    public static BitmapCache Create(Visual visual, bool enableClearType = true, double contentScale = 1.0)
    {
        var renderScale = ResolveRenderScale(visual) * Math.Max(1.0, contentScale);
        return new BitmapCache(renderScale)
        {
            EnableClearType = enableClearType,
            SnapsToDevicePixels = true
        };
    }

    public static double ResolveRenderScale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
    }
}
