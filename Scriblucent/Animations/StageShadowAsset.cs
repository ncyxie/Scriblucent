using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// One shared, pre-rendered halo for the dashboard and feature stages. The plate never moves,
/// so a live <see cref="DropShadowEffect"/> would re-blur on every scroll repaint. The blur is
/// baked once into a small bitmap and stretched as a nine-slice, matching
/// <see cref="CardShadowAsset"/> but even (no downward cast) and sized for the 18px stage corner.
/// </summary>
internal static class StageShadowAsset
{
    public const double CornerTileSize = 40;
    public const double Bleed = 20;

    private const double ShapeCornerRadius = 18;
    private const double BlurRadius = 20;
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
            var inset = Bleed;
            var size = SwatchSize - inset * 2;
            dc.DrawRoundedRectangle(
                Brushes.Black,
                null,
                new Rect(inset, inset, size, size),
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
