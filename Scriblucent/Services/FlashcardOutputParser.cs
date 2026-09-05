using Scriblucent.Models;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class FlashcardOutputParser
{
    internal static IReadOnlyList<FlashcardItem> Parse(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return Array.Empty<FlashcardItem>();

        var cleaned = Clean(rawOutput);

        var pattern = new Regex(
            @"(?im)^\s*(?:q|question|klausimas)\s*[:：-]\s*(?<q>.+?)\s*(?:\r?\n)+\s*(?:a|answer|atsakymas)\s*[:：-]\s*(?<a>.+?)(?=\r?\n\s*(?:q|question|klausimas|a|answer|atsakymas)[:：-]|\z)",
            RegexOptions.Multiline);

        var matches = pattern.Matches(cleaned);
        var items = new List<FlashcardItem>();

        foreach (Match match in matches)
        {
            var question = NormalizeCardText(match.Groups["q"].Value);
            var answer = NormalizeCardText(match.Groups["a"].Value);

            if (!IsValidFlashcard(question, answer))
                continue;

            items.Add(new FlashcardItem { Question = question, Answer = answer });
        }

        if (items.Count == 0)
            items.AddRange(ParseLinePairs(cleaned));

        if (items.Count == 0)
            items.AddRange(ParseInlinePairs(cleaned));

        return items;
    }

    internal static string Clean(string text)
    {
        var withoutAnsi = Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);

        var withoutThink = Regex.Replace(withoutAnsi, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"\[Start thinking\].*$", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);
        withoutThink = Regex.Replace(withoutThink, @"^\s*Thinking Process:.*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*\[?end of text\]?\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*<\|end\|>\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*EOF\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        // A trailing refusal line after otherwise-valid records silently ate the LAST record: the
        // q/a regex's answer group cannot cross newlines to reach \z, so with no following "q:" to
        // terminate against, the final pair failed to match outright (traced real run: five cards
        // plus an echoed REFUSE tail parsed as four). The grammar no longer permits refusals at
        // all, but pasted or cached legacy output can still carry one - strip it when it is the
        // final line, mirroring the other trailing-artifact removals here.
        withoutThink = Regex.Replace(withoutThink, @"\s*(?:refuse|refusal)\s*[:：][^\n]*\z", string.Empty, RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("EOF by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("Interrupted by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        return RemoveTrailingArtifacts(withoutThink.Trim());
    }

    private static IEnumerable<FlashcardItem> ParseInlinePairs(string text)
    {
        var inlinePattern = new Regex(
            @"(?ims)(?:Q(?:uestion)?|Klausimas)\s*[:：\-]\s*(?<q>.+?)\s+(?:A(?:nswer)?|Atsakymas)\s*[:：\-]\s*(?<a>.+?)(?=(?:\s+(?:Card\s*\d+|Q(?:uestion)?|Klausimas)\s*[:：\-])|\z)",
            RegexOptions.Multiline);

        foreach (Match match in inlinePattern.Matches(text))
        {
            var question = NormalizeCardText(match.Groups["q"].Value);
            var answer = NormalizeCardText(match.Groups["a"].Value);

            if (!IsValidFlashcard(question, answer))
                continue;

            yield return new FlashcardItem
            {
                Question = question,
                Answer = answer
            };
        }
    }

    private static IEnumerable<FlashcardItem> ParseLinePairs(string text)
    {
        var lines = text
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? currentQuestion = null;
        foreach (var line in lines)
        {
            if (IsQuestionLine(line, out var q))
            {
                currentQuestion = NormalizeCardText(q);
                continue;
            }

            if (currentQuestion is not null && IsAnswerLine(line, out var a))
            {
                var question = NormalizeCardText(currentQuestion);
                var answer = NormalizeCardText(a);

                currentQuestion = null;

                if (!IsValidFlashcard(question, answer))
                    continue;

                yield return new FlashcardItem
                {
                    Question = question,
                    Answer = answer
                };
            }
        }
    }

    private static bool IsQuestionLine(string line, out string content)
    {
        return TryStripPrefix(line, ["q", "question", "klausimas", "k"], out content);
    }

    private static bool IsAnswerLine(string line, out string content)
    {
        return TryStripPrefix(line, ["a", "answer", "atsakymas", "ats"], out content);
    }

    private static bool TryStripPrefix(string line, string[] keywords, out string content)
    {
        var trimmed = line.Trim();

        foreach (var keyword in keywords)
        {
            var match = Regex.Match(trimmed, $@"^(?:[-*]\s*)?(?:{Regex.Escape(keyword)})\s*[:：\-]\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                content = match.Groups[1].Value.Trim();
                return true;
            }
        }

        content = string.Empty;
        return false;
    }

    private static string NormalizeCardText(string value)
    {
        var normalized = value.Replace("\r", "", StringComparison.Ordinal).Trim();
        normalized = Regex.Replace(normalized, @"^\s*(?:Q(?:uestion)?|Klausimas|A(?:nswer)?|Atsakymas)\s*[:：\-]\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\[?end of text\]?\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*<\|end\|>\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*EOF\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return RemoveTrailingArtifacts(normalized);
    }

    private static string RemoveTrailingArtifacts(string text)
    {
        var result = text.Trim();

        string[] trailingArtifacts =
        [
            "[end of text]",
            "end of text",
            "<|end|>",
            "EOF",
            "EOF by user",
            "Interrupted by user"
        ];

        foreach (var artifact in trailingArtifacts)
        {
            while (result.EndsWith(artifact, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^artifact.Length].TrimEnd();
            }
        }

        return result;
    }

    private static bool IsValidFlashcard(string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            return false;

        if (question.Length < 6 || answer.Length < 3)
            return false;

        if (question.Equals("...", StringComparison.Ordinal) || answer.Equals("...", StringComparison.Ordinal))
            return false;

        if (Regex.IsMatch(question, @"^\s*(?:A(?:nswer)?|Atsakymas)\s*[:：\-]", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(answer, @"^\s*(?:Q(?:uestion)?|Klausimas)\s*[:：\-]", RegexOptions.IgnoreCase))
            return false;

        if (LooksLikePromptLeak(question, answer))
            return false;

        return true;
    }

    private static bool LooksLikePromptLeak(string question, string answer)
    {
        var q = question.ToLowerInvariant();
        var a = answer.ToLowerInvariant();

        if (q.Contains("<question>") || a.Contains("<answer>") || q.Contains("q: question") || a.Contains("a: answer"))
            return true;

        string[] leakMarkers =
        [
            "focus on definitions",
            "avoid markdown",
            "no markdown",
            "no bullet lists",
            "no numbering",
            "no reasoning",
            "no analysis",
            "source note",
            "use only information",
            "do not use outside",
            "output format",
            "formatting rules",
            "every card must be exactly 2 lines",
            "do not output reasoning",
            "output contract"
        ];

        if (q.Contains("begin_flashcards") || a.Contains("begin_flashcards") || q.Contains("end_flashcards") || a.Contains("end_flashcards") || q.Contains("<<flashcards>>") || a.Contains("<<flashcards>>") || q.Contains("<<end_flashcards>>") || a.Contains("<<end_flashcards>>"))
            return true;

        return leakMarkers.Any(m => q.Contains(m) || a.Contains(m));
    }
}
