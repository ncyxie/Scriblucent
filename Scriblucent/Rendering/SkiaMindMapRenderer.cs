using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Scriblucent.Rendering;

/// <summary>
/// Rasters the whole mind-map canvas (see <see cref="MindMapTileSnapshot"/>) off the UI thread
/// into ONE snapshot bitmap at the given zoom. During zoom sessions the snapshot scales with
/// the same LayoutTransform as the live canvas, so the gesture stays continuous (no per-level
/// tile swaps); a fresh snapshot renders at settle and the live canvas returns.
/// </summary>
internal static class SkiaMindMapRenderer
{
    /// <summary>Long-side cap keeps the transient snapshot bitmap bounded on big maps.</summary>
    public const float MaxSnapshotDimensionPx = 3072f;

    private static readonly Dictionary<string, SKPath> IconPathCache = new();
    private static readonly object IconPathGate = new();

    public static SKBitmap RenderFull(MindMapTileSnapshot snapshot, float zoom, float dpi, out float renderScale)
    {
        var scale = zoom * dpi;
        var widthDip = (float)snapshot.CanvasWidthDip;
        var heightDip = (float)snapshot.CanvasHeightDip;
        var longSidePx = Math.Max(widthDip, heightDip) * scale;
        renderScale = longSidePx > MaxSnapshotDimensionPx ? scale * MaxSnapshotDimensionPx / longSidePx : scale;

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(widthDip * renderScale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(heightDip * renderScale));
        var bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        foreach (var edge in snapshot.Edges)
        {
            using var path = new SKPathBuilder();
            path.MoveTo((float)edge.SX * renderScale, (float)edge.SY * renderScale);
            path.CubicTo(
                (float)edge.C1X * renderScale, (float)edge.C1Y * renderScale,
                (float)edge.C2X * renderScale, (float)edge.C2Y * renderScale,
                (float)edge.EX * renderScale, (float)edge.EY * renderScale);
            using var paint = new SKPaint
            {
                Color = FromArgb(edge.Color).WithAlpha((byte)(0.78 * 255)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f * renderScale,
                StrokeCap = SKStrokeCap.Round,
            };
            using var edgePath = path.Detach();
            canvas.DrawPath(edgePath, paint);
        }

        foreach (var node in snapshot.Nodes)
        {
            DrawNode(canvas, node, renderScale);
        }

        return bitmap;
    }

    private static void DrawNode(SKCanvas canvas, MindMapTileNode node, float scale)
    {
        canvas.Save();
        canvas.Translate((float)node.X * scale, (float)node.Y * scale);
        canvas.Scale(scale);
        using var outer = ParsePath(node.OuterContour);
        using var border = ParsePath(node.BorderContour);
        if (outer is not null)
        {
            using var fill = new SKPaint { Color = FromArgb(node.Background), IsAntialias = true };
            canvas.DrawPath(outer, fill);
        }
        var bounds = new SKRect(0, 0, (float)node.Width, (float)node.Height);
        DrawRim(canvas, border, bounds, node.BorderTop, node.Border, node.BorderBottom);
        if (node.Title is { } title) DrawText(canvas, title, scale);
        foreach (var text in node.Metadata) DrawText(canvas, text, scale);
        foreach (var glyph in node.Glyphs)
        {
            var path = GetIconPath(glyph.Path);
            if (path is null) continue;
            var b = path.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;
            var minSize = (float)Math.Min(glyph.Width, glyph.Height);
            var growth = minSize < 18 ? Math.Min(1.15f, 18 / minSize) : 1;
            var iconScale = minSize / Math.Max(b.Width, b.Height) * growth;
            canvas.Save();
            canvas.Translate((float)(glyph.X + glyph.Width / 2) - b.MidX * iconScale,
                (float)(glyph.Y + glyph.Height / 2) + b.MidY * iconScale);
            canvas.Scale(iconScale, -iconScale);
            using var paint = new SKPaint { Color = FromArgb(glyph.Color), IsAntialias = true };
            canvas.DrawPath(path, paint); canvas.Restore();
        }
        using var rim = ParsePath(node.RimContour);
        DrawRim(canvas, rim, bounds, node.RimTop, node.RimBody, node.RimBottom);
        canvas.Restore();
    }
    private static void DrawRim(SKCanvas canvas, SKPath? path, SKRect bounds, uint top, uint body, uint bottom)
    {
        if (path is null || (body >> 24) == 0) return;
        using var shader = DashboardSelectionPaint.Shader(bounds, FromArgb(top), FromArgb(body), FromArgb(bottom));
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }
    private static void DrawText(SKCanvas canvas, MindMapTileText text, float scale)
    {
        var typeface = SkiaCardRenderer.GetTypeface(text.FontFamily, text.FontWeight, text.Italic);
        // Shape at the raster scale for the same crispness as other tiles; the canvas is in DIPs.
        var layout = SkiaTextLayout.Layout(text.Text, (float)text.Width * scale, typeface,
            text.FontSizeDip * scale, text.MaxLines, SKFontManager.Default, SkiaCardRenderer.GetShaper);
        canvas.Save();
        canvas.ClipRect(new SKRect((float)text.X, (float)text.Y,
            (float)(text.X + text.Width), (float)(text.Y + text.Height)));
        canvas.Scale(1 / scale);
        SkiaCardRenderer.PaintLayout(canvas, layout, (float)text.X * scale, (float)text.Y * scale,
            FromArgb(text.Color), scale);
        canvas.Restore();
    }
    private static SKPath? GetIconPath(string data)
    {
        lock (IconPathGate)
        {
            if (IconPathCache.TryGetValue(data, out var cached)) return cached;
            var parsed = ParsePath(data);
            if (parsed is not null) IconPathCache[data] = parsed;
            return parsed;
        }
    }
    private static SKPath? ParsePath(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var rule = SKPathFillType.EvenOdd;
        data = data.Trim();
        if (data.StartsWith("F1")) { rule = SKPathFillType.Winding; data = data[2..]; }
        else if (data.StartsWith("F0")) data = data[2..];
        var path = SKPath.ParseSvgPathData(data);
        if (path is not null) path.FillType = rule;
        return path;
    }
    private static SKColor FromArgb(uint argb)
        => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
}
