using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// GIFT (Moodle's plain-text quiz interchange format) reader/writer. Covers the common ~80% of
/// real-world GIFT files: title, stem, TRUE/FALSE, weighted/unweighted multiple choice, single
/// short-answer, and trailing general feedback (####). Deliberately not modeled: per-answer
/// feedback (#...# on an individual option - stripped, not stored), multiple acceptable
/// short-answers (only the first {=A =B} is kept), GIFT's Matching question type (we have no
/// equivalent question type), and $CATEGORY pragmas. Parse issues are collected as warnings, not
/// thrown - a mostly-good file should still import everything it can.
/// </summary>
internal static class GiftQuizFormat
{
    public sealed class ParseResult
    {
        public QuizDocument Document { get; init; } = new();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private static readonly char[] ReservedChars = { '~', '=', '#', '{', '}', ':' };

    public static ParseResult Parse(string text, string fallbackTitle)
    {
        var warnings = new List<string>();
        var questions = new List<QuizQuestion>();

        var stripped = StripComments(text);
        var blocks = SplitIntoBlocks(stripped);

        var index = 0;
        foreach (var (stemRaw, answerRaw) in blocks)
        {
            index++;
            try
            {
                var question = ParseQuestion(stemRaw, answerRaw, index, warnings);
                if (question is not null)
                    questions.Add(question);
            }
            catch (Exception ex)
            {
                warnings.Add($"Question {index}: skipped - {ex.Message}");
            }
        }

        var document = new QuizDocument
        {
            Title = fallbackTitle,
            Questions = questions
        };

        return new ParseResult { Document = document, Warnings = warnings };
    }

    public static string Write(QuizDocument document)
    {
        var sb = new StringBuilder();
        var index = 0;

        foreach (var question in document.Questions)
        {
            index++;
            sb.Append("::Q").Append(index.ToString(CultureInfo.InvariantCulture)).Append("::");
            sb.Append(' ').Append(Escape(question.Question.Trim())).Append(' ').Append('{');
            sb.AppendLine();

            switch (question.Type)
            {
                case QuizQuestionType.TrueFalse:
                    var trueOption = question.Options.FirstOrDefault(o => o.IsCorrect);
                    var isTrue = trueOption is not null
                        && string.Equals(trueOption.Text.Trim(), "True", StringComparison.OrdinalIgnoreCase);
                    sb.AppendLine(isTrue ? "TRUE" : "FALSE");
                    break;

                case QuizQuestionType.PlainText:
                    sb.Append('=').AppendLine(Escape(question.PlainTextAnswer.Trim()));
                    break;

                default:
                    var correctCount = question.Options.Count(o => o.IsCorrect);
                    var weight = correctCount > 0 ? Math.Round(100.0 / correctCount, 2) : 0;
                    foreach (var option in question.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)))
                    {
                        if (option.IsCorrect && correctCount > 1)
                            sb.Append("~%").Append(weight.ToString(CultureInfo.InvariantCulture)).Append('%');
                        else
                            sb.Append(option.IsCorrect ? "=" : "~");
                        sb.AppendLine(Escape(option.Text.Trim()));
                    }
                    break;
            }

            if (!string.IsNullOrWhiteSpace(question.Explanation))
                sb.Append("#### ").AppendLine(Escape(question.Explanation.Trim()));

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static void SaveGiftFile(string path, QuizDocument document)
    {
        var text = Write(document);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string StripComments(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join("\n", kept);
    }

    // Scans for unescaped { ... } pairs; the "stem" preceding each pair is everything since the
    // end of the previous pair (or start of file). Handles \{ / \} escapes so literal braces in
    // question text don't get mistaken for block delimiters.
    private static List<(string Stem, string Answer)> SplitIntoBlocks(string text)
    {
        var blocks = new List<(string, string)>();
        var stemStart = 0;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == '{')
            {
                var stem = text[stemStart..i];
                var closeIndex = FindMatchingBrace(text, i);
                if (closeIndex < 0)
                    break;

                var answer = text[(i + 1)..closeIndex];
                blocks.Add((stem, answer));
                i = closeIndex + 1;
                stemStart = i;
                continue;
            }

            i++;
        }

        return blocks;
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        var i = openIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == '}')
                return i;

