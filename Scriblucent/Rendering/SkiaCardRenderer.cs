using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Scriblucent.ViewModels;

namespace Scriblucent.Rendering;

/// <summary>
/// Renders a dashboard card into an SKBitmap off the UI thread. Matches the
/// minimal NoteCardControl look: rounded card, title (1 line + ellipsis), wrapped preview
/// (max 4 lines + ellipsis), tags chip. Photo sentinels render as small image glyphs.
/// </summary>
internal static class SkiaCardRenderer
{
    public const float CardWidthDip = 220f;
    public const float CardMarginDip = 12f;
    public const float CardPaddingDip = 14f;
    public const float MinCardHeightDip = 140f;
    public const float CellHeightDip = 164f;
    public const float CardCornerRadiusDip = 14f;
    public const int MaxPreviewLines = 4;

    private static readonly ThreadLocal<Dictionary<SKTypeface, SKShaper>> Shapers = new(() => new());
    private static readonly object TypefaceGate = new();
    private static readonly Dictionary<string, SKTypeface> Typefaces = new();

    public static SKTypeface GetTypeface(string familyName, int weight = 400, bool italic = false)
    {
        var key = $"{familyName}|{weight}|{italic}";
        lock (TypefaceGate)
        {
            if (Typefaces.TryGetValue(key, out var cached))
                return cached;

            // MatchFamily instantiates the variable font at the requested weight (the WPF side
            // renders "Segoe UI Variable Text" SemiBold); FromFamilyName(name, style) silently
            // returns the default (Regular) instance, which made the tile titles thinner than
            // the live text they mirror.
            var style = new SKFontStyle((SKFontStyleWeight)weight, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var typeface =
                SKFontManager.Default.MatchFamily(familyName, style)
                ?? SKFontManager.Default.MatchFamily("Segoe UI", style)
                ?? SKTypeface.FromFamilyName(familyName)
                ?? SKTypeface.FromFamilyName("Segoe UI")
                ?? SKTypeface.Default;
            Typefaces[key] = typeface;
            return typeface;
        }
    }

    internal static SKShaper GetShaper(SKTypeface typeface)
    {
        var shapers = Shapers.Value!;
        if (!shapers.TryGetValue(typeface, out var shaper))
        {
            shaper = new SKShaper(typeface);
            shapers[typeface] = shaper;
        }

        return shaper;
    }

    /// <summary>
    /// Renders the card at the given device-pixel scale factor (WPF's PixelsPerDip - 1.0 at
    /// 96 DPI, 1.5 at 144 DPI, etc. - NOT a raw DPI value). Thread-safe; every render uses its
    /// own bitmap/canvas and the shared typefaces/shaper are only read (the shaper is
    /// thread-local).
    /// </summary>
    public static SKBitmap Render(NoteCardViewModel vm, double dpiScale, CardRenderColors colors, out double heightDip)
    {
        var scale = (float)dpiScale;
        var textWidth = (CardWidthDip - CardPaddingDip * 2) * scale;
        // Titles are SemiBold in the live UI (NoteCardControl / recent strip); the tile text
        // must match or the cards read thinner than the strip.
        var titleTypeface = GetTypeface(ResolveFontFamily(), 600);
        var bodyTypeface = GetTypeface(ResolveFontFamily());
        var fontManager = SKFontManager.Default;

        var title = vm.Title ?? string.Empty;
        // The caller only enqueues a render after the preview is cached. Reading Content here
        // would build a FlowDocument on this background thread.
        var preview = vm.TryGetCachedContentPreview(out var cachedPreview) ? cachedPreview : string.Empty;
        var tags = vm.TagsDisplay;

        Func<SKTypeface, SKShaper> shaperFactory = GetShaper;

        // The menu slot becomes the selection slot while picking, matching the live header.
        var titleLayout = SkiaTextLayout.Layout(title, textWidth - 36 * scale, titleTypeface, 14.5f * scale, 1, fontManager, shaperFactory);

        // Match the uniform 164-DIP cell so applying a tile keeps the estimated extent stable.
        // Fit the preview line count into the remaining space.
        var fixedInnerHeight = (CellHeightDip - CardMarginDip * 2) * scale;
        var titleHeight = titleLayout.Height;
        var tagsLayout = tags.Length > 0
            ? SkiaTextLayout.Layout(tags, textWidth, bodyTypeface, 12f * scale, 1, fontManager, shaperFactory)
            : null;
        var tagsBlockHeight = tagsLayout is null ? 0 : tagsLayout.Height + 6 * scale;

        var previewLineHeight = 19.6f * scale;
        var availablePreview = fixedInnerHeight - CardPaddingDip * 2 * scale - titleHeight - 4 * scale - tagsBlockHeight;
        var previewMaxLines = Math.Max(1, Math.Min(MaxPreviewLines, (int)Math.Floor(availablePreview / previewLineHeight)));

        var previewLayout = SkiaTextLayout.Layout(preview, textWidth, bodyTypeface, 14f * scale, previewMaxLines, fontManager, shaperFactory);

        var contentHeight = titleHeight + 4 * scale + previewLayout.Height + tagsBlockHeight;
        var innerHeight = Math.Max(MinCardHeightDip * scale, Math.Min(CellHeightDip * scale - CardMarginDip * 2 * scale, CardPaddingDip * 2 * scale + contentHeight));
        heightDip = (innerHeight + CardMarginDip * 2) / scale;

        var bitmap = new SKBitmap(
            (int)Math.Ceiling((CardWidthDip + CardMarginDip * 2) * scale),
            (int)Math.Ceiling(heightDip * scale),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var cardRect = SKRect.Create(
            CardMarginDip * scale,
            CardMarginDip * scale,
            CardWidthDip * scale,
            innerHeight);

        using (var background = new SKPaint { Color = colors.Background, IsAntialias = true })
        {
            var radius = CardCornerRadiusDip * scale;
            SurfaceMaterialRaster.Draw(canvas, cardRect, radius, radius, background, colors.Dark, scale,
                Scriblucent.Controls.SurfaceMaterialKind.Raised, frameInset: scale);
        }

        if (colors.Border.Alpha > 0)
        {
            using var borderPaint = new SKPaint
            {
                Color = colors.Border,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = scale
            };
            var radius = (CardCornerRadiusDip + .5f) * scale;
            var rim = cardRect;
            rim.Inflate(-borderPaint.StrokeWidth / 2, -borderPaint.StrokeWidth / 2);
            canvas.DrawRoundRect(rim, Math.Max(0, radius - borderPaint.StrokeWidth / 2), Math.Max(0, radius - borderPaint.StrokeWidth / 2), borderPaint);
        }
        SurfaceMaterialRaster.LightRaisedFrame(canvas, cardRect, CardCornerRadiusDip, colors.Border, colors.Dark, scale);


        var contentX = (CardMarginDip + CardPaddingDip) * scale;
        var contentY = (CardMarginDip + CardPaddingDip) * scale;

        PaintLayout(canvas, titleLayout, contentX, contentY, colors.Title, scale);
        contentY += titleLayout.Height + 4 * scale;

        PaintLayout(canvas, previewLayout, contentX, contentY, colors.Text, scale);
        contentY += previewLayout.Height;

        if (tagsLayout is not null)
        {
            contentY += 6 * scale;
            var chipWidth = Math.Min(tagsLayout.MaxIntrinsicWidth + 12 * scale, textWidth);
            var chipRect = SKRect.Create(contentX, contentY, chipWidth, tagsLayout.Height);
            using var chip = new SKPaint { Color = colors.TagBackground, IsAntialias = true };
            SurfaceMaterialRaster.Draw(canvas, chipRect, 6 * scale, 6 * scale, chip, colors.Dark, scale);
            PaintLayout(canvas, tagsLayout, contentX + 6 * scale, contentY, colors.TagText, scale);
        }

        return bitmap;
    }

    internal static void PaintLayout(SKCanvas canvas, SkiaTextLayout layout, float x, float y, SKColor color, float scale)
    {
        var lineY = y;
        foreach (var line in layout.Lines)
        {
            foreach (var run in line.Runs)
            {
                if (run.Glyphs.Length == 0)
                {
                    PaintImageToken(canvas, run.Positions[0].X + x, lineY, run.FontSize, color);
                    continue;
                }

                using var font = new SKFont(run.Typeface, run.FontSize)
                {
                    // LCD subpixel antialiasing + full hinting - the ClearType analog the cards'
                    // live text uses. Without it the tile text renders aliased/rough.
                    Edging = SKFontEdging.SubpixelAntialias,
                    Hinting = SKFontHinting.Full
                };
                using var builder = new SKTextBlobBuilder();
                builder.AddPositionedRun(run.Glyphs, font, run.Positions);
                using var blob = builder.Build();
                using var paint = new SKPaint { Color = color };
                canvas.DrawText(blob, x, lineY + run.Baseline, paint);
            }

            lineY += line.Height;
        }
    }

    private static void PaintImageToken(SKCanvas canvas, float x, float lineY, float fontSize, SKColor color)
    {
        var size = fontSize * 1.4f;
        var top = lineY + (fontSize - size) / 2;
        var rect = SKRect.Create(x, top, size, size);
        using var paint = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * 0.65f)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, fontSize * 0.08f) };
        canvas.DrawRoundRect(rect, size * 0.2f, size * 0.2f, paint);

