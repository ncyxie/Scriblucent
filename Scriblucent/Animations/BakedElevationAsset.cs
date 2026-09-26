using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// Pre-rendered drop shadows for surfaces that stay on screen (fields, buttons, dialogs).
/// A live <see cref="DropShadowEffect"/> re-blurs whenever a sibling scrolls. These are baked
/// once and stretched, with the same corner, blur, depth, and opacity as the old tokens.
/// </summary>
internal static class BakedElevationAsset
{
    private const double EdgeStripThickness = 6;
    private const double RenderScale = 2.0;

    internal readonly record struct Profile(
        double CornerRadius,
        double BlurRadius,
        double ShadowDepth,
        double Opacity,
        double CornerTileSize)
    {
        public double BleedTop => BlurRadius;
        public double BleedBottom => BlurRadius + ShadowDepth;
        public double SwatchSize => CornerTileSize * 2 + 48;
    }

    // Radius.Control (10) + Elevation.Field.
    public static Profile Field { get; } = new(10, 14, 2, 0.18, 30);

    // Radius.Control (10) + Elevation.Button.
    public static Profile Button { get; } = new(10, 12, 1, 0.18, 27);

    // Radius.Card (14) + Elevation.Button, used by the round floating action button.
    public static Profile ButtonCard { get; } = new(14, 12, 1, 0.18, 31);

    // Radius.Overlay (18) + Elevation.Dialog.
    public static Profile Dialog { get; } = new(18, 36, 8, 0.30, 66);

    private static readonly Dictionary<Profile, Lazy<BitmapSource>> Swatches = new();

    public static ImageSource Slice(Profile profile, SlicePart part)
    {
        var swatch = SwatchFor(profile);
        var (x, y, width, height) = part switch
        {
            SlicePart.TopLeft => (0, 0, profile.CornerTileSize, profile.CornerTileSize),
            SlicePart.TopRight => (profile.SwatchSize - profile.CornerTileSize, 0, profile.CornerTileSize, profile.CornerTileSize),
            SlicePart.BottomLeft => (0, profile.SwatchSize - profile.CornerTileSize, profile.CornerTileSize, profile.CornerTileSize),
            SlicePart.BottomRight => (profile.SwatchSize - profile.CornerTileSize, profile.SwatchSize - profile.CornerTileSize, profile.CornerTileSize, profile.CornerTileSize),
            SlicePart.TopEdge => (profile.CornerTileSize, 0, EdgeStripThickness, profile.CornerTileSize),
            SlicePart.BottomEdge => (profile.CornerTileSize, profile.SwatchSize - profile.CornerTileSize, EdgeStripThickness, profile.CornerTileSize),
            SlicePart.LeftEdge => (0, profile.CornerTileSize, profile.CornerTileSize, EdgeStripThickness),
            _ => (profile.SwatchSize - profile.CornerTileSize, profile.CornerTileSize, profile.CornerTileSize, EdgeStripThickness)
        };

        var pixelsPerDip = swatch.PixelWidth / profile.SwatchSize;
        var rect = new Int32Rect(
            (int)Math.Round(x * pixelsPerDip),
            (int)Math.Round(y * pixelsPerDip),
            Math.Max(1, (int)Math.Round(width * pixelsPerDip)),
            Math.Max(1, (int)Math.Round(height * pixelsPerDip)));
        var cropped = new CroppedBitmap(swatch, rect);
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource SwatchFor(Profile profile)
    {
        if (!Swatches.TryGetValue(profile, out var lazy))
        {
            lazy = new Lazy<BitmapSource>(() => Bake(profile));
            Swatches[profile] = lazy;
        }

        return lazy.Value;
    }

    private static BitmapSource Bake(Profile profile)
    {
        var visual = new DrawingVisual
        {
            Effect = new BlurEffect { Radius = profile.BlurRadius, KernelType = KernelType.Gaussian }
        };

        using (var dc = visual.RenderOpen())
        {
            var inset = profile.BlurRadius;
            var size = profile.SwatchSize - inset * 2 - profile.ShadowDepth;
            dc.DrawRoundedRectangle(
                Brushes.Black,
                null,
                new Rect(inset, inset + profile.ShadowDepth, size, size),
                profile.CornerRadius,
                profile.CornerRadius);
        }

        var pixelSize = (int)Math.Round(profile.SwatchSize * RenderScale);
        var dpi = 96.0 * RenderScale;
        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal enum SlicePart
    {
        TopLeft,
        TopEdge,
        TopRight,
        LeftEdge,
        RightEdge,
        BottomLeft,
        BottomEdge,
        BottomRight
    }
}
