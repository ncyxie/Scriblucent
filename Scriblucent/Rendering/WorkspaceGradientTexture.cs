using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Rendering;

/// <summary>Sub-code-value workspace lighting without full-width 8-bit colour bands.</summary>
internal static class WorkspaceGradientTexture
{
    internal const int Width = 64;
    private const int MaxHeight = 8192;
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly record struct Key(SurfaceMaterialPalette.Colors Colors, int Height);
    internal sealed record Tile(int Height, byte[] Pixels, BitmapSource Image);
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Tile> Cache = new();
    private static readonly Queue<Key> Order = new();
    private static int _bytes;
    private static readonly byte[] Ranks = CreateRanks();

    // Each row contains every threshold exactly once, in a stable shuffled order.
    // This avoids a checkerboard, drifting grain, and random row-average brightness.
    private static byte[] CreateRanks()
    {
        var ranks = new byte[Width * Width];
        for (var y = 0; y < Width; y++)
        {
            for (var x = 0; x < Width; x++) ranks[y * Width + x] = (byte)x;
            var seed = (uint)(y + 1) * 747796405u;
            for (var x = Width - 1; x > 0; x--)
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                var other = (int)(seed % (uint)(x + 1));
                (ranks[y * Width + x], ranks[y * Width + other]) = (ranks[y * Width + other], ranks[y * Width + x]);
            }
        }
        return ranks;
    }

    internal static Tile Get(SurfaceMaterialPalette.Colors colors, int height)
    {
        height = Math.Clamp(height, 1, MaxHeight);
        var key = new Key(colors, height);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var found)) return found;
            var pixels = new byte[Width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var position = (y + .5) / height;
                var from = position < .42 ? colors.Top : colors.Body;
                var to = position < .42 ? colors.Body : colors.Bottom;
                var amount = position < .42 ? position / .42 : (position - .42) / .58;
                var alpha = from.A + (to.A - from.A) * amount;
                var red = (from.R + (to.R - from.R) * amount) * alpha / 255;
                var green = (from.G + (to.G - from.G) * amount) * alpha / 255;
                var blue = (from.B + (to.B - from.B) * amount) * alpha / 255;
                for (var x = 0; x < Width; x++)
                {
                    var threshold = (Ranks[(y & (Width - 1)) * Width + x] + .5) / Width;
                    static byte Quantize(double value, double threshold) => (byte)Math.Clamp(Math.Floor(value + 1 - threshold), 0, 255);
                    var offset = (y * Width + x) * 4;
                    pixels[offset] = Quantize(blue, threshold); pixels[offset + 1] = Quantize(green, threshold);
                    pixels[offset + 2] = Quantize(red, threshold); pixels[offset + 3] = (byte)Math.Round(alpha);
                }
            }
            var image = BitmapSource.Create(Width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, Width * 4);
            image.Freeze();
            var tile = new Tile(height, pixels, image);
            // Account for managed bytes and the immutable bitmap copy; width never follows window width.
            var bytes = pixels.Length * 2;
            while (Order.Count > 0 && (Cache.Count >= 16 || _bytes + bytes > MaxBytes))
            { var oldest = Order.Dequeue(); _bytes -= Cache[oldest].Pixels.Length * 2; Cache.Remove(oldest); }
            Cache.Add(key, tile); Order.Enqueue(key); _bytes += bytes;
            return tile;
        }
    }

    internal sealed class Fill
    {
        private (SurfaceMaterialPalette.Colors Colors, double Height, double DpiX, double DpiY)? _key;
        private Brush? _brush;
        internal Brush Get(SurfaceMaterialPalette.Colors colors, double height, DpiScale dpi)
        {
            height = Math.Max(1, height);
            var key = (colors, height, dpi.DpiScaleX, dpi.DpiScaleY);
            if (_key == key) return _brush!;
            var tile = WorkspaceGradientTexture.Get(colors, (int)Math.Clamp(Math.Ceiling(height * dpi.DpiScaleY), 1, MaxHeight));
            var brush = new ImageBrush(tile.Image)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute,
                // One physical pixel per threshold. Repeat only across the width of the surface.
                Viewport = new Rect(0, 0, Width / dpi.DpiScaleX, height), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            };
            brush.Freeze(); _key = key; return _brush = brush;
        }
    }
}
