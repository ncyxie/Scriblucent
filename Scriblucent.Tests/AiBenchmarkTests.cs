using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scriblucent.Tests;

[Collection(AiGenerationTraceCollection.DiagnosticsCollectionName)]
public sealed class AiBenchmarkTests
{
    private const string EnableVariable = "SCRIBLUCENT_RUN_AI_BENCHMARK";
    private static readonly string[] ModelKeys =
    [
        "Qwen3.5-0.8B",
        "Qwen3.5-2B",
        "Qwen3.5-4B",
        "Qwen3.5-9B"
    ];

    [EnvironmentFact(EnableVariable)]
    [Trait("Category", "AiBenchmark")]
    public async Task BundledModels_QualityAndPerformanceBenchmark()
    {
        var cases = LoadCases();
        var modelKeys = ResolveModelKeys();
        var repetitions = ResolveRepetitions();
        var includeMindMaps = !string.Equals(
            Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_BENCHMARK_QUESTIONS_ONLY"),
            "1",
            StringComparison.Ordinal);
        var results = new List<BenchmarkRunResult>();
        var modelLoads = new List<ModelLoadResult>();

        foreach (var modelKey in modelKeys)
        {
            Assert.True(BundledModelHostService.IsModelDownloaded(modelKey),
                $"Install {modelKey} before running the AI benchmark.");

            using var inner = new LLamaSharpWorkerAdapter(useGpuAccelerationOverride: true);
            using var backend = new BenchmarkBackend(inner);
            var loadTimer = Stopwatch.StartNew();
            await backend.CountTokensAsync("Scriblucent Vulkan benchmark warmup.", modelKey);
            loadTimer.Stop();
            Assert.Equal("vulkan", inner.ActiveBackendName);
            modelLoads.Add(new ModelLoadResult(modelKey, inner.ActiveBackendName, loadTimer.Elapsed.TotalMilliseconds));

            foreach (var benchmarkCase in cases)
            {
                var sourcePath = Path.Combine(AppContext.BaseDirectory, "TestData", "AI", benchmarkCase.SourceFile);
                var source = await File.ReadAllTextAsync(sourcePath);
                for (var repetition = 1; repetition <= repetitions; repetition++)
                {
                    results.Add(await RunOnceAsync(
                        backend,
                        modelKey,
                        benchmarkCase,
                        source,
                        repetition,
                        includeMindMaps,
                        questionPolicyOverride: null));
                }
            }
        }

        var reportDirectory = CreateReportDirectory();
        var report = new BenchmarkReport(
            DateTimeOffset.UtcNow,
            includeMindMaps,
            modelLoads,
            results);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            Path.Combine(reportDirectory, "results.json"),
            JsonSerializer.Serialize(report, jsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(reportDirectory, "report.md"),
            BuildMarkdown(report, cases));

        var failures = ValidateResults(results);
        Assert.True(failures.Count == 0,
            $"Benchmark contract failures (report: {reportDirectory}):\n{string.Join("\n", failures)}");
    }

