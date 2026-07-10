using Scriblucent.Localization;
using Scriblucent.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

public sealed class FlashcardConversionService
{
    public async Task<IReadOnlyList<FlashcardItem>> ConvertToFlashcardsAsync(
        string noteText,
        IProgress<BundledModelHostService.FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return Array.Empty<FlashcardItem>();

        AiInputGuard.EnsureSuitableStudyText(noteText);

        var chunkingProfile = BundledModelHostService.Instance.GetSelectedChunkingProfile();
        var chunks = AiTextChunker.Split(noteText, AiChunkingPurpose.Flashcards, chunkingProfile)
            .Where(AiInputGuard.IsSuitableStudyText)
            .ToList();

        if (chunks.Count == 0)
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var cards = new List<FlashcardItem>();
        var outputs = new List<string>();

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkProgress = CreateChunkProgress(progress, i + 1, chunks.Count);
            var chunkPrompt = BuildPrompt(chunks[i], i + 1, chunks.Count);
            var result = await BundledModelHostService.Instance.CompleteAsync(
                chunkPrompt, nPredict: 1200, temperature: 0.25, progress: chunkProgress, cancellationToken);

            outputs.Add(result);
            if (AiInputGuard.IsRefusalOutput(result))
                continue;

            AddParsedFlashcards(cards, result, i + 1);
        }

        if (cards.Count > 0)
            return DeduplicateFlashcards(cards);

        if (outputs.Any(AiInputGuard.IsRefusalOutput))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        // Repair pass over the full note, but still ask for a bounded set of cards.
        var repairPrompt = BuildRepairPrompt(noteText);
        var repairProgress = CreateRepairProgress(progress, "ConvertToFlashcardsStatusRepairing");
        var repaired = await BundledModelHostService.Instance.CompleteAsync(
            repairPrompt, nPredict: 1500, temperature: 0.1, progress: repairProgress, cancellationToken);

        if (AiInputGuard.IsRefusalOutput(repaired))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var repairedParsed = ParseFlashcards(repaired);
        if (repairedParsed.Count > 0)
            return repairedParsed
                .Select(card => new FlashcardItem
                {
                    Question = card.Question,
                    Answer = card.Answer,
                    SetIndex = 1
                })
                .ToList();

        var diagnosticPath = WriteParseDiagnostic(noteText, string.Join("\n\n--- CHUNK OUTPUT ---\n\n", outputs), repaired);
        throw new InvalidOperationException($"Unable to parse AI output into valid flashcards. Diagnostic log: {diagnosticPath}");
    }

    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateChunkProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        int chunkIndex,
        int chunkCount)
    {
        if (progress is null)
            return progress;

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                ChunkIndex = Math.Max(1, chunkIndex),
                ChunkCount = Math.Max(1, chunkCount)
            }));
    }

    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateRepairProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        string repairStatusKey)
    {
        if (progress is null)
            return progress;

        progress.Report(new BundledModelHostService.FlashcardProgress(repairStatusKey));

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                StatusKey = repairStatusKey
            }));
    }

    private static string BuildPrompt(string noteText, int chunkIndex = 1, int chunkCount = 1)
    {
        return $"""
You are a strict flashcard generator. 
Never think out loud. Never explain. Never output reasoning, analysis, <think> tags or any extra text.

You are processing section {chunkIndex} of {chunkCount} from a longer note.
Cover every important fact in this section from beginning to end.
Do not focus only on the opening lines.
Create as many high-quality flashcards as needed from this section.
Use ONLY facts from SOURCE NOTE. Make every card atomic and useful for spaced repetition.
Use SOURCE NOTE's language and script for every question and answer; use English only for the format keys.
Keep format keys exactly as shown: q, a.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.

Output **ONLY** in this exact format, nothing else:

q: [clear concise question]
a: [short accurate answer]

SOURCE NOTE:
{noteText}
""";
    }

    private static string BuildRepairPrompt(string sourceNote)
    {
        return $"""
You repair flashcard output. 
Ignore any previous noisy text or thinking.
Never think out loud. Never explain. Never output reasoning or extra text.

Use ONLY information from SOURCE NOTE.
Use SOURCE NOTE's language and script for every repaired question and answer; use English only for the format keys.
Keep format keys exactly as shown: q, a.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.
Create **exactly 3 to 8** flashcards in the exact format:

q: [question]
a: [answer]

SOURCE NOTE:
{sourceNote}
""";
    }

    private static IReadOnlyList<FlashcardItem> ParseFlashcards(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return Array.Empty<FlashcardItem>();

        var cleaned = RemoveThinkAndArtifacts(rawOutput);

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

    private static void AddParsedFlashcards(ICollection<FlashcardItem> cards, string rawOutput, int setIndex)
    {
        foreach (var card in ParseFlashcards(rawOutput))
        {
            cards.Add(new FlashcardItem
            {
                Question = card.Question,
                Answer = card.Answer,
                SetIndex = Math.Max(1, setIndex)
            });
        }
    }

    private static IReadOnlyList<FlashcardItem> DeduplicateFlashcards(IEnumerable<FlashcardItem> cards)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<FlashcardItem>();

        foreach (var card in cards)
        {
            var key = $"{card.SetIndex}\u001F{NormalizeCardKey(card.Question)}\u001F{NormalizeCardKey(card.Answer)}";
            if (!seen.Add(key))
                continue;

            unique.Add(card);
        }

        return unique;
    }

    private static string NormalizeCardKey(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.ToLowerInvariant();
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

    private static string RemoveThinkAndArtifacts(string text)
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
        withoutThink = withoutThink.Replace("EOF by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("Interrupted by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        return RemoveTrailingArtifacts(withoutThink.Trim());
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

    private static string WriteParseDiagnostic(string sourceNote, string firstOutput, string repairedOutput)
    {
        try
        {
            var diagnosticsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "AiCache",
                "diagnostics");

            Directory.CreateDirectory(diagnosticsDir);
            var path = Path.Combine(diagnosticsDir, $"parse-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            var firstCleaned = RemoveThinkAndArtifacts(firstOutput);
            var secondCleaned = RemoveThinkAndArtifacts(repairedOutput);

            var sb = new StringBuilder();
            sb.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            sb.AppendLine("--- SOURCE NOTE ---");
            sb.AppendLine(sourceNote);
            sb.AppendLine();
            sb.AppendLine("--- FIRST RAW OUTPUT ---");
            sb.AppendLine(firstOutput);
            sb.AppendLine();
            sb.AppendLine("--- FIRST CLEANED/EXTRACTED ---");
            sb.AppendLine(firstCleaned);
            sb.AppendLine();
            sb.AppendLine("--- REPAIR RAW OUTPUT ---");
            sb.AppendLine(repairedOutput);
            sb.AppendLine();
            sb.AppendLine("--- REPAIR CLEANED/EXTRACTED ---");
            sb.AppendLine(secondCleaned);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(failed to write parse diagnostic log)";
        }
    }

}
