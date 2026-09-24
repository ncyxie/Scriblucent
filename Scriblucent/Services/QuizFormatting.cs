using Scriblucent.Models;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class QuizFormatting
{
    private const int MaxQuizTitleLength = 90;

    internal static List<QuizQuestion> PrepareDisplayQuestions(IReadOnlyList<QuizQuestion> questions)
    {
        foreach (var question in questions.Where(question =>
                     question.Type is QuizQuestionType.SingleChoice or QuizQuestionType.MultipleChoice))
        {
            question.Options = question.Options
                .OrderBy(option => StableOptionOrderKey(question.Question, option.Text))
                .ThenBy(option => NormalizeKey(option.Text), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return questions.Where(IsDisplayReadyQuestion).ToList();
    }

    internal static string NormalizeTitle(string value)
    {
        var normalized = NormalizeText(value);
        normalized = Regex.Replace(normalized,
            @"^(?:TITLE|QUIZ TITLE|TEST TITLE|PAVADINIMAS)\s*[:：-]\s*",
            string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim(' ', '"', '\'', '`', ':', '-', '•');
        return normalized.Length > MaxQuizTitleLength
            ? normalized[..MaxQuizTitleLength].TrimEnd() + "..."
            : normalized;
    }

    internal static string NormalizeKey(string value)
    {
        var normalized = NormalizeText(value).ToLowerInvariant()
            .Replace('ą', 'a').Replace('č', 'c').Replace('ę', 'e').Replace('ė', 'e')
            .Replace('į', 'i').Replace('š', 's').Replace('ų', 'u').Replace('ū', 'u')
            .Replace('ž', 'z');
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool IsDisplayReadyQuestion(QuizQuestion question)
    {
        var correctCount = question.Options.Count(option => option.IsCorrect);
        return question.Type switch
        {
            QuizQuestionType.TrueFalse => question.Options.Count == 2 && correctCount == 1,
            QuizQuestionType.SingleChoice => question.Options.Count is >= 3 and <= 6 && correctCount == 1,
            QuizQuestionType.MultipleChoice => question.Options.Count is >= 3 and <= 6
                && correctCount >= 2 && correctCount < question.Options.Count,
            QuizQuestionType.PlainText => !string.IsNullOrWhiteSpace(question.PlainTextAnswer),
            _ => false
        };
    }

    private static int StableOptionOrderKey(string question, string option)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in NormalizeKey(question + " " + option))
                hash = (hash * 31) + character;
            return hash;
        }
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = value.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        normalized = Regex.Replace(normalized, @"^\s*(?:[-*•]|\d+[\.)])\s*", string.Empty);
        return Regex.Replace(normalized, @"\s+", " ").Trim(' ', '"', '\'', '`');
    }
}
