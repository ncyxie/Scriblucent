using System.Globalization;

namespace Scriblucent.Models;

/// <summary>Shared A/B/C… option index labels for quiz editor and take mode.</summary>
public static class QuizOptionMarker
{
    public static string LetterFromIndex(int index)
    {
        if (index < 0)
            return "?";

        if (index < 26)
            return ((char)('A' + index)).ToString();

        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatWithLetter(int index, string text)
        => string.Concat(LetterFromIndex(index), ". ", text ?? string.Empty);

    /// <summary>
    /// Multiple choice uses a rounded square chip; single-choice / true-false use a circle
    /// (radio-style), matching common quiz UI conventions.
    /// </summary>
    public static bool UsesCircularMarker(QuizQuestionType type)
        => type != QuizQuestionType.MultipleChoice;

    public static double MarkerCornerRadius(QuizQuestionType type)
        => UsesCircularMarker(type) ? 13 : 6;
}
