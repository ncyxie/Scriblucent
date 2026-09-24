using System.Text.RegularExpressions;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiRealModelQualityTests
{
    [EnvironmentFact("SCRIBLUCENT_RUN_REAL_MODEL_TESTS")]
    [Trait("Category", "RealModel")]
    public async Task BundledQwen_FactRichCorpus_MeetsGroundingAndCoverageContract()
    {
        var modelKey = Environment.GetEnvironmentVariable("SCRIBLUCENT_REAL_MODEL_KEY")
            ?? "Qwen3.5-0.8B";
        Assert.NotNull(AiModelManifest.Instance.FindByKey(modelKey));
        Assert.True(
            BundledModelHostService.IsModelDownloaded(modelKey),
            $"Install {modelKey} before running the RealModel test category.");

        var corpusPath = Path.Combine(AppContext.BaseDirectory, "TestData", "AI", "fact-rich-biography.txt");
        var source = await File.ReadAllTextAsync(corpusPath);
        using var backend = new RecordingBackend(new LLamaSharpWorkerAdapter());
        var coordinator = new StudyPlanCoordinator(backend, new StudyPlanCache());
        StudyGenerationContext context;
        try
        {
            context = await coordinator.GetOrCreateAsync(source, modelKey, StudyCoverageMode.Auto);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Study planning failed.\n{backend.FormatTranscript()}\n{ex}");
        }
        var planningCallCount = backend.CallCount;
        var clozeOnly = AiModelCapabilities.Resolve(modelKey).NaturalQuestionTier
            == NaturalQuestionPolicyTier.ClozeOnly;
        Assert.InRange(
            planningCallCount,
            clozeOnly ? 0 : context.Plan.ContextPackets.Count,
            clozeOnly ? 0 : context.Plan.ContextPackets.Count * 3);
        var expectedMindMapCalls = MindMapConversionService.ExpectedGroupingCallCount(context);
        var questionPlanner = new GroundedQuestionPlanner();

        IReadOnlyList<Models.FlashcardItem> cards;
        var flashProgress = new List<AiGenerationProgress>();
        try
        {
            cards = await new FlashcardConversionService(backend, questionPlanner).ConvertToFlashcardsAsync(
                context,
                new SyncProgress<AiGenerationProgress>(flashProgress.Add));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Flashcard generation failed for {context.TargetCount} selected facts.\n{backend.FormatTranscript()}\n{ex}");
        }
        var completedQuestionPlan = await questionPlanner.GetOrCreateAsync(context);
        var questionCallCount = backend.CallCount - planningCallCount;
        Assert.Equal(0, questionCallCount);
        var callsAfterQuestions = backend.CallCount;
        Models.QuizDocument? quiz;
        var quizProgress = new List<AiGenerationProgress>();
        try
        {
            quiz = await new QuizConversionService(backend, questionPlanner).ConvertToQuizAsync(
                "Elena Maris",
                context,
                progress: new SyncProgress<AiGenerationProgress>(quizProgress.Add));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Quiz generation failed for {context.TargetCount} selected facts.\n{backend.FormatTranscript()}\n{ex}");
        }
        var quizCallCount = backend.CallCount - callsAfterQuestions;
        var maximumQuizCalls = clozeOnly ? 0
            : (context.TargetCount + QuizConversionService.MaximumDistractorBatchSize - 1)
              / QuizConversionService.MaximumDistractorBatchSize;
        Assert.InRange(quizCallCount, 0, maximumQuizCalls);
        var callsAfterQuiz = backend.CallCount;

        var mindMapProgress = new List<AiGenerationProgress>();
        Models.MindMapNode? mindMap;
        try
        {
            mindMap = await new MindMapConversionService(backend).ConvertToMindMapAsync(
                "Elena Maris",
                context,
                new SyncProgress<AiGenerationProgress>(mindMapProgress.Add));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Mind-map generation failed for {context.TargetCount} selected facts.\n{backend.FormatTranscript()}\n{ex}");
        }
        var maximumMindMapCalls = expectedMindMapCalls
            * (AiModelCapabilities.Resolve(modelKey).QuestionStrategy == GroundedQuestionStrategy.Joint ? 2 : 1);
        Assert.InRange(backend.CallCount,
            callsAfterQuiz + expectedMindMapCalls,
            callsAfterQuiz + maximumMindMapCalls);

        Assert.InRange(context.TargetCount, 10, 60);
        Assert.Equal(context.TargetCount, cards.Count);
        Assert.NotNull(quiz);
        Assert.Equal(context.TargetCount, quiz!.Questions.Count);
        Assert.Equal(cards.Count, quiz.Questions.Count);
        Assert.NotNull(mindMap);

        var minimumModelItems = (int)Math.Ceiling(context.TargetCount * MinimumModelAcceptance(modelKey));
        var qualityDetails = $"{completedQuestionPlan.Diagnostics}\n{backend.FormatTranscript()}";
        AssertGroundedCompletion(flashProgress, context.TargetCount, minimumModelItems, qualityDetails);
        AssertGroundedCompletion(quizProgress, context.TargetCount, minimumModelItems);
        AssertGroundedCompletion(mindMapProgress, context.TargetCount, 0);

        var normalizedSource = Normalize(source);
        Assert.All(cards, card => Assert.Contains(Normalize(card.Answer), normalizedSource, StringComparison.Ordinal));
        Assert.All(quiz.Questions, question =>
        {
            IReadOnlyList<string> correctAnswers = question.Type == Models.QuizQuestionType.PlainText
                ? [question.PlainTextAnswer]
                : question.Options.Where(option => option.IsCorrect).Select(option => option.Text).ToList();
            Assert.NotEmpty(correctAnswers);
            Assert.All(correctAnswers, answer =>
                Assert.Contains(Normalize(answer), normalizedSource, StringComparison.Ordinal));
        });
        var mindMapLabels = Flatten(mindMap!).Select(node => Normalize(node.Text)).ToHashSet();
        var displayedWords = mindMapLabels.SelectMany(label =>
                label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(context.SelectedFacts, fact =>
        {
            var answer = Normalize(fact.Answer);
            var words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (words.Length <= 6)
                Assert.Contains(mindMapLabels, label => label.Contains(answer, StringComparison.Ordinal));
            else
                Assert.True(words.Count(displayedWords.Contains) >= Math.Ceiling(words.Length * 0.6));
        });
    }

    private static string Normalize(string value)
        => string.Join(' ', value
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AssertGroundedCompletion(
        IReadOnlyList<AiGenerationProgress> reports,
        int targetCount,
        int minimumModelItems,
        string? details = null)
    {
        var completion = Assert.Single(reports, report => report.ModelGeneratedItemCount.HasValue);
        Assert.Equal(targetCount,
            completion.ModelGeneratedItemCount.GetValueOrDefault()
            + completion.FallbackItemCount.GetValueOrDefault());
        Assert.True(
            completion.ModelGeneratedItemCount >= minimumModelItems,
            $"Only {completion.ModelGeneratedItemCount}/{targetCount} items used a natural source-grounded question; expected at least {minimumModelItems}.\n{details}");
        Assert.Equal(completion.TotalPacketCount, completion.SuccessfulPacketCount);
    }

    private static double MinimumModelAcceptance(string modelKey)
        => modelKey.Contains("0.8B", StringComparison.OrdinalIgnoreCase) ? 0
            : modelKey.Contains("2B", StringComparison.OrdinalIgnoreCase) ? 0.35
            : 0.55;

    private static int ParseDiagnosticCount(string diagnostics, string key)
    {
        var match = Regex.Match(diagnostics ?? string.Empty, $@"\b{Regex.Escape(key)}=(?<count>\d+)\b");
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
    }

    private static IEnumerable<Models.MindMapNode> Flatten(Models.MindMapNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Flatten(child))
            yield return descendant;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingBackend(ITextGenerationBackend inner) : ITextGenerationBackend
    {
        private readonly List<(AiGenerationRequest Request, AiGenerationResponse Response)> _calls = [];

        public async Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request)
        {
            var response = await inner.GenerateAsync(request);
            _calls.Add((request, response));
            return response;
        }

        public Task<int> CountTokensAsync(string text, string? modelKey, CancellationToken ct = default)
            => inner.CountTokensAsync(text, modelKey, ct);

        public int GetEffectiveContextSize(string? modelKey = null)
            => inner.GetEffectiveContextSize(modelKey);

        public int CallCount => _calls.Count;

        public string FormatTranscript() => string.Join(
            "\n\n--- CALL ---\n",
            _calls.Select(call => $"PROMPT:\n{call.Request.Prompt}\nOUTPUT:\n{call.Response.Output}"));

        public void Dispose() => inner.Dispose();
    }
}
