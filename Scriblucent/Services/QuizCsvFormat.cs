using System.Globalization;
using System.IO;
using System.Text;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// This app's own bulk-edit CSV schema for quizzes - a real spreadsheet layout (fixed
/// Option1..Option6 columns) rather than a packed delimiter string, so it's actually usable as a
/// column-by-column edit target in Excel/Sheets. There's no universal "CSV quiz standard" the way
/// GIFT is a standard, so this is positioned alongside .scribquiz as ours, not alongside GIFT as
/// an external format. Structure+correctness only - no per-question Id, LastAttemptCorrect,
/// SetIndex, PlainTextCharLimit/MatchCase/IgnorePunctuation, or any document-level field.
/// </summary>
internal static class QuizCsvFormat
{
    private const int MaxOptions = 6;

    private static readonly string[] ExpectedHeaders = BuildHeaders();

    public sealed class ParseResult
    {
        public QuizDocument Document { get; init; } = new();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    public sealed class WriteResult
    {
        public string Csv { get; init; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    private static string[] BuildHeaders()
    {
        var headers = new List<string> { "Number", "Type", "Question" };
        for (var i = 1; i <= MaxOptions; i++)
            headers.Add($"Option{i}");
        headers.Add("CorrectOptions");
        headers.Add("Explanation");
        headers.Add("Hint");
        return headers.ToArray();
    }

    public static QuizDocument Parse(string text, string fallbackTitle)
        => ParseWithWarnings(text, fallbackTitle).Document;

    public static ParseResult ParseWithWarnings(string text, string fallbackTitle)
    {
        var warnings = new List<string>();
        var rows = ParseCsvRows(text)
            .Where(row => row.Count > 0 && !(row.Count == 1 && row[0].Length == 0))
            .ToList();

        if (rows.Count == 0)
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportUnsupportedFormat"));

        var header = rows[0].Select(cell => cell.Trim()).ToList();
        if (header.Count != ExpectedHeaders.Length
            || !header.SequenceEqual(ExpectedHeaders, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportUnsupportedFormat"));
        }

        var questions = new List<QuizQuestion>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count < ExpectedHeaders.Length)
            {
                warnings.Add($"Row {r + 1}: skipped - expected {ExpectedHeaders.Length} columns, found {row.Count}.");
                continue;
            }

            var typeText = row[1].Trim();
            if (!Enum.TryParse<QuizQuestionType>(typeText, true, out var type))
            {
                warnings.Add($"Row {r + 1}: skipped - unrecognized Type '{typeText}'.");
                continue;
            }

            // Column layout: Number(0), Type(1), Question(2), Option1..OptionN(3..2+MaxOptions),
            // CorrectOptions, Explanation, Hint (the three columns right after the options).
            const int firstOptionColumn = 3;
            var correctOptionsColumn = firstOptionColumn + MaxOptions;
            var explanationColumn = correctOptionsColumn + 1;
            var hintColumn = correctOptionsColumn + 2;

            var question = new QuizQuestion
            {
                Type = type,
                Question = row[2],
                Explanation = row[explanationColumn],
                Hint = row[hintColumn]
            };

            var correctRaw = row[correctOptionsColumn].Trim();

            if (type == QuizQuestionType.PlainText)
            {
                question.PlainTextAnswer = correctRaw;
            }
            else
            {
                var optionTexts = new List<string>();
                for (var col = firstOptionColumn; col < correctOptionsColumn; col++)
                {
                    if (!string.IsNullOrWhiteSpace(row[col]))
                        optionTexts.Add(row[col]);
                }

                var correctIndexes = correctRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1)
                    .Where(n => n >= 1)
                    .ToHashSet();

                question.Options = optionTexts
                    .Select((optionText, idx) => new QuizOption { Text = optionText, IsCorrect = correctIndexes.Contains(idx + 1) })
                    .ToList();

                if (question.Options.Count > 0 && question.Options.All(option => !option.IsCorrect))
                {
                    warnings.Add($"Row {r + 1}: no correct option listed, defaulting the first option to correct.");
                    question.Options[0].IsCorrect = true;
                }
            }

            questions.Add(question);
        }

        var document = new QuizDocument
        {
            Title = fallbackTitle,
            Questions = questions
        };

        return new ParseResult { Document = document, Warnings = warnings };
    }

    public static WriteResult Write(QuizDocument document)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", ExpectedHeaders.Select(EscapeCsv)));

        var number = 0;
        foreach (var question in document.Questions)
        {
            number++;
            var allOptions = question.Options ?? new List<QuizOption>();
            if (allOptions.Count > MaxOptions)
            {
                warnings.Add($"Question {number}: has {allOptions.Count} options, only the first {MaxOptions} fit in CSV - use .scribquiz or GIFT for this question instead.");
            }

            var options = allOptions.Take(MaxOptions).ToList();

            var correctColumn = question.Type == QuizQuestionType.PlainText
                ? question.PlainTextAnswer
                : string.Join(",", options
                    .Select((option, idx) => (option.IsCorrect, Number: idx + 1))
                    .Where(pair => pair.IsCorrect)
                    .Select(pair => pair.Number.ToString(CultureInfo.InvariantCulture)));

            var fields = new List<string>
            {
                number.ToString(CultureInfo.InvariantCulture),
                question.Type.ToString(),
                question.Question
            };

            for (var i = 0; i < MaxOptions; i++)
                fields.Add(i < options.Count ? options[i].Text : string.Empty);

            fields.Add(correctColumn);
            fields.Add(question.Explanation);
            fields.Add(question.Hint);

            sb.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return new WriteResult { Csv = sb.ToString(), Warnings = warnings };
    }

    public static IReadOnlyList<string> SaveCsvFile(string path, QuizDocument document)
    {
        var result = Write(document);
        File.WriteAllText(path, result.Csv, new UTF8Encoding(false));
        return result.Warnings;
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        var escaped = text.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    // Quote-aware CSV reader operating on the whole file at once (not line-by-line), so a quoted
    // field containing an embedded newline (a multi-line question/explanation someone typed in
    // Excel) parses correctly instead of getting split into two rows.
    private static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var i = 0;

        while (i < normalized.Length)
        {
            var c = normalized[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < normalized.Length && normalized[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
