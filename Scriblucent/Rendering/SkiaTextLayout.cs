using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Scriblucent.Rendering;

/// <summary>A laid-out text run (a shaped glyph sequence with positions).</summary>
internal sealed class SkiaTextRun
{
    public required SKTypeface Typeface;
    public required ushort[] Glyphs;
    public required SKPoint[] Positions;
    public float Width;
    public float LineHeight;
    public float FontSize;
    /// <summary>Distance from the line's top to the run's baseline (for canvas.DrawText y).</summary>
    public float Baseline;
}

/// <summary>A single wrapped line of runs.</summary>
internal sealed class SkiaTextLine
{
    public List<SkiaTextRun> Runs = new();
    public float Width;
    public float Height;
}

/// <summary>
/// A lightweight text layout for the card tiles: word-wrapped, max-lines + ellipsis, shaped
/// with HarfBuzz, with per-character font fallback (emoji etc.). SkiaSharp 4.x removed the
/// Paragraph API, so this is the hand-rolled equivalent - deliberately small and testable.
/// </summary>
internal sealed class SkiaTextLayout
{
    private readonly List<SkiaTextLine> _lines = new();

    public IReadOnlyList<SkiaTextLine> Lines => _lines;
    public float Height { get; private set; }
    public float MaxIntrinsicWidth { get; private set; }
    public bool Truncated { get; private set; }

    private const float ImageTokenWidthRatio = 1.4f;

    public static SkiaTextLayout Layout(
        string text,
        float maxWidth,
        SKTypeface primaryTypeface,
        float fontSize,
        int maxLines,
        SKFontManager fontManager,
        Func<SKTypeface, SKShaper> shaperFactory)
    {
        var layout = new SkiaTextLayout();
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return layout;

        var tokens = Tokenize(text);

        // Wrap into lines on word boundaries, estimating each token's width in the primary
        // font (the shaped widths land in the runs at draw time).
        var lines = new List<List<SkiaTextToken>>();
        var current = new List<SkiaTextToken>();
        var currentWidth = 0f;
        lines.Add(current);

        using var measureFont = new SKFont(primaryTypeface, fontSize);

        foreach (var token in tokens)
        {
            if (token.Kind == SkiaTextTokenKind.NewLine)
            {
                if (lines.Count >= maxLines)
                {
                    layout.Truncated = true;
                    break;
                }

                current = new List<SkiaTextToken>();
                currentWidth = 0;
                lines.Add(current);
                continue;
            }

            var tokenWidth = token.Kind == SkiaTextTokenKind.Image
                ? fontSize * ImageTokenWidthRatio
                : measureFont.MeasureText(token.Text);

            if (current.Count > 0 && currentWidth + tokenWidth > maxWidth)
            {
                if (lines.Count >= maxLines)
                {
                    layout.Truncated = true;
                    break;
                }

                current = new List<SkiaTextToken>();
                currentWidth = 0;
                lines.Add(current);
            }

            current.Add(token);
            currentWidth += tokenWidth;
        }

        var ellipsisWidth = MeasureEllipsis(primaryTypeface, fontSize);

        foreach (var lineTokens in lines)
        {
            var line = new SkiaTextLine();
            var x = 0f;
            var lineHeight = 0f;

            foreach (var token in lineTokens)
            {
                if (token.Kind == SkiaTextTokenKind.Image)
                {
                    using var font = new SKFont(primaryTypeface, fontSize);
                    var metrics = font.Metrics;
                    var height = metrics.Descent - metrics.Ascent;
                    line.Runs.Add(new SkiaTextRun
                    {
                        Typeface = primaryTypeface,
                        Glyphs = Array.Empty<ushort>(),
                        Positions = new[] { new SKPoint(x, 0) },
                        Width = fontSize * ImageTokenWidthRatio,
                        LineHeight = height,
                        FontSize = fontSize,
                        Baseline = -metrics.Ascent
                    });
                    x += fontSize * ImageTokenWidthRatio;
                    lineHeight = Math.Max(lineHeight, height);
                    continue;
                }

                var shaped = ShapeToken(token.Text, primaryTypeface, fontSize, fontManager, shaperFactory);
                if (shaped is null)
                    continue;

                var positions = new SKPoint[shaped.Points.Length];
                for (var i = 0; i < shaped.Points.Length; i++)
                    positions[i] = new SKPoint(shaped.Points[i].X + x, shaped.Points[i].Y);

                line.Runs.Add(new SkiaTextRun
                {
                    Typeface = shaped.Typeface,
                    Glyphs = shaped.Glyphs,
                    Positions = positions,
                    Width = shaped.Width,
                    LineHeight = shaped.LineHeight,
                    FontSize = fontSize,
                    Baseline = shaped.Baseline
                });

                x += shaped.Width;
                lineHeight = Math.Max(lineHeight, shaped.LineHeight);
            }

            line.Width = x;
            line.Height = lineHeight;
            layout._lines.Add(line);
            layout.MaxIntrinsicWidth = Math.Max(layout.MaxIntrinsicWidth, x);
            layout.Height += lineHeight;
        }

        // Ellipsis: if the last line overflows the max width, truncate its runs to fit.
        if (layout._lines.Count > 0)
        {
            var last = layout._lines[^1];
            if (last.Width > maxWidth)
            {
                layout.Truncated = true;
                layout.TruncateLastLine(last, maxWidth, ellipsisWidth, primaryTypeface, fontSize);
            }
        }

        return layout;
    }

