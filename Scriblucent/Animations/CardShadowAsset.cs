using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// One shared, pre-rendered soft-shadow texture (baked once, frozen) reused as a nine-slice
/// across every card. A live <see cref="DropShadowEffect"/> forces a real GPU blur pass on
/// every dirty repaint; with many cards on screen during a scroll, that per-element blur cost
/// is what made scrolling choppy. Baking the blur once into a small shared bitmap and
/// slicing/stretching it costs nothing per card beyond a handful of cheap image draws. The bake
/// composites a soft centered ambient contact with a downward key cast so tops stay readable on
/// light shells without a live effect.
/// </summary>
internal static class CardShadowAsset
{
    public const double CornerTileSize = 28;

    private const double ShapeCornerRadius = 14;
    private const double BlurRadius = 14;
    private const double EdgeStripThickness = 6;
    private const double SwatchSize = CornerTileSize * 2 + 32;
    private const double RenderScale = 2.0;

    private static readonly Lazy<BitmapSource> Swatch = new(Bake);

    public static ImageSource TopLeft { get; } = CreateSlice(0, 0, CornerTileSize, CornerTileSize);
    public static ImageSource TopRight { get; } = CreateSlice(SwatchSize - CornerTileSize, 0, CornerTileSize, CornerTileSize);
    public static ImageSource BottomLeft { get; } = CreateSlice(0, SwatchSize - CornerTileSize, CornerTileSize, CornerTileSize);
    public static ImageSource BottomRight { get; } = CreateSlice(SwatchSize - CornerTileSize, SwatchSize - CornerTileSize, CornerTileSize, CornerTileSize);
    public static ImageSource TopEdge { get; } = CreateSlice(CornerTileSize, 0, EdgeStripThickness, CornerTileSize);
    public static ImageSource BottomEdge { get; } = CreateSlice(CornerTileSize, SwatchSize - CornerTileSize, EdgeStripThickness, CornerTileSize);
    public static ImageSource LeftEdge { get; } = CreateSlice(0, CornerTileSize, CornerTileSize, EdgeStripThickness);
    public static ImageSource RightEdge { get; } = CreateSlice(SwatchSize - CornerTileSize, CornerTileSize, CornerTileSize, EdgeStripThickness);

    private static ImageSource CreateSlice(double x, double y, double width, double height)
    {
        var bitmap = Swatch.Value;
        var pixelsPerDip = bitmap.PixelWidth / SwatchSize;
        var rect = new Int32Rect(
            (int)Math.Round(x * pixelsPerDip),
            (int)Math.Round(y * pixelsPerDip),
            Math.Max(1, (int)Math.Round(width * pixelsPerDip)),
            Math.Max(1, (int)Math.Round(height * pixelsPerDip)));

        var cropped = new CroppedBitmap(bitmap, rect);
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource Bake()
    {
        var visual = new DrawingVisual
        {
            Effect = new BlurEffect { Radius = BlurRadius, KernelType = KernelType.Gaussian }
        };

        using (var dc = visual.RenderOpen())
        {
            // Ambient (centered) + key (offset down) in one blur pass: top contact stays
            // readable on white section shells while the underside reads as a cast.
            const double castOffsetY = 3;
            var inset = BlurRadius + 3;
            var size = SwatchSize - inset * 2;
            var ambient = new Rect(inset, inset, size, size);
            var key = new Rect(inset, inset + castOffsetY, size, size);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                null,
                ambient,
                ShapeCornerRadius,
                ShapeCornerRadius);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                null,
                key,
                ShapeCornerRadius,
                ShapeCornerRadius);
        }

        var pixelSize = (int)Math.Round(SwatchSize * RenderScale);
        var dpi = 96.0 * RenderScale;
        var rtb = new RenderTargetBitmap(pixelSize, pixelSize, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
