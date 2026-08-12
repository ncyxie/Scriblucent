using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Scriblucent.Icons;

/// <summary>
/// Parses PackIconLucide path data into frozen WPF <see cref="StreamGeometry"/> values,
/// cached per kind. Pure WPF vectors — no bitmaps, no Skia — so icons stay crisp at any
/// DPI, layout size, or ancestor scale (editor zoom). The Lucide data is authored Y-up in a
/// ~1000-unit canvas; the AppIcon control mirrors the Y axis and centers it when drawing.
/// </summary>
internal static class LucideGeometryCache
{
    private static readonly Dictionary<PackIconLucideKind, StreamGeometry?> Cache = new();
    private static readonly object Gate = new();

    public static StreamGeometry? Get(PackIconLucideKind kind)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(kind, out var cached))
                return cached;
        }

        var geometry = Parse(new PackIconLucide { Kind = kind }.Data ?? string.Empty);
        lock (Gate)
        {
            Cache[kind] = geometry;
        }

        return geometry;
    }

    private static StreamGeometry? Parse(string data)
    {
        try
        {
            return ParseCore(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses an SVG path string (M/L/H/V/C/S/Q/T/A/Z with relative variants) into a
    /// StreamGeometry. Real Lucide pack data only uses absolute M/L/C, but the full set is
    /// supported for robustness; arcs degrade to a straight chord.</summary>
    private static StreamGeometry? ParseCore(string data)
    {
        var tokens = Tokenize(data);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var i = 0;
        double currentX = 0, currentY = 0, startX = 0, startY = 0;
        double prevC2X = 0, prevC2Y = 0, prevQX = 0, prevQY = 0;
        var lastCommand = ' ';
        var prevCommand = ' ';

        while (i < tokens.Count)
        {
            if (tokens[i].IsCommand)
            {
                lastCommand = tokens[i].Command;
                i++;
            }
            else if (lastCommand == ' ')
            {
                break;
            }

            var relative = char.IsLower(lastCommand);
            var command = char.ToUpperInvariant(lastCommand);

            switch (command)
            {
                case 'M':
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.BeginFigure(new Point(currentX, currentY), isFilled: true, isClosed: false);
                    startX = currentX;
                    startY = currentY;
                    lastCommand = relative ? 'l' : 'L';
                    break;
                case 'L':
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.LineTo(new Point(currentX, currentY), isStroked: true, isSmoothJoin: false);
                    break;
                case 'H':
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    ctx.LineTo(new Point(currentX, currentY), isStroked: true, isSmoothJoin: false);
                    break;
                case 'V':
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.LineTo(new Point(currentX, currentY), isStroked: true, isSmoothJoin: false);
                    break;
                case 'C':
                {
                    var c1X = NextCoord(tokens, ref i, currentX, relative);
                    var c1Y = NextCoord(tokens, ref i, currentY, relative);
                    var c2X = NextCoord(tokens, ref i, currentX, relative);
                    var c2Y = NextCoord(tokens, ref i, currentY, relative);
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.BezierTo(
                        new Point(c1X, c1Y),
                        new Point(c2X, c2Y),
                        new Point(currentX, currentY),
                        isStroked: true,
                        isSmoothJoin: false);
                    prevC2X = c2X;
                    prevC2Y = c2Y;
                    break;
                }
                case 'S':
                {
                    // Reflect the previous C/S control point, or fall back to the current point.
                    double c1X, c1Y;
                    if (prevCommand is 'C' or 'S')
                    {
                        c1X = currentX * 2 - prevC2X;
                        c1Y = currentY * 2 - prevC2Y;
                    }
                    else
                    {
                        c1X = currentX;
                        c1Y = currentY;
                    }

                    var c2X = NextCoord(tokens, ref i, currentX, relative);
                    var c2Y = NextCoord(tokens, ref i, currentY, relative);
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.BezierTo(
                        new Point(c1X, c1Y),
                        new Point(c2X, c2Y),
                        new Point(currentX, currentY),
                        isStroked: true,
                        isSmoothJoin: false);
                    prevC2X = c2X;
                    prevC2Y = c2Y;
                    break;
                }
                case 'Q':
                {
                    var qX = NextCoord(tokens, ref i, currentX, relative);
                    var qY = NextCoord(tokens, ref i, currentY, relative);
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.QuadraticBezierTo(
                        new Point(qX, qY),
                        new Point(currentX, currentY),
                        isStroked: true,
                        isSmoothJoin: false);
                    prevQX = qX;
                    prevQY = qY;
                    break;
                }
                case 'T':
                {
                    // Reflect the previous Q/T control point, or fall back to the current point.
                    double qX, qY;
                    if (prevCommand is 'Q' or 'T')
                    {
                        qX = currentX * 2 - prevQX;
                        qY = currentY * 2 - prevQY;
                    }
                    else
                    {
                        qX = currentX;
                        qY = currentY;
                    }

                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.QuadraticBezierTo(
                        new Point(qX, qY),
                        new Point(currentX, currentY),
                        isStroked: true,
                        isSmoothJoin: false);
                    prevQX = qX;
                    prevQY = qY;
                    break;
                }
                case 'A':
                {
                    // Arcs never appear in the Lucide pack data; degrade to a straight chord.
                    _ = NextCoord(tokens, ref i, 0, false); // rx
                    _ = NextCoord(tokens, ref i, 0, false); // ry
                    _ = NextCoord(tokens, ref i, 0, false); // rotation
                    _ = NextCoord(tokens, ref i, 0, false); // large-arc flag
                    _ = NextCoord(tokens, ref i, 0, false); // sweep flag
                    currentX = NextCoord(tokens, ref i, currentX, relative);
                    currentY = NextCoord(tokens, ref i, currentY, relative);
                    ctx.LineTo(new Point(currentX, currentY), isStroked: true, isSmoothJoin: false);
                    break;
                }
                case 'Z':
                    ctx.Close();
                    currentX = startX;
                    currentY = startY;
                    lastCommand = ' ';
                    break;
            }

            prevCommand = command;
            if (command == 'Z')
                prevCommand = ' ';
        }

        geometry.Freeze();
        return geometry;
    }

    private static double NextCoord(List<NumberToken> tokens, ref int i, double current, bool relative)
    {
        if (i >= tokens.Count || tokens[i].IsCommand)
            return current;

        var value = tokens[i].Value;
        i++;
        return relative ? current + value : value;
    }

    private readonly record struct NumberToken(bool IsCommand, char Command, double Value);

    private static List<NumberToken> Tokenize(string data)
    {
        var result = new List<NumberToken>();
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
                result.Add(new NumberToken(true, c, 0));
                i++;
                continue;
            }

            var start = i;
            if (c == '+' || c == '-')
                i++;
            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.'))
                i++;

            if (i > start && double.TryParse(data.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                result.Add(new NumberToken(false, ' ', value));
            else
                i++;
        }

        return result;
    }
}