    private void TruncateLastLine(SkiaTextLine line, float maxWidth, float ellipsisWidth, SKTypeface primaryTypeface, float fontSize)
    {
        while (line.Runs.Count > 0 && line.Runs[^1].Glyphs.Length == 0)
            line.Runs.RemoveAt(line.Runs.Count - 1);

        if (line.Runs.Count == 0)
            return;

        var available = maxWidth - ellipsisWidth;
        var newWidth = 0f;
        var newRuns = new List<SkiaTextRun>();
        var done = false;

        foreach (var run in line.Runs)
        {
            if (done)
                break;

            var count = 0;
            var runWidth = 0f;
            for (var i = 0; i < run.Glyphs.Length; i++)
            {
                float glyphWidth;
                if (i + 1 < run.Positions.Length)
                    glyphWidth = run.Positions[i + 1].X - run.Positions[i].X;
                else if (run.Positions.Length > 1)
                    glyphWidth = run.Width - run.Positions[^1].X;
                else
                    glyphWidth = run.Width;

                if (newWidth + runWidth + glyphWidth > available)
                {
                    done = true;
                    break;
                }

                runWidth += glyphWidth;
                count++;
            }

            if (count > 0)
            {
                newRuns.Add(new SkiaTextRun
                {
                    Typeface = run.Typeface,
                    Glyphs = run.Glyphs.Take(count).ToArray(),
                    Positions = run.Positions.Take(count).ToArray(),
                    Width = runWidth,
                    LineHeight = run.LineHeight,
                    FontSize = run.FontSize,
                    Baseline = run.Baseline
                });
                newWidth += runWidth;
            }
        }

        using var ellipsisFont = new SKFont(primaryTypeface, fontSize);
        var ellipsisGlyph = ellipsisFont.GetGlyphs("…");
        if (ellipsisGlyph.Length > 0)
        {
            var metrics = ellipsisFont.Metrics;
            newRuns.Add(new SkiaTextRun
            {
                Typeface = primaryTypeface,
                Glyphs = ellipsisGlyph,
                Positions = new[] { new SKPoint(newWidth, 0) },
                Width = ellipsisWidth,
                LineHeight = line.Height,
                FontSize = fontSize,
                Baseline = -metrics.Ascent
            });
        }

        line.Runs = newRuns;
        line.Width = newWidth + ellipsisWidth;
    }

    private static float MeasureEllipsis(SKTypeface typeface, float fontSize)
    {
        using var font = new SKFont(typeface, fontSize);
        return font.MeasureText("…");
    }

