using System.Windows;
using System.Windows.Controls;
using SkiaSharp;

namespace Scriblucent.Animations;

/// <summary>A shared, bounded elevation model for every dashboard's dragged card.</summary>
internal static class DashboardDragShadow
{
    internal readonly record struct Profile(double Height, double OffsetY, double CastSigma, double AmbientSigma)
    {
        // Three sigma describes the practical falloff envelope, not a hard shadow edge.
        internal double SideReach => 3 * CastSigma;
        internal double TopReach => Math.Max(3 * AmbientSigma, SideReach - OffsetY);
        internal double BottomReach => SideReach + OffsetY;
    }

    internal static Profile ForFace(double width, double height)
    {
        // 220x140 notes are the reference. Larger faces get at most 10% more elevation;
        // their width changes the silhouette, rather than multiplying shadow distance.
        var size = Math.Clamp(Math.Sqrt(Math.Max(1, Math.Min(width, height)) / 140), .9, 1.1);
        var elevation = 6 * size;
        // Stylized light projection: 35 degrees from vertical, broad 32-degree source.
        // These are design-space DIPs, rather than claims about physical screen dimensions.
        return new(elevation, elevation * Math.Tan(35 * Math.PI / 180),
            elevation * Math.Tan(32 * Math.PI / 180), elevation / 3);
    }

    internal static void Draw(SKCanvas canvas, Rect face, DpiScale dpi, Border? surface)
    {
        var profile = ForFace(face.Width, face.Height);
        var radius = surface?.CornerRadius.TopLeft ?? 14;
        var frame = surface is null ? new Thickness() : SurfaceContour.EffectiveFrame(surface);
        var rx = radius == 0 ? 0 : radius + frame.Left / 2;
        var ry = radius == 0 ? 0 : radius + frame.Top / 2;
        // Bake both silhouettes once into the ghost. Cursor movement reuses that bitmap.
        void Layer(double sigma, double offset, byte alpha)
        {
            using var blur = SKImageFilter.CreateBlur((float)(sigma * dpi.DpiScaleX), (float)(sigma * dpi.DpiScaleY));
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, alpha), ImageFilter = blur };
            canvas.DrawRoundRect(new SKRect((float)(face.Left * dpi.DpiScaleX), (float)((face.Top + offset) * dpi.DpiScaleY),
                (float)(face.Right * dpi.DpiScaleX), (float)((face.Bottom + offset) * dpi.DpiScaleY)),
                (float)(rx * dpi.DpiScaleX), (float)(ry * dpi.DpiScaleY), paint);
        }
        Layer(profile.AmbientSigma, 0, 12);
        Layer(profile.CastSigma, profile.OffsetY, 32);
    }
}
