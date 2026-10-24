using System.Globalization;
using System.Text;
using Scriblucent.Localization;
using Scriblucent.Models;
using SkiaSharp;

namespace Scriblucent.Rendering;

internal static class NotePreviewRaster
{
    internal const float LineHeight = 20.8f;
    internal static SkiaTextLayout Layout(NotePreviewSnapshot preview, float width, int lines, float scale)
    {
        var text = new StringBuilder(); var labels = new List<string>();
        foreach (var part in preview.Parts)
        {
            if (part.ImageCount == 0) text.Append(part.Text);
            else
            {
                text.Append('\uFFFC');
                labels.Add(part.ImageCount == 1 ? LocalizationService.GetString("NotePreviewImage")
                    : string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("NotePreviewImages"), part.ImageCount));
            }
        }
        return SkiaTextLayout.Layout(text.ToString(), width, SkiaCardRenderer.GetTypeface(SkiaCardRenderer.ResolveFontFamily()),
            14 * scale, lines, SKFontManager.Default, SkiaCardRenderer.GetShaper, labels);
    }

    internal static void PaintChip(SKCanvas canvas, SkiaTextRun run, float x, float top, float lineHeight,
        SKColor background, SKColor foreground, bool dark)
    {
        var scale = run.FontSize / 14;
        var rect = SKRect.Create(x + 2 * scale, top + (lineHeight - 20 * scale) / 2, run.Width - 4 * scale, 20 * scale);
        using var face = new SKPaint { Color = background, IsAntialias = true };
        SurfaceMaterialRaster.Draw(canvas, rect, 7 * scale, 7 * scale, face, dark, scale,
            Scriblucent.Controls.SurfaceMaterialKind.Tinted);
        var icon = SKRect.Create(rect.Left + 6 * scale, rect.Top + 4 * scale, 12 * scale, 12 * scale);
        using var stroke = new SKPaint { Color = foreground, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f * scale, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawRoundRect(icon, 2 * scale, 2 * scale, stroke);
        canvas.DrawCircle(icon.Left + 3.5f * scale, icon.Top + 3.5f * scale, 1 * scale, stroke);
        using var mountains = new SKPathBuilder();
        mountains.MoveTo(icon.Left + 1.5f * scale, icon.Bottom - 2 * scale);
        mountains.LineTo(icon.Left + 5 * scale, icon.Top + 6 * scale);
        mountains.LineTo(icon.Left + 7 * scale, icon.Top + 8 * scale);
        mountains.LineTo(icon.Left + 9 * scale, icon.Top + 5 * scale);
        mountains.LineTo(icon.Right - 1 * scale, icon.Top + 7 * scale);
        canvas.DrawPath(mountains.Detach(), stroke);
        var labelLayout = SkiaTextLayout.Layout(run.ImageLabel!, run.Width - 32 * scale + 1,
            run.Typeface, 12 * scale, 1, SKFontManager.Default, SkiaCardRenderer.GetShaper);
        SkiaCardRenderer.PaintLayout(canvas, labelLayout, rect.Left + 22 * scale,
            rect.Top + (rect.Height - labelLayout.Height) / 2, foreground, scale);
    }
}