    private sealed class ShapedToken
    {
        public required SKTypeface Typeface;
        public required ushort[] Glyphs;
        public required SKPoint[] Points;
        public float Width;
        public float LineHeight;
        public float Baseline;
    }

    private static ShapedToken? ShapeToken(string token, SKTypeface primaryTypeface, float fontSize, SKFontManager fontManager, Func<SKTypeface, SKShaper> shaperFactory)
    {
        // Segment the token into font runs by glyph availability (fallback for emoji etc.).
        using var font = new SKFont(primaryTypeface, fontSize);
        var runs = new List<(string Chunk, SKTypeface Typeface)>();
        var current = new System.Text.StringBuilder();
        SKTypeface? currentTypeface = null;

        foreach (var ch in token)
        {
            var glyph = font.GetGlyphs(new[] { ch })[0];
            var typeface = glyph != 0
                ? primaryTypeface
                : fontManager.MatchCharacter(ch) ?? primaryTypeface;

            if (currentTypeface is null)
            {
                currentTypeface = typeface;
            }
            else if (!ReferenceEquals(typeface, currentTypeface))
            {
                runs.Add((current.ToString(), currentTypeface));
                current.Clear();
                currentTypeface = typeface;
            }

            current.Append(ch);
        }

        if (current.Length > 0 && currentTypeface is not null)
            runs.Add((current.ToString(), currentTypeface));

        if (runs.Count == 0)
            return null;

        var totalWidth = 0f;
        var allGlyphs = new List<ushort>();
        var allPoints = new List<SKPoint>();
        float lineHeight = 0;
        float baseline = 0;

        foreach (var (chunk, typeface) in runs)
        {
            using var runFont = new SKFont(typeface, fontSize);
            var metrics = runFont.Metrics;
            lineHeight = Math.Max(lineHeight, metrics.Descent - metrics.Ascent);
            baseline = Math.Max(baseline, -metrics.Ascent);

            var result = shaperFactory(typeface).Shape(chunk, runFont);
            if (result is null || result.Codepoints.Length == 0)
                continue;

            for (var i = 0; i < result.Codepoints.Length; i++)
            {
                allGlyphs.Add((ushort)result.Codepoints[i]);
                var point = result.Points.Length > i ? result.Points[i] : SKPoint.Empty;
                allPoints.Add(new SKPoint(point.X + totalWidth, point.Y));
            }

            totalWidth += result.Width;
        }

        if (allGlyphs.Count == 0)
            return null;

        return new ShapedToken
        {
            Typeface = runs[0].Typeface,
            Glyphs = allGlyphs.ToArray(),
            Points = allPoints.ToArray(),
            Width = totalWidth,
            LineHeight = lineHeight,
            Baseline = baseline
        };
    }

    private static List<SkiaTextToken> Tokenize(string text)
    {
        var tokens = new List<SkiaTextToken>();
        var word = new System.Text.StringBuilder();

        void FlushWord()
        {
            if (word.Length > 0)
            {
                tokens.Add(new SkiaTextToken(SkiaTextTokenKind.Text, word.ToString()));
                word.Clear();
            }
        }

        foreach (var ch in text)
        {
            if (ch == '\uFFFC')
            {
                FlushWord();
                tokens.Add(new SkiaTextToken(SkiaTextTokenKind.Image, "\uFFFC"));
                continue;
            }

            if (ch == '\n' || ch == '\r')
            {
                FlushWord();
                if (ch == '\n')
                    tokens.Add(new SkiaTextToken(SkiaTextTokenKind.NewLine, "\n"));
                continue;
            }

            if (ch == ' ')
            {
                FlushWord();
                tokens.Add(new SkiaTextToken(SkiaTextTokenKind.Text, " "));
                continue;
            }

            word.Append(ch);
        }

        FlushWord();
        return tokens;
    }
}

internal enum SkiaTextTokenKind
{
    Text,
    Image,
    NewLine
}

internal readonly record struct SkiaTextToken(SkiaTextTokenKind Kind, string Text);
