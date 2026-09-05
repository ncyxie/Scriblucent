using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal sealed class FlashcardConversionService
{
    private readonly ITextGenerationBackend _backend;

    public FlashcardConversionService(ITextGenerationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public Task<IReadOnlyList<FlashcardItem>> ConvertToFlashcardsAsync(
        string noteText,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return Task.FromResult<IReadOnlyList<FlashcardItem>>(Array.Empty<FlashcardItem>());

        var cards = new List<FlashcardItem>();
        var profile = SamplingProfiles.Default;
        var repairProfile = SamplingProfiles.Repair;

        return AiGenerationPipeline.RunAsync<IReadOnlyList<FlashcardItem>>(
            noteText,
            AiChunkingPurpose.Flashcards,
            _backend,
            new AiGenerationCallbacks
            {
                BuildChunkPrompt = (chunk, _) =>
                {
                    var (min, max) = CalculateTargetFlashcardCountRange(chunk.Text);
                    return BuildPrompt(chunk.Text, min, max, chunk.Index + 1, chunk.TotalCount);
                },
                BuildRepairPrompt = (failedChunkText, _, _) => BuildRepairPrompt(failedChunkText),
                BuildChunkGrammar = chunk =>
                {
                    var (min, max) = CalculateTargetFlashcardCountRange(chunk.Text);
                    return OutputGrammars.FlashcardGrammar(min, max);
                },
                BuildRepairGrammar = failedChunkText =>
                {
                    var (min, max) = CalculateTargetFlashcardCountRange(failedChunkText);
                    return OutputGrammars.FlashcardGrammar(min, max);
                },
                MaxTokens = 1600,
                RepairMaxTokens = 1800,
                Temperature = profile.Temperature,
                RepairTemperature = repairProfile.Temperature,
                TopP = profile.TopP,
                RepairTopP = repairProfile.TopP,
                TopK = profile.TopK,
                RepairTopK = repairProfile.TopK,
                PresencePenalty = profile.PresencePenalty,
                RepairPresencePenalty = repairProfile.PresencePenalty,
                RepetitionPenalty = profile.RepetitionPenalty,
                RepairRepetitionPenalty = repairProfile.RepetitionPenalty,
                GrammarRoot = OutputGrammars.FlashcardRoot,
                OnChunkGenerated = (output, chunks, index) =>
                {
                    var added = AddParsedFlashcards(cards, output, index + 1);
                    return Task.FromResult(added);
                },
                OnRepairGenerated = async (repaired, totalChunkCount, ct) =>
                {
                    // Repair now only resubmits the chunks that failed - append what it recovers
                    // instead of clearing, so cards from chunks that already succeeded survive.
                    // Stamped with a set index past the real chunks, since a single batched repair
                    // response has no attribution back to which specific chunk it recovered.
                    var parsed = FlashcardOutputParser.Parse(repaired);
                    var repairSetIndex = totalChunkCount + 1;
                    foreach (var card in parsed)
                        cards.Add(new FlashcardItem
                        {
                            Question = card.Question,
                            Answer = card.Answer,
                            SetIndex = repairSetIndex
                        });
                    return parsed.Count > 0 ? "repaired" : "";
                },
                Finalize = () =>
                {
                    var deduplicated = DeduplicateFlashcards(cards);
                    // An empty (not just null) result must signal "nothing yet" so the pipeline's
                    // all-refusal check, repair pass, and diagnostic log stay reachable - matching
                    // QuizConversionService's Finalize, which already gets this right.
                    return Task.FromResult<object?>(deduplicated.Count > 0 ? deduplicated : null);
                },
                DiagnosticTag = _ => "flashcard"
            },
            progress, ct);
    }

    private static string BuildPrompt(string noteText, int minCount, int maxCount, int chunkIndex = 1, int chunkCount = 1)
    {
        var section = chunkCount > 1
            ? $"Section {chunkIndex} of {chunkCount}. "
            : "";

        return $"""
{section}Generate between {minCount} and {maxCount} flashcards from the note below - cover the
key points, don't pad with minor or repeated details. Each card must cover a different point -
never reuse the same question wording for multiple cards. Use the exact format shown (the example
below is a generic placeholder, not real content - replace it).

q: [specific question about one point in the note]
a: [answer to that question]

Note:
{noteText}
""";
    }

    private static string BuildRepairPrompt(string sourceNote)
    {
        var (min, max) = CalculateTargetFlashcardCountRange(sourceNote);

        return $"""
Repair the flashcards below. Generate between {min} and {max} cards.

q: [question]
a: [answer]

Source note:
{sourceNote}
""";
    }

    internal static int CalculateTargetFlashcardCount(string sourceNote)
    {
        var wordCount = CountWords(sourceNote);
        if (wordCount < 30) return 2;

        var uniqueWords = CountUniqueWords(sourceNote);
        var uniqueRatio = wordCount > 0 ? (double)uniqueWords / wordCount : 0.5;

        var baseCount = wordCount / 65.0;
        var densityFactor = 0.45 + (uniqueRatio * 1.0);
        var target = (int)Math.Round(baseCount * densityFactor);

        var floor = Math.Max(2, (int)Math.Round(baseCount * 0.3));
        var ceiling = Math.Max(floor + 1, Math.Min(24, (int)Math.Round(baseCount * 1.8)));

        return Math.Clamp(target, floor, ceiling);
    }

    // Turns the single density-adjusted target into a tight band the grammar can enforce as a hard
    // {min,max} cap - see OutputGrammars.FlashcardGrammar's comment. A single number is only ever
    // a suggestion the model can ignore; a grammar-enforced range is a mechanical guarantee.
    internal static (int Min, int Max) CalculateTargetFlashcardCountRange(string sourceNote)
    {
        var target = CalculateTargetFlashcardCount(sourceNote);
        return (Math.Max(2, target - 1), target + 2);
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}").Count;
    }

    private static int CountUniqueWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}"))
            seen.Add(m.Value);
        return seen.Count;
    }

    private static int AddParsedFlashcards(ICollection<FlashcardItem> cards, string rawOutput, int setIndex)
    {
        var parsed = FlashcardOutputParser.Parse(rawOutput);
        foreach (var card in parsed)
        {
            cards.Add(new FlashcardItem
            {
                Question = card.Question,
                Answer = card.Answer,
                SetIndex = Math.Max(1, setIndex)
            });
        }

        return parsed.Count;
    }

    private static IReadOnlyList<FlashcardItem> DeduplicateFlashcards(IEnumerable<FlashcardItem> cards)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenQuestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<FlashcardItem>();

        foreach (var card in cards)
        {
            // Deliberately excludes SetIndex: adjacent chunks overlap by design (see
            // AiChunkingPipeline's overlap window), so the same card can legitimately be
            // generated from two different chunks - keying on content alone is what catches that.
            var key = $"{NormalizeCardKey(card.Question)}||{NormalizeCardKey(card.Answer)}";
            if (!seen.Add(key))
                continue;

            // A second, question-only check: the model can reuse the same generic question text
            // (e.g. "What is the main concept?") across multiple cards with genuinely different
            // answers, which the combined key above doesn't catch since the answers differ. A set
            // of cards that all ask the same thing is a bad flashcard set regardless of whether
            // the answers happen to differ, so this drops every repeat after the first - a
            // deliberately exact-match-only check, never a fuzzy similarity one, so it only ever
            // removes genuinely identical question phrasing.
            var questionKey = NormalizeCardKey(card.Question);
            if (!seenQuestions.Add(questionKey))
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
}