    private static async Task<BenchmarkRunResult> RunOnceAsync(
        BenchmarkBackend backend,
        string modelKey,
        BenchmarkCase benchmarkCase,
        string source,
        int repetition,
        bool includeMindMaps,
        NaturalQuestionPolicyTier? questionPolicyOverride)
    {
        var coordinator = new StudyPlanCoordinator(backend, new StudyPlanCache());
        var plannerStart = backend.CallCount;
        var plannerTimer = Stopwatch.StartNew();
        var coverageMode = ResolveCoverageMode(benchmarkCase.Key);
        var context = await coordinator.GetOrCreateAsync(
            source,
            modelKey,
            coverageMode);
        plannerTimer.Stop();
        var plannerCalls = backend.SummarizeCalls(plannerStart);

        var expectedQuestionCalls = AiModelCapabilities.Resolve(modelKey).NaturalQuestionTier
                                    == NaturalQuestionPolicyTier.ClozeOnly
            ? 0
            : context.Plan.ContextPackets.Count;
        var expectedMindMapCalls = includeMindMaps
            ? MindMapConversionService.ExpectedGroupingCallCount(context)
            : 0;

        var flashProgress = new List<AiGenerationProgress>();
        var questionPlanner = new GroundedQuestionPlanner(questionPolicyOverride);
        var flashStart = backend.CallCount;
        var flashTimer = Stopwatch.StartNew();
        var cards = await new FlashcardConversionService(backend, questionPlanner).ConvertToFlashcardsAsync(
            context,
            new SyncProgress<AiGenerationProgress>(flashProgress.Add));
        flashTimer.Stop();
        var flashCalls = backend.SummarizeCalls(flashStart);
        var completedQuestionPlan = await questionPlanner.GetOrCreateAsync(context);
        var questionDecisions = completedQuestionPlan.Decisions.Select(decision =>
            new BenchmarkQuestionDecision(
                decision.FactId,
                decision.Kind.ToString(),
                decision.CandidateQuestion,
                decision.RejectionReason?.ToString() ?? string.Empty,
                decision.VerifiedAnswer)).ToList();
        var originById = completedQuestionPlan.Decisions.ToDictionary(
            decision => decision.FactId,
            decision => decision.Kind == GroundedQuestionDecisionKind.AcceptedNatural ? "model" : "fallback",
            StringComparer.OrdinalIgnoreCase);

        var quizProgress = new List<AiGenerationProgress>();
        var quizStart = backend.CallCount;
        var quizTimer = Stopwatch.StartNew();
        var quiz = await new QuizConversionService(backend, questionPlanner).ConvertToQuizAsync(
            benchmarkCase.Title,
            context,
            progress: new SyncProgress<AiGenerationProgress>(quizProgress.Add));
        quizTimer.Stop();
        var quizCalls = backend.SummarizeCalls(quizStart);

        var mindMapProgress = new List<AiGenerationProgress>();
        var mindMapStart = backend.CallCount;
        var mindMapTimer = Stopwatch.StartNew();
        Models.MindMapNode? mindMap = null;
        if (includeMindMaps)
        {
            mindMap = await new MindMapConversionService(backend).ConvertToMindMapAsync(
                benchmarkCase.Title,
                context,
                new SyncProgress<AiGenerationProgress>(mindMapProgress.Add));
        }
        mindMapTimer.Stop();
        var mindMapCalls = backend.SummarizeCalls(mindMapStart);

        var planFacts = context.Plan.Facts.Select(ToBenchmarkFact).ToList();
        var selectedFacts = context.SelectedFacts.Select(ToBenchmarkFact).ToList();
        var cardResults = cards.Select((card, index) => new BenchmarkFlashcard(
            context.SelectedFacts[index].Id,
            originById.GetValueOrDefault(context.SelectedFacts[index].Id, "fallback"),
            card.Question,
            card.Answer,
            context.SelectedFacts[index].Evidence,
            card.SetIndex)).ToList();
        var quizResults = (quiz?.Questions ?? []).Select((question, index) => new BenchmarkQuizQuestion(
            context.SelectedFacts[index].Id,
            originById.GetValueOrDefault(context.SelectedFacts[index].Id, "fallback"),
            question.Type.ToString(),
            question.Question,
            question.Type == QuizQuestionType.PlainText
                ? [question.PlainTextAnswer]
                : question.Options.Where(option => option.IsCorrect).Select(option => option.Text).ToList(),
            question.Options.Where(option => !option.IsCorrect).Select(option => option.Text).ToList(),
            question.Explanation,
            context.SelectedFacts[index].Evidence,
            question.SetIndex)).ToList();
        var mindMapPaths = mindMap is null ? [] : FlattenMindMap(mindMap).ToList();
        var rawModelOutputs = backend.OutputsSince(plannerStart);

        var inventoryAnswers = context.Plan.Facts.Select(fact => fact.Answer).ToList();
        var selectedAnswers = context.SelectedFacts.Select(fact => fact.Answer).ToList();
        var inventoryMisses = FindMissingTargets(benchmarkCase.ExpectedTargets, inventoryAnswers);
        var selectedMisses = FindMissingTargets(benchmarkCase.ExpectedTargets, selectedAnswers);

        return new BenchmarkRunResult(
            modelKey,
            backend.ActiveBackendName,
            benchmarkCase.Key,
            benchmarkCase.Title,
            benchmarkCase.Language,
            coverageMode.ToString(),
            repetition,
            source,
            expectedQuestionCalls,
            expectedMindMapCalls,
            plannerTimer.Elapsed.TotalMilliseconds,
            flashTimer.Elapsed.TotalMilliseconds,
            quizTimer.Elapsed.TotalMilliseconds,
            mindMapTimer.Elapsed.TotalMilliseconds,
            plannerCalls,
            flashCalls,
            quizCalls,
            mindMapCalls,
            ReadCompletion(flashProgress),
            ReadCompletion(quizProgress),
            includeMindMaps
                ? ReadCompletion(mindMapProgress)
                : new BenchmarkCompletion(0, 0, 0, 0, "skipped"),
            includeMindMaps,
            benchmarkCase.ExpectedTargets.Count,
            inventoryMisses,
            selectedMisses,
            planFacts,
            selectedFacts,
            questionDecisions,
            rawModelOutputs,
            cardResults,
            quizResults,
            mindMapPaths);
    }

