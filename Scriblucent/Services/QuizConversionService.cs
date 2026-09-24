using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal sealed class QuizConversionService
{
    private const int GeneratedSetIndex = 1;
    internal const int MaximumDistractorBatchSize = 12;

    private readonly ITextGenerationBackend _backend;
    private readonly GroundedQuestionPlanner _questionPlanner;

    public QuizConversionService(
        ITextGenerationBackend backend,
        GroundedQuestionPlanner? questionPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _questionPlanner = questionPlanner ?? new GroundedQuestionPlanner();
    }

    public async Task<QuizDocument?> ConvertToQuizAsync(
        string noteTitle,
        StudyGenerationContext studyContext,
        Guid? sourceNoteId = null,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(studyContext);
        var normalizedNoteTitle = QuizFormatting.NormalizeTitle(noteTitle);
        var questionPlan = await _questionPlanner.GetOrCreateAsync(studyContext, progress, ct);
        var plannedQuestions = questionPlan.Questions.Select(question =>
            CreateGroundedQuizQuestion(question, studyContext.Plan.Facts)).ToList();
        if (AiModelCapabilities.Resolve(studyContext.ModelKey).NaturalQuestionTier != NaturalQuestionPolicyTier.ClozeOnly)
            await AddModelDistractorsAsync(questionPlan.Questions, plannedQuestions, studyContext, progress, ct);

        return new QuizDocument
        {
            Title = ResolveQuizTitle(normalizedNoteTitle, studyContext.Plan.SourceUnits),
            Questions = QuizFormatting.PrepareDisplayQuestions(plannedQuestions),
            SourceNoteId = sourceNoteId
        };
    }

    private static QuizQuestion CreateGroundedQuizQuestion(
        GroundedPlannedQuestion question,
        IReadOnlyList<StudyFact> distractorFacts)
    {
        var seed = GroundedClozeBuilder.CreateSeed(question.Fact) with { Answer = question.Answer };
        if (RequiresPlainTextAnswer(seed))
            return CreatePlainTextQuestion(question);

        if (TryCreateGroundedMultipleChoice(question, seed, distractorFacts, out var multipleChoice))
            return multipleChoice;

        var wrongAnswers = new List<string>();
        AppendGroundedDistractors(wrongAnswers, seed, distractorFacts);
        return wrongAnswers.Count >= 2
            ? new QuizQuestion
            {
                Type = QuizQuestionType.SingleChoice,
                Question = question.Question,
                Explanation = question.Fact.Evidence,
                SetIndex = GeneratedSetIndex,
                Options = [
                    new QuizOption { Text = question.Answer, IsCorrect = true },
                    .. wrongAnswers.Select(wrong => new QuizOption { Text = wrong, IsCorrect = false })
                ]
            }
            : CreatePlainTextQuestion(question);
    }

    private static bool RequiresPlainTextAnswer(GroundedQuestionSeed seed)
        => seed.AnswerKind == StudyAnswerKind.Quantity
           || seed.ValueShape is StudyValueShape.AtomicNumber or StudyValueShape.OrdinalRole;

    private static QuizQuestion CreatePlainTextQuestion(GroundedPlannedQuestion question)
        => new()
        {
            Type = QuizQuestionType.PlainText,
            Question = question.Question,
            Explanation = question.Fact.Evidence,
            SetIndex = GeneratedSetIndex,
            PlainTextAnswer = question.Answer,
            PlainTextMatchCase = false,
            PlainTextIgnorePunctuation = true
        };

    private static bool TryCreateGroundedMultipleChoice(
        GroundedPlannedQuestion question,
        GroundedQuestionSeed seed,
        IReadOnlyList<StudyFact> distractorFacts,
        out QuizQuestion multipleChoice)
    {
        multipleChoice = null!;
        IReadOnlyList<string> correctAnswers;
        if (seed.Fact.AnswerItems is { Count: >= 2 })
            correctAnswers = seed.Fact.AnswerItems;
        else if (!StudyAnswerList.TrySplit(seed.Answer, out correctAnswers))
            return false;
        if (!HasUsableParallelItems(correctAnswers))
            return false;

        var correctKeys = correctAnswers
            .Select(QuizFormatting.NormalizeKey)
            .ToHashSet(StringComparer.Ordinal);
        var targetEvidenceKey = QuizFormatting.NormalizeKey(seed.Fact.Evidence);
        var wrongAnswers = new List<string>();
        var wrongKeys = new HashSet<string>(StringComparer.Ordinal);
        const int maximumWrongAnswerCount = 1;
        var targetItemWordCount = (int)Math.Round(correctAnswers.Average(CountWords), MidpointRounding.AwayFromZero);

        foreach (var fact in ResolveCompatibleDistractors(
                         seed.Fact,
                         distractorFacts,
                         allowTermList: seed.AnswerKind == StudyAnswerKind.Term)
                     .OrderBy(fact => Math.Abs(
                         CountWords(GroundedClozeBuilder.CreateSeed(fact).Answer) - targetItemWordCount))
                     .ThenBy(fact => fact.SourceOrder))
        {
            var candidateSeed = GroundedClozeBuilder.CreateSeed(fact);
            var candidates = candidateSeed.Fact.AnswerItems is { Count: >= 2 }
                ? candidateSeed.Fact.AnswerItems
                : [candidateSeed.Fact.Answer];
            foreach (var candidate in candidates)
            {
                var candidateKey = QuizFormatting.NormalizeKey(candidate);
                if (!StudyAnswerList.IsUsefulComponent(candidate)
                    || (seed.AnswerKind == StudyAnswerKind.Term
                        && CountWords(candidate) != targetItemWordCount)
                    || correctKeys.Contains(candidateKey)
                    || ContainsWholePhrase(targetEvidenceKey, candidateKey)
                    || !wrongKeys.Add(candidateKey))
                {
                    continue;
                }

                wrongAnswers.Add(candidate);
                if (wrongAnswers.Count >= maximumWrongAnswerCount)
                    break;
            }

            if (wrongAnswers.Count >= maximumWrongAnswerCount)
                break;
        }

        if (wrongAnswers.Count == 0)
            return false;

        multipleChoice = new QuizQuestion
        {
            Type = QuizQuestionType.MultipleChoice,
            Question = question.Question,
            Explanation = question.Fact.Evidence,
            SetIndex = GeneratedSetIndex,
            Options = [
                .. correctAnswers.Select(answer => new QuizOption { Text = answer, IsCorrect = true }),
                .. wrongAnswers.Select(answer => new QuizOption { Text = answer, IsCorrect = false })
            ]
        };
        return true;
    }

    private static void AppendGroundedDistractors(
        ICollection<string> wrongAnswers,
        GroundedQuestionSeed seed,
        IReadOnlyList<StudyFact> distractorFacts)
    {
        var existing = new HashSet<string>(
            wrongAnswers.Select(QuizFormatting.NormalizeKey),
            StringComparer.Ordinal);
        var answerKey = QuizFormatting.NormalizeKey(seed.Answer);
        var evidenceKey = QuizFormatting.NormalizeKey(seed.Fact.Evidence);

        var sameKindSeeds = ResolveCompatibleDistractors(seed.Fact, distractorFacts, allowTermList: true)
            .Select(GroundedClozeBuilder.CreateSeed)
            .Where(candidate => IsCredibleTextDistractor(seed, candidate))
            .OrderBy(candidate => DistractorDistance(seed, candidate))
            .ThenBy(candidate => candidate.Fact.SourceOrder);
        foreach (var other in sameKindSeeds)
        {
            foreach (var candidate in new[] { other.Answer })
            {
                var candidateKey = QuizFormatting.NormalizeKey(candidate);
                if (candidateKey.Length == 0
                    || string.Equals(candidateKey, answerKey, StringComparison.Ordinal)
                    || ContainsWholePhrase(evidenceKey, candidateKey)
                    || !existing.Add(candidateKey))
                {
                    continue;
                }

                wrongAnswers.Add(candidate);
                if (wrongAnswers.Count >= 3)
                    return;
            }
        }
    }

    private static bool IsCredibleTextDistractor(
        GroundedQuestionSeed target,
        GroundedQuestionSeed candidate)
    {
        if (target.AnswerKind == StudyAnswerKind.Date)
            return candidate.AnswerKind == StudyAnswerKind.Date
                   && candidate.ValueShape == target.ValueShape;

        var targetWords = CountWords(target.Answer);
        var candidateWords = CountWords(candidate.Answer);
        if (targetWords == 0 || candidateWords == 0 || Math.Abs(targetWords - candidateWords) > 2)
            return false;

        var ratio = candidate.Answer.Length / (double)Math.Max(1, target.Answer.Length);
        return ratio is >= 0.5 and <= 2.0;
    }

    private async Task AddModelDistractorsAsync(
        IReadOnlyList<GroundedPlannedQuestion> planned,
        List<QuizQuestion> questions,
        StudyGenerationContext context,
        IProgress<AiGenerationProgress>? progress,
        CancellationToken ct)
    {
        var eligible = Enumerable.Range(0, Math.Min(planned.Count, questions.Count))
            .Where(index => questions[index].Type == QuizQuestionType.PlainText
                            && CountWords(planned[index].Answer) is >= 1 and <= 5)
            .ToArray();
        var sourceKey = QuizFormatting.NormalizeKey(string.Join(' ', context.Plan.Facts.Select(fact => fact.Evidence)));
        var answerKeys = context.Plan.Facts.Select(fact => QuizFormatting.NormalizeKey(fact.Answer))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var batch in eligible.Chunk(MaximumDistractorBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AiGenerationProgress(AiGenerationStatus.Processing));
            try
            {
                var response = await _backend.GenerateAsync(new AiGenerationRequest(
                    Prompt: BuildDistractorPrompt(batch.Select(index => planned[index]).ToArray()),
                    MaxTokens: Math.Clamp(64 + 36 * batch.Length, 128, 640),
                    Temperature: 0.25,
                    TopP: 0.8,
                    TopK: 20,
                    CancellationToken: ct,
                    ModelKey: context.ModelKey));
                var byId = batch.ToDictionary(index => planned[index].Fact.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var line in response.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = line.Trim().Split('|', 3, StringSplitOptions.TrimEntries);
                    if (fields.Length != 3 || !byId.TryGetValue(fields[0], out var index))
                        continue;

                    var correct = planned[index].Answer;
                    var correctKey = QuizFormatting.NormalizeKey(correct);
                    var wrong = fields.Skip(1)
                        .Select(field => field.Trim().Trim('"', '\'', '.', ';'))
                        .Where(field => IsSafeModelDistractor(field, correct, correctKey, sourceKey, answerKeys))
                        .DistinctBy(QuizFormatting.NormalizeKey)
                        .ToArray();
                    if (wrong.Length != 2)
                        continue;

                    questions[index] = new QuizQuestion
                    {
                        Type = QuizQuestionType.SingleChoice,
                        Question = planned[index].Question,
                        Explanation = planned[index].Fact.Evidence,
                        SetIndex = GeneratedSetIndex,
                        Options = [
                            new QuizOption { Text = correct, IsCorrect = true },
                            .. wrong.Select(text => new QuizOption { Text = text, IsCorrect = false })
                        ]
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep the grounded plain-text question if distractor generation is unavailable.
            }
        }
    }

    private static string BuildDistractorPrompt(IReadOnlyList<GroundedPlannedQuestion> questions)
        => $"""
Create two plausible but incorrect quiz choices for each question. The CORRECT answer is fixed and must NOT appear in your choices. Match its semantic category, grammar, and language. For example, if the correct answer is a color, give other colors; if a process, give other processes; if a year, give other years. The choices must not also answer the question according to SOURCE. If you cannot make two clearly wrong choices, write NONE.
Return only one line per item: FACT_ID | WRONG_1 | WRONG_2. Do not add explanations.

{string.Join("\n", questions.Select(question => $"{question.Fact.Id} | QUESTION: {question.Question} | CORRECT: {question.Answer} | SOURCE: {question.Fact.Evidence}"))}
""";

    private static bool IsSafeModelDistractor(
        string candidate,
        string correct,
        string correctKey,
        string sourceKey,
        IReadOnlySet<string> answerKeys)
    {
        var key = QuizFormatting.NormalizeKey(candidate);
        var candidateWords = CountWords(candidate);
        var correctWords = CountWords(correct);
        return candidate.Length is >= 2 and <= 60
               && candidateWords is >= 1 and <= 6
               && Math.Abs(candidateWords - correctWords) <= 3
               && candidate.All(character => char.IsLetterOrDigit(character)
                                            || char.IsWhiteSpace(character)
                                            || character is '-' or '’' or '\'')
               && !key.Equals("none", StringComparison.Ordinal)
               && !key.Equals("nėra", StringComparison.Ordinal)
               && !answerKeys.Contains(key)
               && !ContainsWholePhrase(sourceKey, key)
               && !ContainsWholePhrase(key, correctKey)
               && !ContainsWholePhrase(correctKey, key);
    }

    private static double DistractorDistance(
        GroundedQuestionSeed target,
        GroundedQuestionSeed candidate)
    {
        if (target.AnswerKind == StudyAnswerKind.Date
            && TryReadFirstNumber(target.Answer, out var targetNumber)
            && TryReadFirstNumber(candidate.Fact.Answer, out var candidateNumber))
        {
            return Math.Abs(targetNumber - candidateNumber);
        }

        return Math.Abs(target.Answer.Length - candidate.Answer.Length);
    }

    private static bool TryReadFirstNumber(string value, out double number)
    {
        number = 0;
        var match = Regex.Match(value, @"[-+]?\p{N}+(?:[.,]\p{N}+)?");
        return match.Success
               && double.TryParse(
                   match.Value.Replace(',', '.'),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out number);
    }

    private static IEnumerable<StudyFact> ResolveCompatibleDistractors(
        StudyFact target,
        IReadOnlyList<StudyFact> facts,
        bool allowTermList = false)
    {
        if (target.AnswerKind is not (StudyAnswerKind.Date
            or StudyAnswerKind.Person
            or StudyAnswerKind.Place
            or StudyAnswerKind.Organization
            or StudyAnswerKind.Entity)
            && !(allowTermList && target.AnswerKind == StudyAnswerKind.Term))
        {
            return [];
        }

        return facts.Where(candidate =>
            !string.Equals(candidate.Id, target.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.SourceUnitId, target.SourceUnitId, StringComparison.OrdinalIgnoreCase)
            && candidate.AnswerKind == target.AnswerKind
            && (target.AnswerKind != StudyAnswerKind.Date || candidate.ValueShape == target.ValueShape)
            && (target.AnswerKind is not (StudyAnswerKind.Term or StudyAnswerKind.Entity)
                || HasParallelLocalContext(target, candidate)));
    }

    private static bool HasParallelLocalContext(StudyFact target, StudyFact candidate)
    {
        var targetContext = LocalContextStems(target);
        var candidateContext = LocalContextStems(candidate);
        return targetContext.Intersect(candidateContext, StringComparer.Ordinal).Take(2).Count() >= 2;
    }

    private static HashSet<string> LocalContextStems(StudyFact fact)
    {
        var words = Regex.Matches(fact.Evidence, @"[\p{L}\p{Mn}\p{N}]+")
            .Cast<Match>().ToArray();
        var answerStart = fact.AnswerStart >= 0 ? fact.AnswerStart
            : fact.Evidence.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase);
        if (answerStart < 0)
            return [];
        var answerEnd = answerStart + fact.Answer.Length;
        var firstAnswerWord = Array.FindIndex(words, word => word.Index + word.Length > answerStart);
        var lastAnswerWord = Array.FindLastIndex(words, word => word.Index < answerEnd);
        if (firstAnswerWord < 0 || lastAnswerWord < 0)
            return [];

        return words
            .Skip(Math.Max(0, firstAnswerWord - 4))
            .Take(Math.Min(words.Length, lastAnswerWord + 5) - Math.Max(0, firstAnswerWord - 4))
            .Where(word => word.Index + word.Length <= answerStart || word.Index >= answerEnd)
            .Select(word => QuizFormatting.NormalizeKey(word.Value))
            .Where(word => word.Length >= 5)
            .Select(word => word[..5])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasUsableParallelItems(IReadOnlyList<string> items)
    {
        if (items.Count is < 2 or > 4)
            return false;

        foreach (var item in items)
        {
            var tokens = Regex.Matches(item, @"[\p{L}\p{Mn}\p{N}]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();
            if (tokens.Count == 0)
                return false;
            if (tokens.Count >= 2
                && tokens[0].Length <= 3
                && tokens[0].All(character => !char.IsUpper(character)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsWholePhrase(string text, string phrase)
        => phrase.Length > 0
           && $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}").Count;
    }

    private static string ResolveQuizTitle(
        string noteTitle,
        IReadOnlyList<StudySourceUnitSnapshot>? sourceUnits)
    {
        if (!IsGenericNoteTitle(noteTitle))
            return QuizFormatting.NormalizeTitle(noteTitle);

        var fallback = (sourceUnits ?? [])
            .Select(unit => unit.Text)
            .Select(QuizFormatting.NormalizeTitle)
            .FirstOrDefault(title => title.Length >= 5);

        return string.IsNullOrWhiteSpace(fallback)
            ? LocalizationService.GetString("QuizUntitled")
            : fallback;
    }

    private static bool IsGenericNoteTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var normalized = QuizFormatting.NormalizeKey(title);
        string[] genericTitles =
        [
            "new note",
            "naujas uzrasas",
            "naujas užrašas",
            "untitled",
            "be pavadinimo"
        ];

        return genericTitles.Any(generic => string.Equals(normalized, QuizFormatting.NormalizeKey(generic), StringComparison.OrdinalIgnoreCase));
    }
}
