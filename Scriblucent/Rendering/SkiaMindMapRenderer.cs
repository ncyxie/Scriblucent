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

    private const float ChevronSize = 8f;
    private const float MetaGlyphSize = 13f;
    private const float MetaItemGap = 8f;
    private const float IconMargin = 4f;
    private const float TitleMetaGap = 8f;
    private const float MetaRowHeight = 18f;
    private const float CardPaddingX = 14f;
    private const float CardPaddingY = 12f;
    private const float ChevronHitSize = 20f;
    private const float MaxTitleLines = 4;

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
            canvas.DrawPath(path.Detach(), paint);
        }

        foreach (var node in snapshot.Nodes)
        {
            DrawNode(canvas, node, renderScale, zoom, dpi);
        }

        return bitmap;
    }

    private static void DrawNode(SKCanvas canvas, MindMapTileNode node, float scale, float zoom, float dpi)
    {
        var x = (float)node.X * scale;
        var y = (float)node.Y * scale;
        var w = (float)node.Width * scale;
        var h = (float)node.Height * scale;

        var radius = GetRadius(node.Shape, node.IsRoot, scale);

        using (var bg = new SKPaint { Color = FromArgb(node.Background), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), radius, radius, bg);

        if ((node.Border >> 24) > 0 && node.BorderThickness > 0)
        {
            using var border = new SKPaint
            {
                Color = FromArgb(node.Border),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)node.BorderThickness * scale,
            };
            canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), radius, radius, border);
        }

        // Title block: chevron reserve + wrapped/truncated title.
        var titleX = x + CardPaddingX * scale;
        var titleY = y + CardPaddingY * scale;
        var contentWidth = w - CardPaddingX * 2 * scale;
        var hasMeta = node.IconPaths.Count > 0 || node.IconOverflow > 0;

        var titleWidth = contentWidth - (node.HasChevron ? ChevronHitSize * scale : 0);

        var titleTypeface = ResolveTitleTypeface(node);
        var titleLayout = SkiaTextLayout.Layout(
            node.Title,
            titleWidth,
            titleTypeface,
            node.FontSizeDip * scale,
            (int)MaxTitleLines,
            SKFontManager.Default,
            SkiaCardRenderer.GetShaper);
        var titleLineHeight = titleLayout.Height;

        var titleAreaHeight = h - CardPaddingY * 2 * scale - (hasMeta ? TitleMetaGap * scale + MetaRowHeight * scale : 0);
        var titleBlockY = titleY + Math.Max(0, (titleAreaHeight - titleLineHeight) / 2);

        if (node.HasChevron)
        {
            DrawChevron(canvas, x + (ChevronHitSize - ChevronSize) / 2 * scale, y + (h - ChevronSize * scale) / 2, ChevronSize * scale, node.Folded, FromArgb(node.TitleColor));
            titleX += ChevronHitSize * scale;
        }

        SkiaCardRenderer.PaintLayout(canvas, titleLayout, titleX, titleBlockY, FromArgb(node.TitleColor), scale);

        // Meta row: icons + overflow count, bottom-aligned.
        if (hasMeta)
        {
            var metaY = y + h - CardPaddingY * scale - MetaRowHeight * scale;
            var iconX = titleX;
            var iconColor = FromArgb(node.IconColor);
            foreach (var iconPathData in node.IconPaths)
            {
                var path = GetIconPath(iconPathData);
                if (path is null)
                    continue;

                DrawIconPath(canvas, path, iconX, metaY + (MetaRowHeight - MetaGlyphSize) / 2 * scale, MetaGlyphSize * scale, iconColor);
                iconX += (MetaGlyphSize + IconMargin) * scale;
            }

            if (node.IconOverflow > 0)
            {
                var overflowText = "+" + node.IconOverflow;
                var overflowLayout = SkiaTextLayout.Layout(
                    overflowText,
                    60f * scale,
                    ResolveTitleTypeface(node),
                    11f * scale,
                    1,
                    SKFontManager.Default,
                    SkiaCardRenderer.GetShaper);
                SkiaCardRenderer.PaintLayout(canvas, overflowLayout, iconX, metaY + 2f * scale, iconColor, scale);
            }
        }
    }

    private static float GetRadius(string? shape, bool isRoot, float scale)
    {
        var cardRadius = 14f;
        return shape switch
        {
            "Circle" => 95f,
            "Ellipse" => 26f,
            "Rectangle" => 0f,
            _ => cardRadius,
        } * scale;
    }

    private static void DrawChevron(SKCanvas canvas, float x, float y, float size, bool folded, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var path = new SKPathBuilder();
        if (folded)
        {
            path.MoveTo(x + size * 0.25f, y + size * 0.2f);
            path.LineTo(x + size * 0.75f, y + size * 0.5f);
            path.LineTo(x + size * 0.25f, y + size * 0.8f);
        }
        else
        {
            path.MoveTo(x + size * 0.2f, y + size * 0.25f);
            path.LineTo(x + size * 0.5f, y + size * 0.75f);
            path.LineTo(x + size * 0.8f, y + size * 0.25f);
        }
        canvas.DrawPath(path.Detach(), paint);
    }

    private static SKTypeface ResolveTitleTypeface(MindMapTileNode node)
    {
        var family = string.IsNullOrWhiteSpace(node.FontFamily) ? "Segoe UI Variable Text" : node.FontFamily;
        var weight = Math.Clamp(node.FontWeight, 100, 900);
        return SkiaCardRenderer.GetTypeface(family, weight);
    }

    private static void DrawIconPath(SKCanvas canvas, SKPath path, float x, float y, float size, SKColor color)
    {
        var bounds = path.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var scale = size / Math.Max(bounds.Width, bounds.Height);
        canvas.Save();
        canvas.Translate(x - bounds.Left * scale, y + bounds.Top * scale);
        canvas.Scale(scale, -scale);
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private static SKPath? GetIconPath(string pathData)
    {
        lock (IconPathGate)
        {
            if (IconPathCache.TryGetValue(pathData, out var cached))
                return cached;

            var parsed = ParseIconPath(pathData);
            IconPathCache[pathData] = parsed!;
            return parsed;
        }
    }

    /// <summary>Minimal SVG path parser (M/L/C/H/V/Z, absolute + relative) for the Lucide node
    /// icon data — the pack's converted paths use M/L/C only.</summary>
    private static SKPath? ParseIconPath(string data)
    {
        try
        {
            var path = new SKPathBuilder();
            var tokens = new List<(bool IsCommand, char Command, float Value)>();
            var i = 0;
            while (i < data.Length)
            {
                var c = data[i];
                if (char.IsWhiteSpace(c) || c == ',')
                {
                    i++;
                    continue;
                }

                if (char.IsLetter(c))
                {
                    tokens.Add((true, c, 0));
                    i++;
                    continue;
                }

                var start = i;
                if (c == '+' || c == '-')
                    i++;
                while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.'))
                    i++;
                if (i > start && float.TryParse(data.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    tokens.Add((false, ' ', value));
                else
                    i++;
            }

            var idx = 0;
            float currentX = 0, currentY = 0, startX = 0, startY = 0;
            var lastCommand = ' ';
            while (idx < tokens.Count)
            {
                if (tokens[idx].IsCommand)
                {
                    lastCommand = tokens[idx].Command;
                    idx++;
                }
                else if (lastCommand == ' ')
                {
                    break;
                }

                var relative = char.IsLower(lastCommand);
                var command = char.ToUpperInvariant(lastCommand);

                float NextCoord(float current, bool rel)
                {
                    if (idx >= tokens.Count || tokens[idx].IsCommand)
                        return current;
                    var v = tokens[idx].Value;
                    idx++;
                    return rel ? current + v : v;
                }

                switch (command)
                {
                    case 'M':
                        currentX = NextCoord(currentX, relative);
                        currentY = NextCoord(currentY, relative);
                        path.MoveTo(currentX, currentY);
                        startX = currentX;
                        startY = currentY;
                        lastCommand = relative ? 'l' : 'L';
                        break;
                    case 'L':
                        currentX = NextCoord(currentX, relative);
                        currentY = NextCoord(currentY, relative);
                        path.LineTo(currentX, currentY);
                        break;
                    case 'H':
                        currentX = NextCoord(currentX, relative);
                        path.LineTo(currentX, currentY);
                        break;
                    case 'V':
                        currentY = NextCoord(currentY, relative);
                        path.LineTo(currentX, currentY);
                        break;
                    case 'C':
                    {
                        var c1x = NextCoord(currentX, relative);
                        var c1y = NextCoord(currentY, relative);
                        var c2x = NextCoord(currentX, relative);
                        var c2y = NextCoord(currentY, relative);
                        currentX = NextCoord(currentX, relative);
                        currentY = NextCoord(currentY, relative);
                        path.CubicTo(c1x, c1y, c2x, c2y, currentX, currentY);
                        break;
                    }
                    case 'Z':
                        path.Close();
                        currentX = startX;
                        currentY = startY;
                        lastCommand = ' ';
                        break;
                    default:
                        lastCommand = ' ';
                        break;
                }
            }

            return path.Detach();
        }
        catch
        {
            return null;
        }
    }

    private static SKColor FromArgb(uint argb)
        => new(
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF),
            (byte)((argb >> 24) & 0xFF));
}


