using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Scriblucent.Rendering;

/// <summary>
/// Shared WPF-bitmap &lt;-&gt; SKBitmap round-trip for the off-thread Skia raster helpers.
/// Normalizes any source format to premultiplied BGRA (Pbgra32 byte layout), which is what
/// the Skia CPU pipeline consumes and what WPF shows back.
/// </summary>
internal static class SkiaBitmapUtil
{
    public static SKBitmap FromSource(BitmapSource source)
    {
        var normalized = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        var width = normalized.PixelWidth;
        var height = normalized.PixelHeight;
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        normalized.CopyPixels(
            new Int32Rect(0, 0, width, height),
            bitmap.GetPixels(),
            width * height * 4,
            width * 4);
        return bitmap;
    }

    public static BitmapSource ToSource(SKBitmap bitmap, DpiScale dpi)
    {
        var result = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32,
            null,
            bitmap.GetPixels(),
            bitmap.Width * bitmap.Height * 4,
            bitmap.RowBytes);
        result.Freeze();
        return result;
    }

    public static DpiScale DefaultDpi => new(96, 96);
}
