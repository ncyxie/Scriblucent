using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal sealed class QuizConversionService
{
    private const int MaxQuestionCount = 60;

    private readonly ITextGenerationBackend _backend;

    public QuizConversionService(ITextGenerationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<QuizDocument?> ConvertToQuizAsync(
        string noteTitle,
        string noteText,
        Guid? sourceNoteId = null,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return null;

        var normalizedNoteTitle = QuizOutputParser.NormalizeTitle(noteTitle);
        var questions = new List<QuizQuestion>();
        var profile = SamplingProfiles.Default;
        var repairProfile = SamplingProfiles.Repair;

        var result = await AiGenerationPipeline.RunAsync<List<QuizQuestion>>(
            noteText,
            AiChunkingPurpose.Quiz,
            _backend,
            new AiGenerationCallbacks
            {
                BuildChunkPrompt = (chunk, _) =>
                {
                    var (min, max) = CalculateTargetChunkQuestionCountRange(chunk.Text, chunk.TotalCount);
                    return BuildPrompt(normalizedNoteTitle, chunk.Text, min, max, chunk.Index + 1, chunk.TotalCount);
                },
                BuildRepairPrompt = (failedChunkText, _, _) =>
                {
                    var (min, max) = CalculateTargetChunkQuestionCountRange(failedChunkText, 1);
                    return BuildRepairPrompt(normalizedNoteTitle, failedChunkText, min, max);
                },
                BuildChunkGrammar = chunk =>
                {
                    var (min, max) = CalculateTargetChunkQuestionCountRange(chunk.Text, chunk.TotalCount);
                    return OutputGrammars.QuizGrammar(min, max);
                },
                BuildRepairGrammar = failedChunkText =>
                {
                    var (min, max) = CalculateTargetChunkQuestionCountRange(failedChunkText, 1);
                    return OutputGrammars.QuizGrammar(min, max);
                },
                MaxTokens = 1800,
                RepairMaxTokens = 2800,
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
                GrammarRoot = OutputGrammars.QuizRoot,
                OnChunkGenerated = (output, chunks, index) =>
                {
                    var parsed = QuizOutputParser.Parse(output);
                    QuizOutputParser.AddParsedQuestions(questions, parsed.Questions, index + 1);
                    return Task.FromResult(parsed.Questions.Count);
                },
                OnRepairGenerated = async (repaired, totalChunkCount, ct) =>
                {
                    // Repair now only resubmits the chunks that failed - append what it recovers
                    // instead of clearing, so questions from chunks that already succeeded survive.
                    // Stamped with a set index past the real chunks, since a single batched repair
                    // response has no attribution back to which specific chunk it recovered.
                    var parsed = QuizOutputParser.Parse(repaired);
                    QuizOutputParser.AddParsedQuestions(questions, parsed.Questions, totalChunkCount + 1);
                    return parsed.Questions.Count > 0 ? "repaired" : "";
                },
                Finalize = () =>
                {
                    var parsedQuestions = QuizOutputParser.PrepareDisplayQuestions(
                        QuizOutputParser.DeduplicateQuestions(questions).Take(MaxQuestionCount).ToList());
                    return Task.FromResult<object?>(parsedQuestions.Count > 0 ? parsedQuestions : null);
                },
                DiagnosticTag = _ => "quiz"
            },
            progress, ct);

        if (result is null || result.Count == 0)
            return null;

        return new QuizDocument
        {
            Title = ResolveQuizTitle(normalizedNoteTitle, noteText),
            Questions = result,
            SourceNoteId = sourceNoteId
        };
    }

    private static int CalculateTargetChunkQuestionCount(string sourceNote, int chunkCount)
    {
        var wordCount = CountWords(sourceNote);
        if (wordCount < 70) return 5;
        if (wordCount < 140) return 7;
        if (chunkCount <= 1) return wordCount < 260 ? 10 : 13;
        return wordCount < 180 ? 7 : 10;
    }

    // Turns the single target into a tight band the grammar can enforce as a hard {min,max} cap -
    // see OutputGrammars.QuizGrammar's comment and FlashcardConversionService's equivalent.
    private static (int Min, int Max) CalculateTargetChunkQuestionCountRange(string sourceNote, int chunkCount)
    {
        var target = CalculateTargetChunkQuestionCount(sourceNote, chunkCount);
        return (Math.Max(3, target - 2), target + 1);
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}").Count;
    }

    private static string BuildQuestionTypePlan(int targetQuestionCount)
    {
        var plan = new List<string>();
        for (var i = 1; i <= targetQuestionCount; i++)
        {
            var type = i switch
            {
                1 => "single", 2 => "truefalse", 3 => "multi",
                4 => "single", 5 => "truefalse", 6 => "multi",
                7 => "single", 8 => "truefalse",
                _ => i % 3 == 0 ? "multi" : i % 2 == 0 ? "truefalse" : "single"
            };
            plan.Add($"Q{i}: {type}");
        }
        return string.Join(", ", plan);
    }

    private static string BuildPrompt(string noteTitle, string sourceNote, int minCount, int maxCount, int chunkIndex = 1, int chunkCount = 1)
    {
        var displayTitle = string.IsNullOrWhiteSpace(noteTitle) ? "(blank)" : noteTitle;
        var typePlan = BuildQuestionTypePlan(maxCount);
        var section = chunkCount > 1
            ? $"Section {chunkIndex} of {chunkCount}. "
            : "";

        return $"""
{section}Generate between {minCount} and {maxCount} quiz questions from the note below - cover
the key points, don't pad with minor or repeated details.

Aim for this mix of types, but if a fact doesn't fit the suggested type, pick whichever type DOES
fit instead - correctness matters more than following the mix exactly: {typePlan}

- single/multi (use correct:/wrong: lines): for what/when/who/which/how questions needing a
  specific answer picked from options. Give 2-3 wrong options per question, and for multi give at
  least 2 correct options.
- truefalse (use answer: true/false): ONLY for a plain factual STATEMENT that is true or false,
  e.g. "Marie Curie was born in Warsaw." Never phrase a truefalse item as a question starting with
  what/when/who/which/how - if a fact doesn't reduce to a clean true/false statement, use single
  or multi for it instead.

Use the note title as quiz title.

q: question text
type: single
correct: correct answer
wrong: wrong answer
wrong: another wrong answer
explanation: short explanation

Title: {displayTitle}

Note:
{sourceNote}
""";
    }

    private static string BuildRepairPrompt(string noteTitle, string sourceNote, int minCount, int maxCount)
    {
        var displayTitle = string.IsNullOrWhiteSpace(noteTitle) ? "(blank)" : noteTitle;
        var typePlan = BuildQuestionTypePlan(maxCount);

        return $"""
Repair quiz output. Generate between {minCount} and {maxCount} questions.

Aim for this mix of types, but if a fact doesn't fit the suggested type, pick whichever type DOES
fit instead - correctness matters more than following the mix exactly: {typePlan}

- single/multi (use correct:/wrong: lines): for what/when/who/which/how questions needing a
  specific answer picked from options. Give 2-3 wrong options per question, and for multi give at
  least 2 correct options.
- truefalse (use answer: true/false): ONLY for a plain factual STATEMENT that is true or false,
  e.g. "Marie Curie was born in Warsaw." Never phrase a truefalse item as a question starting with
  what/when/who/which/how - if a fact doesn't reduce to a clean true/false statement, use single
  or multi for it instead.

Use the note title as quiz title.

q: question text
type: single
correct: correct answer
wrong: wrong answer
wrong: another wrong answer
explanation: short explanation

Title: {displayTitle}

Note:
{sourceNote}
""";
    }

    private static string ResolveQuizTitle(string noteTitle, string noteText)
    {
        if (!IsGenericNoteTitle(noteTitle))
            return QuizOutputParser.NormalizeTitle(noteTitle);

        var fallback = Regex
            .Split(noteText.Replace("\r", "\n", StringComparison.Ordinal), @"\n+|(?<=[.!?])\s+")
            .Select(QuizOutputParser.NormalizeTitle)
            .FirstOrDefault(title => title.Length >= 5);

        return string.IsNullOrWhiteSpace(fallback)
            ? LocalizationService.GetString("QuizUntitled")
            : fallback;
    }

    private static bool IsGenericNoteTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var normalized = QuizOutputParser.NormalizeKey(title);
        string[] genericTitles =
        [
            "new note",
            "naujas uzrasas",
            "naujas užrašas",
            "untitled",
            "be pavadinimo"
        ];

        return genericTitles.Any(generic => string.Equals(normalized, QuizOutputParser.NormalizeKey(generic), StringComparison.OrdinalIgnoreCase));
    }
}
