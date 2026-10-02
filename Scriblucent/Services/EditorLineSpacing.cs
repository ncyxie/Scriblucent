using System.Windows.Media;

namespace Scriblucent.Services;

/// <summary>
/// Spacing steps follow Word, Google Docs, and LibreOffice. 1.0 is the font's own single
/// line, and 1.15, 1.5, and 2.0 multiply that line. Older notes stored the step times the
/// point size, which sits under the font's own line for 1.0 and 1.15.
/// </summary>
internal static class EditorLineSpacing
{
    internal static readonly double[] Steps = [1.0, 1.15, 1.5, 2.0];

    private const double StepMatch = 0.04;

    /// <summary>Baseline-to-baseline distance of one line, relative to the font size.
    /// This is the line WPF draws when no line height is set.</summary>
    internal static double SingleSpacing(FontFamily? family)
    {
        var spacing = family?.LineSpacing ?? double.NaN;
        return double.IsNaN(spacing) || spacing <= 0 ? 1 : spacing;
    }

    internal static double LineHeight(double singleLine, double multiplier)
    {
        if (double.IsNaN(singleLine) || singleLine <= 0)
            singleLine = 1;
        if (double.IsNaN(multiplier) || multiplier <= 0)
            multiplier = 1;
        return Math.Max(1, singleLine * multiplier);
    }

    /// <summary>Reads a stored line height back into the spacing step the menu shows.</summary>
    internal static double ReadMultiplier(double lineHeight, double fontSize, double singleLine)
    {
        if (lineHeight <= 0 || double.IsNaN(lineHeight) || fontSize <= 0 || singleLine <= 0)
            return 1;

        var legacy = lineHeight / fontSize;
        var current = lineHeight / singleLine;
        var hasLegacy = TrySnap(legacy, out var legacyStep, out var legacyDistance);
        var hasCurrent = TrySnap(current, out var currentStep, out var currentDistance);
        if (hasCurrent && (!hasLegacy || currentDistance <= legacyDistance))
            return currentStep;
        if (hasLegacy)
            return legacyStep;
        return current;
    }

    /// <summary>True when the height was saved as point size times a menu step, so it still
    /// needs to move onto the font's own line.</summary>
    internal static bool IsLegacyPointSizeStep(double lineHeight, double fontSize, double singleLine, out double step)
    {
        step = 1;
        if (lineHeight <= 0 || double.IsNaN(lineHeight) || fontSize <= 0 || singleLine <= 0)
            return false;

        if (!TrySnap(lineHeight / fontSize, out var legacyStep, out var legacyDistance))
            return false;
        if (TrySnap(lineHeight / singleLine, out _, out var currentDistance) && currentDistance <= legacyDistance)
            return false;

        step = legacyStep;
        return true;
    }

    private static bool TrySnap(double value, out double step, out double distance)
    {
        step = 1;
        distance = double.PositiveInfinity;
        foreach (var candidate in Steps)
        {
            var gap = Math.Abs(value - candidate);
            if (gap < distance)
            {
                distance = gap;
                step = candidate;
            }
        }

        return distance <= StepMatch;
    }
}