            i++;
        }

        return -1;
    }

    private static QuizQuestion? ParseQuestion(string stemRaw, string answerRaw, int index, List<string> warnings)
    {
        var stem = stemRaw.Trim();
        if (stem.Length == 0 && answerRaw.Trim().Length == 0)
            return null;

        string? title = null;
        var titleMatch = Regex.Match(stem, @"^::(.*?)::\s*(.*)$", RegexOptions.Singleline);
        if (titleMatch.Success)
        {
            title = Unescape(titleMatch.Groups[1].Value.Trim());
            stem = titleMatch.Groups[2].Value.Trim();
        }

        var questionText = Unescape(stem);
        if (string.IsNullOrWhiteSpace(questionText))
            questionText = string.IsNullOrWhiteSpace(title) ? $"Question {index}" : title;

        var answerContent = answerRaw;
        var explanation = string.Empty;
        var feedbackIndex = FindUnescaped(answerContent, "####");
        if (feedbackIndex >= 0)
        {
            explanation = Unescape(answerContent[(feedbackIndex + 4)..].Trim());
            answerContent = answerContent[..feedbackIndex];
        }

        var trimmedAnswer = answerContent.Trim();

        if (Regex.IsMatch(trimmedAnswer, @"^(TRUE|T|FALSE|F)$", RegexOptions.IgnoreCase))
        {
            var isTrue = trimmedAnswer.StartsWith("T", StringComparison.OrdinalIgnoreCase);
            return new QuizQuestion
            {
                Type = QuizQuestionType.TrueFalse,
                Question = questionText,
                Explanation = explanation,
                Options = new List<QuizOption>
                {
                    new() { Text = "True", IsCorrect = isTrue },
                    new() { Text = "False", IsCorrect = !isTrue }
                }
            };
        }

        var tokens = TokenizeAnswers(trimmedAnswer);
        if (tokens.Count == 0)
        {
            // Bare {answer} shorthand some GIFT writers use instead of the strict {=answer}.
            return new QuizQuestion
            {
                Type = QuizQuestionType.PlainText,
                Question = questionText,
                Explanation = explanation,
                PlainTextAnswer = Unescape(StripInlineFeedback(trimmedAnswer)).Trim()
            };
        }

        if (!tokens.Any(t => !t.IsCorrect))
        {
            // Only correct answers, no distractors -> short-answer question (first kept).
            return new QuizQuestion
            {
                Type = QuizQuestionType.PlainText,
                Question = questionText,
                Explanation = explanation,
                PlainTextAnswer = tokens[0].Text
            };
        }

        var correctCount = tokens.Count(t => t.IsCorrect);
        if (correctCount == 0)
        {
            warnings.Add($"Question {index}: no correct answer marked, defaulting the first option to correct.");
            tokens[0] = tokens[0] with { IsCorrect = true };
            correctCount = 1;
        }

        return new QuizQuestion
        {
            Type = correctCount == 1 ? QuizQuestionType.SingleChoice : QuizQuestionType.MultipleChoice,
            Question = questionText,
            Explanation = explanation,
            Options = tokens.Select(t => new QuizOption { Text = t.Text, IsCorrect = t.IsCorrect }).ToList()
        };
    }

    private readonly record struct AnswerToken(string Text, bool IsCorrect);

    private static List<AnswerToken> TokenizeAnswers(string content)
    {
        var tokens = new List<AnswerToken>();
        var i = 0;

        while (i < content.Length)
        {
            while (i < content.Length && content[i] != '=' && content[i] != '~')
            {
                if (content[i] == '\\' && i + 1 < content.Length) i += 2;
                else i++;
            }

            if (i >= content.Length)
                break;

            var marker = content[i];
            i++;
            var isCorrect = marker == '=';

            if (i < content.Length && content[i] == '%')
            {
                var end = content.IndexOf('%', i + 1);
                if (end > i)
                {
                    var weightText = content[(i + 1)..end];
                    if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                        isCorrect = weight > 0;
                    i = end + 1;
                }
            }

            var textStart = i;
            while (i < content.Length && content[i] != '=' && content[i] != '~')
            {
                if (content[i] == '\\' && i + 1 < content.Length) i += 2;
                else i++;
            }

            var text = Unescape(StripInlineFeedback(content[textStart..i])).Trim();
            tokens.Add(new AnswerToken(text, isCorrect));
        }

        return tokens;
    }

    private static string StripInlineFeedback(string text)
    {
        var index = FindUnescaped(text, "#");
        return index >= 0 ? text[..index] : text;
    }

    private static int FindUnescaped(string text, string marker)
    {
        for (var i = 0; i <= text.Length - marker.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(text.Substring(i, marker.Length), marker) == 0)
                return i;
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && Array.IndexOf(ReservedChars, text[i + 1]) >= 0)
            {
                sb.Append(text[i + 1]);
                i++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Array.IndexOf(ReservedChars, ch) >= 0)
                sb.Append('\\');
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
