using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace NoteCards.Animations;

/// <summary>
/// One shared, pre-rendered soft-shadow texture (baked once, frozen) reused as a nine-slice
/// across every card. A live <see cref="DropShadowEffect"/> forces a real GPU blur pass on every
/// dirty repaint of the element it is attached to; with many cards on screen during a scroll,
/// that per-element blur cost is what made scrolling choppy (see NoteCardControl.xaml history).
/// Baking the blur once into a small shared bitmap and slicing/stretching it costs nothing per
/// card beyond a handful of cheap image draws - the same technique large UI toolkits (Material,
/// Fluent, etc.) use for elevation shadows at scale.
/// </summary>
internal static class CardShadowAsset
{
    public const double CornerTileSize = 22;

    private const double ShapeCornerRadius = 12;
    private const double BlurRadius = 9;
    private const double EdgeStripThickness = 4;
    private const double SwatchSize = CornerTileSize * 2 + 24;
    private const double RenderScale = 3.0;

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
            var inset = BlurRadius + 3;
            var rect = new Rect(inset, inset, SwatchSize - inset * 2, SwatchSize - inset * 2);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                null,
                rect,
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