        // A tiny "mountain + sun" glyph inside the frame.
        using var inner = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = paint.StrokeWidth };
        using var mountain = new SKPathBuilder();
        mountain.MoveTo(rect.Left + size * 0.2f, rect.Bottom - size * 0.25f);
        mountain.LineTo(rect.Left + size * 0.45f, rect.Top + size * 0.45f);
        mountain.LineTo(rect.Left + size * 0.68f, rect.Bottom - size * 0.25f);
        canvas.DrawPath(mountain.Detach(), inner);

        using var sun = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = paint.StrokeWidth };
        canvas.DrawCircle(rect.Right - size * 0.28f, rect.Top + size * 0.3f, size * 0.09f, sun);
    }

    private static string ResolveFontFamily()
    {
        try
        {
            var app = System.Windows.Application.Current;
            var family = app?.Resources["FontFamily.UI"] as System.Windows.Media.FontFamily;
            var source = family?.Source ?? "Segoe UI Variable Text";
            return source.Split(',')[0].Trim();
        }
        catch
        {
            return "Segoe UI";
        }
    }
}

/// <summary>Theme-resolved card colors, resolved once per theme change and shared by all renders.</summary>
internal readonly record struct CardRenderColors(
    SKColor Background,
    SKColor Border,
    SKColor Title,
    SKColor Text,
    SKColor TagBackground,
    SKColor TagText,
    bool Dark = false);

