using SkiaSharp;
using Scriblucent.Controls;
using System.Runtime.InteropServices;

namespace Scriblucent.Rendering;

internal static class SurfaceMaterialRaster
{
    internal static bool ResourceDark => System.Windows.Application.Current?.TryFindResource("SurfaceMaterialIsDark") is true;
    private static System.Windows.Media.Color ToWpf(SKColor color) => System.Windows.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);
    private static SKColor ToSkia(System.Windows.Media.Color color) => new(color.R, color.G, color.B, color.A);

    internal static void Draw(SKCanvas canvas, SKRect bounds, float radiusX, float radiusY, SKPaint face,
        bool dark, float scale, SurfaceMaterialKind kind = SurfaceMaterialKind.Tinted, SKColor? foreground = null, float frameInset = 0)
    {
        if (kind == SurfaceMaterialKind.None) { canvas.DrawRoundRect(bounds, radiusX, radiusY, face); return; }
        var colors = SurfaceMaterialPalette.Get(ToWpf(face.Color), kind, dark, foreground.HasValue ? ToWpf(foreground.Value) : null).Colors;
        using var texture = kind == SurfaceMaterialKind.Workspace ? WorkspaceBitmap(colors, bounds.Height) : null;
        using var fill = texture is not null ? texture.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Clamp,
            SKMatrix.Concat(SKMatrix.CreateTranslation(bounds.Left, bounds.Top), SKMatrix.CreateScale(1, bounds.Height / texture.Height)))
            : kind == SurfaceMaterialKind.Raised ? null : SKShader.CreateLinearGradient(new(bounds.MidX, bounds.Top), new(bounds.MidX, bounds.Bottom),
            [ToSkia(colors.Top), ToSkia(colors.Body), ToSkia(colors.Bottom)], [0f, .42f, 1f], SKShaderTileMode.Clamp);
        var previous = face.Shader;
        face.Shader = fill;
        var litHairline = kind == SurfaceMaterialKind.Raised && frameInset is > 0 && frameInset <= 1.5f * scale;
        var fillBounds = bounds;
        if (litHairline) fillBounds.Inflate(-frameInset, -frameInset);
        canvas.DrawRoundRect(fillBounds, Math.Max(0, radiusX - (litHairline ? frameInset / 2 : 0)), Math.Max(0, radiusY - (litHairline ? frameInset / 2 : 0)), face);
        face.Shader = previous;
        if (kind == SurfaceMaterialKind.Data || litHairline) return;
        using var edge = SKShader.CreateLinearGradient(new(bounds.MidX, bounds.Top), new(bounds.MidX, bounds.Bottom),
            [ToSkia(colors.EdgeTop), SKColors.Transparent, ToSkia(colors.EdgeBottom)], [0f, .42f, 1f], SKShaderTileMode.Clamp);
        using var rim = new SKPaint { Shader = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = scale };
        var edgeInset = frameInset + scale / 2;
        var inset = bounds; inset.Inflate(-edgeInset, -edgeInset);
        canvas.DrawRoundRect(inset, Math.Max(0, radiusX - edgeInset), Math.Max(0, radiusY - edgeInset), rim);
    }

    private static SKBitmap WorkspaceBitmap(SurfaceMaterialPalette.Colors colors, float height)
    {
        var tile = WorkspaceGradientTexture.Get(colors, (int)Math.Clamp(Math.Ceiling(height), 1, 8192));
        var bitmap = new SKBitmap(WorkspaceGradientTexture.Width, tile.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(tile.Pixels, 0, bitmap.GetPixels(), tile.Pixels.Length);
        return bitmap;
    }

    // Apply only after the native stroke. Neutral rest hairlines carry the object's light;
    // colored/thicker selection frames keep their own functional paint.
    internal static void LightRaisedFrame(SKCanvas canvas, SKRect bounds, float radius, SKColor frame, bool dark, float scale, float widthDip = 1)
    {
        if (widthDip is <= 0 or > 1.5f || Math.Max(frame.Red, Math.Max(frame.Green, frame.Blue)) - Math.Min(frame.Red, Math.Min(frame.Green, frame.Blue)) > 8) return;
        // Lighting belongs to the face even when the theme's rest border is transparent.
        var light = SurfaceMaterialPalette.Get(System.Windows.Media.Colors.White, SurfaceMaterialKind.Raised, dark).Colors;
        using var shader = SKShader.CreateLinearGradient(new(bounds.MidX, bounds.Top), new(bounds.MidX, bounds.Bottom),
            [ToSkia(light.EdgeTop), SKColors.Transparent, ToSkia(light.EdgeBottom)], [0f, .42f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = widthDip * scale };
        var inset = bounds; inset.Inflate(-paint.StrokeWidth / 2, -paint.StrokeWidth / 2);
        canvas.DrawRoundRect(inset, radius * scale, radius * scale, paint);
    }
}
