using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Scriblucent.Rendering;

/// <summary>
/// Gaussian-blurs a captured WPF bitmap with Skia's CPU backend. Replaces the second
/// full-window RenderTargetBitmap pass (Image + BlurEffect) that overlay backdrops paid on
/// every dialog open - the documented open-latency cost (see OverlayBackdropCapture).
/// </summary>
internal static class SkiaBackdropBlur
{
    private static int _warming;
    private static volatile bool _ready;
    internal static bool IsReady => _ready;
    internal static void BeginWarmup()
    {
        if (System.Threading.Interlocked.Exchange(ref _warming, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, new byte[16 * 16 * 4], 64);
                source.Freeze();
                _ready = Blur(source, 6, new DpiScale(1, 1)) is not null;
            }
            catch { _ready = false; }
        });
    }
    /// <summary>
    /// Blurs <paramref name="source"/> with a Gaussian of <paramref name="radiusDip"/> (WPF
    /// BlurEffect convention: sigma = radius / 3). Returns null when the blur
    /// cannot be produced - callers already treat that as the safe fallback.
    /// </summary>
    public static BitmapSource? Blur(BitmapSource source, double radiusDip, DpiScale dpi)
    {
        try
        {
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            if (width < 1 || height < 1)
                return null;

            using var input = SkiaBitmapUtil.FromSource(source);

            var sigmaX = (float)(radiusDip * dpi.DpiScaleX / 3.0);
            var sigmaY = (float)(radiusDip * dpi.DpiScaleY / 3.0);
            using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(output))
            using (var filter = SKImageFilter.CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { ImageFilter = filter })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(input, 0, 0, SkiaBitmapUtil.LinearSampling, paint);
            }

            return SkiaBitmapUtil.ToSource(output, dpi);
        }
        catch
        {
            return null;
        }
    }
}
