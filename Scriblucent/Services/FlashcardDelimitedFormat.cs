using System.IO;
using System.Text;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Lightweight, content-only flashcard interop format - comma-delimited (.csv, spreadsheet
/// friendly) or tab-delimited (.txt, matching Anki's own plain-text export default) via the same
/// row parser/writer. Columns: Question, Answer, Category, Hint - Category/Hint optional/trailing,
/// blank when missing. No SM-2/FSRS/media columns, matching Anki's own real plain-text scope (it
/// doesn't carry media or scheduling state either). Row parsing (quote-aware, doubled-quote
/// escaping, embedded-newline handling) is a straightforward generalization of QuizCsvFormat's
/// whole-file parser to a parametrized delimiter.
///
/// A real Anki plain-text export has no literal header row - it starts with #-prefixed directive
/// comments (#separator:tab, #html:true, ...) and then goes straight to headerless data rows. To
/// make "paste an Anki export in" actually work, parsing skips leading blank/#-prefixed lines, then
/// treats the first remaining row as a header only if it case-insensitively matches
/// Question/Answer/Category/Hint in order - otherwise every row, including that first one, is
/// treated positionally as data. Export always writes the literal header, so files this app
/// produces stay unambiguous on re-import.
/// </summary>
internal static class FlashcardDelimitedFormat
{
    private static readonly string[] ExpectedHeaders = { "Question", "Answer", "Category", "Hint" };

    public sealed class ParseResult
    {
        public FlashcardSetDocument Document { get; init; } = new();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    public static ParseResult ParseWithWarnings(string text, char delimiter, string fallbackTitle)
    {
        var warnings = new List<string>();
        var rows = ParseRows(text, delimiter)
            .Where(row => row.Count > 0 && !(row.Count == 1 && row[0].Length == 0))
            .SkipWhile(row => row[0].TrimStart().StartsWith('#'))
            .ToList();

        if (rows.Count > 0)
        {
            var header = rows[0].Select(cell => cell.Trim()).ToList();
            var compareCount = Math.Min(header.Count, ExpectedHeaders.Length);
            // At least Question+Answer must be present and match positionally for this to count as
            // a header row - anything shorter or mismatched is treated as a real data row instead.
            var isHeader = compareCount >= 2
                && header.Take(compareCount).SequenceEqual(ExpectedHeaders.Take(compareCount), StringComparer.OrdinalIgnoreCase);
            if (isHeader)
                rows = rows.Skip(1).ToList();
        }

        var cards = new List<FlashcardItem>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var question = row.Count > 0 ? row[0].Trim() : string.Empty;
            var answer = row.Count > 1 ? row[1].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(question))
            {
                warnings.Add($"Row {r + 1}: skipped - empty Question.");
                continue;
            }

            cards.Add(new FlashcardItem
            {
                Question = question,
                Answer = answer,
                Category = row.Count > 2 ? row[2].Trim() : string.Empty,
                Hint = row.Count > 3 ? row[3].Trim() : string.Empty,
                SetIndex = 1
            });
        }

        var document = new FlashcardSetDocument
        {
            Title = fallbackTitle,
            Cards = cards
        };

        return new ParseResult { Document = document, Warnings = warnings };
    }

    // No SetIndex column, so cards from every set in the document are flattened into one flat
    // list on export (re-importing always lands everything in a single set) - the same known
    // limitation QuizCsvFormat documents for its option-count cap; use .scribflash to round-trip
    // a multi-set document exactly.
    public static string Write(FlashcardSetDocument document, char delimiter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, ExpectedHeaders.Select(header => Escape(header, delimiter))));

        foreach (var card in document.Cards)
        {
            var fields = new[] { card.Question, card.Answer, card.Category, card.Hint };
            sb.AppendLine(string.Join(delimiter, fields.Select(field => Escape(field, delimiter))));
        }

        return sb.ToString();
    }

    public static void SaveCsvFile(string path, FlashcardSetDocument document)
        => File.WriteAllText(path, Write(document, ','), new UTF8Encoding(false));

    public static void SaveTxtFile(string path, FlashcardSetDocument document)
        => File.WriteAllText(path, Write(document, '\t'), new UTF8Encoding(false));

    private static string Escape(string? value, char delimiter)
    {
        var text = value ?? string.Empty;
        var needsQuoting = text.Contains(delimiter) || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        if (!needsQuoting)
            return text;

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    // Quote-aware reader operating on the whole file at once (not line-by-line), so a quoted field
    // containing an embedded newline parses correctly instead of getting split into two rows.
    private static List<List<string>> ParseRows(string text, char delimiter)
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

            if (c == '"')
            {
                inQuotes = true;
                i++;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                i++;
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
                i++;
            }
            else
            {
                field.Append(c);
                i++;
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