    private static List<string> ValidateResults(IReadOnlyList<BenchmarkRunResult> results)
    {
        var failures = new List<string>();
        foreach (var run in results)
        {
            var prefix = $"{run.ModelKey}/{run.CaseKey}/run-{run.Repetition}";
            if (!string.Equals(run.ActiveBackend, "vulkan", StringComparison.Ordinal))
                failures.Add($"{prefix}: backend changed to {run.ActiveBackend} during the run");
            if (run.FlashCalls.Count != 0)
            {
                failures.Add($"{prefix}: flashcard conversion made {run.FlashCalls.Count} unexpected model calls");
            }
            var capabilities = AiModelCapabilities.Resolve(run.ModelKey);
            var clozeOnly = capabilities.NaturalQuestionTier == NaturalQuestionPolicyTier.ClozeOnly;
            var groundingBatches = (int)Math.Ceiling(
                capabilities.ContextPacketTargetLimit / (double)capabilities.GroundingBatchSize);
            var metadataCalls = !capabilities.UsesSemanticMetadata
                ? 0
                : run.ExpectedQuestionCalls;
            var expectedPlannerCalls = clozeOnly
                ? 0
                : run.ExpectedQuestionCalls * (1 + (2 * groundingBatches)) + metadataCalls;
            if (run.PlannerCalls.Count < (clozeOnly ? 0 : run.ExpectedQuestionCalls)
                || run.PlannerCalls.Count > Math.Max(expectedPlannerCalls, run.ExpectedQuestionCalls * 3))
            {
                failures.Add(
                    $"{prefix}: planner calls {run.PlannerCalls.Count}, expected " +
                    $"{(clozeOnly ? 0 : run.ExpectedQuestionCalls)}..{expectedPlannerCalls}");
            }
            var maximumQuizCalls = clozeOnly ? 0
                : (run.SelectedFacts.Count + QuizConversionService.MaximumDistractorBatchSize - 1)
                  / QuizConversionService.MaximumDistractorBatchSize;
            if (run.QuizCalls.Count > maximumQuizCalls)
                failures.Add($"{prefix}: quiz distractor calls {run.QuizCalls.Count}, maximum {maximumQuizCalls}");
            var maximumMindMapCalls = run.ExpectedMindMapCalls
                * (AiModelCapabilities.Resolve(run.ModelKey).QuestionStrategy == GroundedQuestionStrategy.Joint ? 2 : 1);
            if (run.MindMapCalls.Count < run.ExpectedMindMapCalls
                || run.MindMapCalls.Count > maximumMindMapCalls)
                failures.Add($"{prefix}: mind-map calls {run.MindMapCalls.Count}, expected " +
                    $"{run.ExpectedMindMapCalls}..{maximumMindMapCalls}");
            ValidateCompletion(prefix, "flash", run.FlashCompletion, run.SelectedFacts.Count, failures);
            ValidateCompletion(prefix, "quiz", run.QuizCompletion, run.SelectedFacts.Count, failures);
            if (run.IncludesMindMap)
                ValidateCompletion(prefix, "mind-map", run.MindMapCompletion, run.SelectedFacts.Count, failures);
            if (run.Flashcards.Count != run.SelectedFacts.Count)
                failures.Add($"{prefix}: flashcard count {run.Flashcards.Count}, selected {run.SelectedFacts.Count}");
            if (run.QuizQuestions.Count != run.SelectedFacts.Count)
                failures.Add($"{prefix}: quiz count {run.QuizQuestions.Count}, selected {run.SelectedFacts.Count}");
            if (run.QuestionDecisions.Count != run.SelectedFacts.Count)
                failures.Add($"{prefix}: question decisions {run.QuestionDecisions.Count}, selected {run.SelectedFacts.Count}");
            var viableUnits = StudyPlanCoordinator.SplitSource(run.Source)
                .Where(unit => GroundedClozeBuilder.IsViableSourceUnit(unit.Text))
                .Select(unit => unit.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coveredUnits = run.SelectedFacts
                .Select(fact => fact.SourceUnitId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var uncoveredUnits = viableUnits.Where(unit => !coveredUnits.Contains(unit)).ToList();
            if (uncoveredUnits.Count > 0)
            {
                failures.Add(
                    $"{prefix}: source-unit coverage {coveredUnits.Count}/{viableUnits.Count}; " +
                    $"missing {string.Join(", ", uncoveredUnits)}");
            }
            var selectedById = run.SelectedFacts.ToDictionary(fact => fact.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var decision in run.QuestionDecisions.Where(decision =>
                         decision.Kind == nameof(GroundedQuestionDecisionKind.AcceptedNatural)))
            {
                if (!selectedById.TryGetValue(decision.FactId, out var fact)
                    || !string.Equals(Normalize(decision.VerifiedAnswer), Normalize(fact.Answer), StringComparison.Ordinal))
                {
                    failures.Add($"{prefix}: accepted natural question {decision.FactId} changed its planned answer");
                }
            }
            foreach (var card in run.Flashcards)
            {
                if (!selectedById.TryGetValue(card.FactId, out var fact))
                    continue;
                if (string.Equals(card.Origin, "model", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Normalize(card.Answer), Normalize(fact.Answer), StringComparison.Ordinal))
                        failures.Add($"{prefix}: flashcard {card.FactId} changed its planned answer");
                }
                else if (!ContainsNormalized(Normalize(fact.Evidence), card.Answer)
                         && !ContainsNormalized(Normalize(fact.Answer), card.Answer))
                {
                    failures.Add($"{prefix}: cloze answer is not in the planned evidence: {card.Answer}");
                }
            }
            if (clozeOnly
                && run.QuestionDecisions.Any(decision => decision.Kind == nameof(GroundedQuestionDecisionKind.AcceptedNatural)))
            {
                failures.Add($"{prefix}: 0.8B produced a natural question despite its cloze-only policy");
            }
            var minimumNaturalRatio = capabilities.QuestionStrategy == GroundedQuestionStrategy.Joint
                ? 0.60
                : 0.0;
            if (minimumNaturalRatio > 0)
            {
                var naturalCount = run.Flashcards.Count(card =>
                    string.Equals(card.Origin, "model", StringComparison.OrdinalIgnoreCase));
                var naturalRatio = naturalCount / (double)Math.Max(1, run.Flashcards.Count);
                if (naturalRatio + 0.0001 < minimumNaturalRatio)
                {
                    failures.Add(
                        $"{prefix}: natural-question ratio {naturalCount}/{run.Flashcards.Count} " +
                        $"({naturalRatio:P0}), expected at least {minimumNaturalRatio:P0}");
                }
            }
            if (run.RawModelOutputs.Any(ContainsLeakedModelControlToken))
                failures.Add($"{prefix}: raw output leaked model control or thought-channel tokens");
            if (run.Flashcards.Any(card => card.SetIndex != 1))
                failures.Add($"{prefix}: generated flashcards were split across internal sets");
            if (run.QuizQuestions.Any(question => question.SetIndex != 1))
                failures.Add($"{prefix}: generated quiz questions were split across internal sets");

            var sourceKey = Normalize(run.Source);
            foreach (var card in run.Flashcards.Where(card => !ContainsNormalized(sourceKey, card.Answer)))
                failures.Add($"{prefix}: flash answer is not a source span: {card.Answer}");
            foreach (var answer in run.QuizQuestions
                         .SelectMany(question => question.CorrectAnswers)
                         .Where(answer => !ContainsNormalized(sourceKey, answer)))
            {
                failures.Add($"{prefix}: quiz answer is not a source span: {answer}");
            }
            foreach (var question in run.QuizQuestions)
            {
                var correctKeys = question.CorrectAnswers.Select(Normalize)
                    .ToHashSet(StringComparer.Ordinal);
                var wrongKeys = question.WrongAnswers.Select(Normalize).ToArray();
                if (wrongKeys.Any(correctKeys.Contains) || wrongKeys.Distinct(StringComparer.Ordinal).Count() != wrongKeys.Length)
                    failures.Add($"{prefix}: quiz contains a duplicate or correct-answer distractor: {question.FactId}");
            }
            var mapKeys = run.MindMapPaths.Select(Normalize).ToList();
            foreach (var answer in run.IncludesMindMap
                         ? run.SelectedFacts
                             .Select(fact => fact.Answer)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(answer => !MapRepresentsAnswer(mapKeys, answer))
                         : [])
            {
                failures.Add($"{prefix}: mind map omitted selected answer: {answer}");
            }
            ValidateStrangerAudit(prefix, run, failures);
        }

        foreach (var modelRuns in results
                     .Where(run => AiModelCapabilities.Resolve(run.ModelKey).QuestionStrategy
                                   == GroundedQuestionStrategy.Joint)
                     .GroupBy(run => run.ModelKey, StringComparer.OrdinalIgnoreCase))
        {
            var hasChoiceOpportunity = modelRuns.Any(run => run.SelectedFacts
                .Where(fact => fact.AnswerKind is nameof(StudyAnswerKind.Date)
                    or nameof(StudyAnswerKind.Person)
                    or nameof(StudyAnswerKind.Place)
                    or nameof(StudyAnswerKind.Organization))
                .GroupBy(
                    fact => $"{fact.AnswerKind}:{(fact.AnswerKind == nameof(StudyAnswerKind.Date) ? fact.ValueShape : string.Empty)}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() >= 3));
            if (hasChoiceOpportunity
                && !modelRuns.SelectMany(run => run.QuizQuestions)
                    .Any(question => question.Type != nameof(QuizQuestionType.PlainText)))
            {
                failures.Add($"{modelRuns.Key}: grounded quiz matrix produced no safe choice questions");
            }
        }

        return failures;
    }

    private static bool MapRepresentsAnswer(IReadOnlyList<string> paths, string answer)
    {
        var normalized = Normalize(answer);
        var answerWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (answerWords.Length <= 6)
            return paths.Any(path => ContainsNormalized(path, answer));

        var displayedWords = paths.SelectMany(path => path.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return answerWords.Distinct(StringComparer.Ordinal)
            .Count(displayedWords.Contains) >= Math.Ceiling(answerWords.Distinct(StringComparer.Ordinal).Count() * 0.6);
    }

    private static void ValidateStrangerAudit(
        string prefix,
        BenchmarkRunResult run,
        ICollection<string> failures)
    {
        var perUnit = run.SelectedFacts
            .GroupBy(fact => fact.SourceUnitId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        var attemptCap = string.Equals(run.CoverageMode, nameof(StudyCoverageMode.Thorough), StringComparison.Ordinal)
                         || (string.Equals(run.CoverageMode, nameof(StudyCoverageMode.Auto), StringComparison.Ordinal)
                             && AiModelCapabilities.Resolve(run.ModelKey).NaturalQuestionTier
                                != NaturalQuestionPolicyTier.ClozeOnly)
            ? 2
            : 1;
        if (perUnit > attemptCap)
        {
            failures.Add(
                $"{prefix}: {run.CoverageMode} selected {perUnit} facts from one source unit, expected at most {attemptCap}");
        }

        var factsById = run.SelectedFacts.ToDictionary(fact => fact.Id, StringComparer.Ordinal);
        foreach (var card in run.Flashcards)
        {
            if (Regex.IsMatch(card.Question, @"\[[^\]]+\]"))
                failures.Add($"{prefix}: placeholder in question {card.FactId}: {card.Question}");
            if (Regex.IsMatch(card.Question, @"\S_____") || Regex.IsMatch(card.Question, @"_____[^\s.,;:!?]"))
                failures.Add($"{prefix}: glued cloze blank in {card.FactId}: {card.Question}");
            if (!string.Equals(card.Origin, "model", StringComparison.OrdinalIgnoreCase)
                || !factsById.TryGetValue(card.FactId, out var fact))
                continue;

            var seed = GroundedClozeBuilder.CreateSeed(ToStudyFact(fact));
            if (!GroundedQuestionValidator.TryValidate(seed, card.Question, out var reason))
            {
                failures.Add(
                    $"{prefix}: natural question failed current-unit overlap for {card.FactId} ({reason}): {card.Question}");
            }
        }

    }

    private static bool ContainsLeakedModelControlToken(string output)
        => Regex.IsMatch(
            output,
            @"(?i)<\|(?:turn|channel|think|tool_call|im_end)\b|<(?:turn|channel)\|>");

    private static void ValidateCompletion(
        string prefix,
        string artifact,
        BenchmarkCompletion completion,
        int expected,
        ICollection<string> failures)
    {
        if (completion.ModelGenerated + completion.Fallback != expected)
            failures.Add($"{prefix}: {artifact} completion {completion.ModelGenerated}+{completion.Fallback}, expected {expected}");
        if (completion.SuccessfulPackets != completion.TotalPackets)
            failures.Add($"{prefix}: {artifact} packet coverage {completion.SuccessfulPackets}/{completion.TotalPackets}");
    }

    private static BenchmarkCompletion ReadCompletion(IReadOnlyList<AiGenerationProgress> reports)
    {
        var completion = reports.Single(report => report.ModelGeneratedItemCount.HasValue);
        return new BenchmarkCompletion(
            completion.ModelGeneratedItemCount.GetValueOrDefault(),
            completion.FallbackItemCount.GetValueOrDefault(),
            completion.SuccessfulPacketCount.GetValueOrDefault(),
            completion.TotalPacketCount.GetValueOrDefault(),
            completion.GenerationDiagnostics ?? string.Empty);
    }

    private static BenchmarkFact ToBenchmarkFact(StudyFact fact) => new(
        fact.Id,
        fact.SourceUnitId,
        fact.Importance.ToString(),
        fact.AnswerKind.ToString(),
        fact.ValueShape.ToString(),
        fact.IsPrimaryCandidate,
        fact.IsExplicitList,
        fact.AnswerItems ?? [],
        fact.Answer,
        fact.Evidence,
        fact.QuestionContext);

    private static StudyFact ToStudyFact(BenchmarkFact fact)
        => new(
            fact.Id,
            fact.SourceUnitId,
            fact.Evidence,
            fact.Answer,
            Enum.Parse<StudyFactImportance>(fact.Importance),
            0,
            Enum.Parse<StudyAnswerKind>(fact.AnswerKind),
            fact.QuestionContext,
            IsExplicitList: fact.IsExplicitList,
            AnswerItems: fact.AnswerItems);

    private static StudyCoverageMode ResolveCoverageMode(string caseKey)
        => string.Equals(caseKey, "civic-leader-bio-lt", StringComparison.Ordinal)
            ? StudyCoverageMode.Thorough
            : StudyCoverageMode.Auto;

    private static int ParseDiagnosticCount(string diagnostics, string key)
    {
        var match = Regex.Match(diagnostics ?? string.Empty, $@"\b{Regex.Escape(key)}=(?<count>\d+)\b");
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
    }

    private static IEnumerable<string> FlattenMindMap(MindMapNode root)
    {
        return Visit(root, []);

        static IEnumerable<string> Visit(MindMapNode node, IReadOnlyList<string> parents)
        {
            var path = parents.Append(node.Text).ToList();
            if (node.Children.Count == 0)
                yield return string.Join(" > ", path);
            foreach (var child in node.Children)
            foreach (var childPath in Visit(child, path))
                yield return childPath;
        }
    }

    private static List<string> FindMissingTargets(
        IReadOnlyList<BenchmarkTarget> targets,
        IReadOnlyList<string> generatedAnswers)
        => targets
            .Where(target => !target.Answers.Any(expected => generatedAnswers.Any(actual =>
                ContainsEitherDirection(actual, expected))))
            .Select(target => target.Label)
            .ToList();

    private static bool ContainsEitherDirection(string left, string right)
    {
        var leftKey = Normalize(left);
        var rightKey = Normalize(right);
        return ContainsNormalized(leftKey, rightKey) || ContainsNormalized(rightKey, leftKey);
    }

    private static bool ContainsNormalized(string normalizedText, string value)
    {
        var valueKey = Normalize(value);
        return valueKey.Length > 0
            && $" {normalizedText} ".Contains($" {valueKey} ", StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<BenchmarkCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "AI", "benchmark-cases.json");
        var cases = JsonSerializer.Deserialize<List<BenchmarkCase>>(
                        File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("AI benchmark cases could not be loaded.");
        var configured = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_BENCHMARK_CASES");
        if (string.IsNullOrWhiteSpace(configured))
            return cases;

        var requested = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var selected = cases.Where(item => requested.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("SCRIBLUCENT_AI_BENCHMARK_CASES did not name a benchmark corpus.");
        return selected;
    }

    private static IReadOnlyList<string> ResolveModelKeys()
    {
        var available = ModelKeys.ToList();
        var configured = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_BENCHMARK_MODELS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var installed = available
                .Where(BundledModelHostService.IsModelDownloaded)
                .ToList();
            return installed.Count == 0 ? available : installed;
        }

        var requested = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var selected = available.Where(key => requested.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("SCRIBLUCENT_AI_BENCHMARK_MODELS did not name a bundled benchmark model.");
        return selected;
    }

    private static int ResolveRepetitions()
        => int.TryParse(Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_BENCHMARK_REPETITIONS"), out var value)
            ? Math.Clamp(value, 1, 3)
            : 3;

    private static string CreateReportDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_BENCHMARK_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "ai-benchmark"))
            : Path.GetFullPath(configured);
        var directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildMarkdown(BenchmarkReport report, IReadOnlyList<BenchmarkCase> cases)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Scriblucent AI Benchmark");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.TimestampUtc:O}");
        builder.AppendLine($"Mind maps: {(report.IncludesMindMaps ? "included" : "skipped (question-only run)")}");
        builder.AppendLine();
        builder.AppendLine("## Model Load");
        builder.AppendLine();
        builder.AppendLine("| Model | Backend | Cold load (ms) |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var load in report.ModelLoads)
            builder.AppendLine($"| {load.ModelKey} | {load.Backend} | {load.ColdLoadMilliseconds:F0} |");

        builder.AppendLine();
        builder.AppendLine("## Median Timing And Coverage");
        builder.AppendLine();
        builder.AppendLine("| Model | Corpus | Plan ms | Flash ms | Quiz ms | Map ms | Inventory targets | Selected targets | Quiz types | Flash model/fallback | Quiz model/fallback | Map model/fallback |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|");
        foreach (var group in report.Runs.GroupBy(run => (run.ModelKey, run.CaseKey)))
        {
            var first = group.OrderBy(run => run.Repetition).First();
            var inventoryHit = first.ExpectedTargetCount - first.MissingInventoryTargets.Count;
            var selectedHit = first.ExpectedTargetCount - first.MissingSelectedTargets.Count;
            builder.AppendLine(
                $"| {group.Key.ModelKey} | {group.Key.CaseKey} | {Median(group.Select(run => run.PlannerWallMilliseconds)):F0} | " +
                $"{Median(group.Select(run => run.FlashWallMilliseconds)):F0} | {Median(group.Select(run => run.QuizWallMilliseconds)):F0} | " +
                $"{Median(group.Select(run => run.MindMapWallMilliseconds)):F0} | {inventoryHit}/{first.ExpectedTargetCount} | " +
                $"{selectedHit}/{first.ExpectedTargetCount} | {FormatQuestionTypes(first.QuizQuestions)} | " +
                $"{first.FlashCompletion.ModelGenerated}/{first.FlashCompletion.Fallback} | " +
                $"{first.QuizCompletion.ModelGenerated}/{first.QuizCompletion.Fallback} | " +
                $"{first.MindMapCompletion.ModelGenerated}/{first.MindMapCompletion.Fallback} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Timing Detail");
        builder.AppendLine();
        builder.AppendLine("| Model | Corpus | Phase | Min ms | Median ms | Max ms | Median inference ms | Chars/s | Calls |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in report.Runs.GroupBy(run => (run.ModelKey, run.CaseKey)))
        {
            AppendPhase("planner", group.Select(run => run.PlannerWallMilliseconds), group.Select(run => run.PlannerCalls));
            AppendPhase("flash", group.Select(run => run.FlashWallMilliseconds), group.Select(run => run.FlashCalls));
            AppendPhase("quiz", group.Select(run => run.QuizWallMilliseconds), group.Select(run => run.QuizCalls));
            AppendPhase("mind-map", group.Select(run => run.MindMapWallMilliseconds), group.Select(run => run.MindMapCalls));

            void AppendPhase(string phase, IEnumerable<double> walls, IEnumerable<BenchmarkCallSummary> calls)
            {
                var wallValues = walls.Order().ToList();
                var callValues = calls.ToList();
                var charactersPerSecond = Median(callValues.Select(call =>
                    call.InferenceMilliseconds <= 0
                        ? 0
                        : call.GeneratedCharacters * 1000 / call.InferenceMilliseconds));
                builder.AppendLine(
                    $"| {group.Key.ModelKey} | {group.Key.CaseKey} | {phase} | {wallValues.First():F0} | " +
                    $"{Median(wallValues):F0} | {wallValues.Last():F0} | " +
                    $"{Median(callValues.Select(call => call.InferenceMilliseconds)):F0} | {charactersPerSecond:F0} | {callValues[0].Count} |");
            }
        }

        foreach (var model in report.ModelLoads.Select(load => load.ModelKey))
        foreach (var benchmarkCase in cases)
        {
            var run = report.Runs.Single(item =>
                item.ModelKey == model && item.CaseKey == benchmarkCase.Key && item.Repetition == 1);
            builder.AppendLine();
            builder.AppendLine($"## {model}: {benchmarkCase.Key}");
            builder.AppendLine();
            builder.AppendLine($"Source fixture: `{benchmarkCase.SourceFile}`");
            builder.AppendLine($"Backend after run: `{run.ActiveBackend}`");
            builder.AppendLine();
            builder.AppendLine($"Atomic targets missed by inventory: {FormatList(run.MissingInventoryTargets)}");
            builder.AppendLine($"Atomic targets missed by Auto selection: {FormatList(run.MissingSelectedTargets)}");
            builder.AppendLine();
            builder.AppendLine("### Selected Facts");
            builder.AppendLine();
            foreach (var fact in run.SelectedFacts)
                builder.AppendLine($"- `{fact.Id}` [{fact.Importance}/{fact.AnswerKind}/{fact.ValueShape}{(fact.IsPrimaryCandidate ? "/primary" : string.Empty)}{(fact.IsExplicitList ? "/list" : string.Empty)}] **{fact.Answer}** from: {fact.Evidence} | context: {fact.QuestionContext}");
            builder.AppendLine();
            builder.AppendLine("### Flashcards");
            builder.AppendLine($"Diagnostics: `{run.FlashCompletion.Diagnostics}`");
            builder.AppendLine();
            foreach (var card in run.Flashcards)
                builder.AppendLine($"- `{card.FactId}` [{card.Origin}] **Q:** {card.Question} **A:** {card.Answer}");
            builder.AppendLine();
            builder.AppendLine("### Natural Question Decisions");
            builder.AppendLine();
            foreach (var decision in run.QuestionDecisions)
            {
                var reason = string.IsNullOrWhiteSpace(decision.RejectionReason)
                    ? string.Empty
                    : $"/{decision.RejectionReason}";
                var candidate = string.IsNullOrWhiteSpace(decision.CandidateQuestion)
                    ? "(none)"
                    : decision.CandidateQuestion;
                var verifiedAnswer = string.IsNullOrWhiteSpace(decision.VerifiedAnswer)
                    ? string.Empty
                    : $" -> target A: **{decision.VerifiedAnswer}**";
                builder.AppendLine($"- `{decision.FactId}` [{decision.Kind}{reason}] {candidate}{verifiedAnswer}");
            }
            builder.AppendLine();
            builder.AppendLine("### Raw Model Outputs");
            builder.AppendLine();
            foreach (var output in run.RawModelOutputs)
            {
                builder.AppendLine("```text");
                builder.AppendLine(output.Trim());
                builder.AppendLine("```");
            }
            builder.AppendLine();
            builder.AppendLine("### Quiz");
            builder.AppendLine($"Diagnostics: `{run.QuizCompletion.Diagnostics}`");
            builder.AppendLine();
            foreach (var question in run.QuizQuestions)
                builder.AppendLine($"- `{question.FactId}` [{question.Origin}/{question.Type}] **Q:** {question.Question} **Correct:** {string.Join("; ", question.CorrectAnswers)} **Wrong:** {string.Join("; ", question.WrongAnswers)}");
            builder.AppendLine();
            builder.AppendLine("### Mind Map Leaves");
            builder.AppendLine();
            foreach (var path in run.MindMapPaths)
                builder.AppendLine($"- {path}");
        }

        return builder.ToString();
    }

    private static string FormatList(IReadOnlyList<string> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string FormatQuestionTypes(IReadOnlyList<BenchmarkQuizQuestion> questions)
        => string.Join(", ", questions
            .GroupBy(question => question.Type)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"));

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToList();
        if (ordered.Count == 0)
            return 0;
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private sealed class BenchmarkBackend(LLamaSharpWorkerAdapter inner) : ITextGenerationBackend
    {
        private readonly List<AiGenerationResponse> _responses = [];

        public int CallCount => _responses.Count;
        public string ActiveBackendName => inner.ActiveBackendName;

        public async Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request)
        {
            var response = await inner.GenerateAsync(request);
            _responses.Add(response);
            return response;
        }

        public Task<int> CountTokensAsync(string text, string? modelKey, CancellationToken ct = default)
            => inner.CountTokensAsync(text, modelKey, ct);

        public BenchmarkCallSummary SummarizeCalls(int startIndex)
        {
            var calls = _responses.Skip(startIndex).ToList();
            return new BenchmarkCallSummary(
                calls.Count,
                calls.Sum(call => call.Elapsed.TotalMilliseconds),
                calls.Sum(call => call.GeneratedChars));
        }

        public IReadOnlyList<string> OutputsSince(int startIndex)
            => _responses.Skip(startIndex).Select(response => response.Output).ToList();

        public void Dispose()
        {
            // The owner disposes the concrete adapter after this lightweight wrapper.
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record BenchmarkCase(
        string Key,
        string Title,
        string Language,
        string SourceFile,
        IReadOnlyList<BenchmarkTarget> ExpectedTargets);

    private sealed record BenchmarkTarget(string Label, IReadOnlyList<string> Answers);
    private sealed record ModelLoadResult(string ModelKey, string Backend, double ColdLoadMilliseconds);
    private sealed record BenchmarkCallSummary(int Count, double InferenceMilliseconds, int GeneratedCharacters);
    private sealed record BenchmarkCompletion(
        int ModelGenerated,
        int Fallback,
        int SuccessfulPackets,
        int TotalPackets,
        string Diagnostics);
    private sealed record BenchmarkFact(
        string Id,
        string SourceUnitId,
        string Importance,
        string AnswerKind,
        string ValueShape,
        bool IsPrimaryCandidate,
        bool IsExplicitList,
        IReadOnlyList<string> AnswerItems,
        string Answer,
        string Evidence,
        string QuestionContext);
    private sealed record BenchmarkFlashcard(
        string FactId,
        string Origin,
        string Question,
        string Answer,
        string SourceEvidence,
        int SetIndex);
    private sealed record BenchmarkQuestionDecision(
        string FactId,
        string Kind,
        string CandidateQuestion,
        string RejectionReason,
        string VerifiedAnswer);
    private sealed record BenchmarkQuizQuestion(
        string FactId,
        string Origin,
        string Type,
        string Question,
        IReadOnlyList<string> CorrectAnswers,
        IReadOnlyList<string> WrongAnswers,
        string Explanation,
        string SourceEvidence,
        int SetIndex);
    private sealed record BenchmarkRunResult(
        string ModelKey,
        string ActiveBackend,
        string CaseKey,
        string Title,
        string Language,
        string CoverageMode,
        int Repetition,
        [property: System.Text.Json.Serialization.JsonIgnore] string Source,
        int ExpectedQuestionCalls,
        int ExpectedMindMapCalls,
        double PlannerWallMilliseconds,
        double FlashWallMilliseconds,
        double QuizWallMilliseconds,
        double MindMapWallMilliseconds,
        BenchmarkCallSummary PlannerCalls,
        BenchmarkCallSummary FlashCalls,
        BenchmarkCallSummary QuizCalls,
        BenchmarkCallSummary MindMapCalls,
        BenchmarkCompletion FlashCompletion,
        BenchmarkCompletion QuizCompletion,
        BenchmarkCompletion MindMapCompletion,
        bool IncludesMindMap,
        int ExpectedTargetCount,
        IReadOnlyList<string> MissingInventoryTargets,
        IReadOnlyList<string> MissingSelectedTargets,
        IReadOnlyList<BenchmarkFact> PlanFacts,
        IReadOnlyList<BenchmarkFact> SelectedFacts,
        IReadOnlyList<BenchmarkQuestionDecision> QuestionDecisions,
        IReadOnlyList<string> RawModelOutputs,
        IReadOnlyList<BenchmarkFlashcard> Flashcards,
        IReadOnlyList<BenchmarkQuizQuestion> QuizQuestions,
        IReadOnlyList<string> MindMapPaths);
    private sealed record BenchmarkReport(
        DateTimeOffset TimestampUtc,
        bool IncludesMindMaps,
        IReadOnlyList<ModelLoadResult> ModelLoads,
        IReadOnlyList<BenchmarkRunResult> Runs);
}
