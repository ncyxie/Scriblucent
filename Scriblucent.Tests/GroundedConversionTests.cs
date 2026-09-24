using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class GroundedConversionTests
{
    [Fact]
    public async Task Converters_UseTheSameMandatoryGroundedContext()
    {
        const string evidence = "Plants use chlorophyll to absorb sunlight.";
        const string answer = "chlorophyll";
        var fact = new StudyFact(
            "F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Term,
            AnswerStart: evidence.IndexOf(answer, StringComparison.Ordinal),
            AnswerLength: answer.Length);
        var plan = new StudyPlan(
            "SYNTHETIC", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact],
            SourceUnits: [new StudySourceUnitSnapshot("S001", evidence, 0, 0, 0)]);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced);
        using var backend = new FakeTextGenerationBackend();
        var planner = new GroundedQuestionPlanner();

        var cards = await new FlashcardConversionService(backend, planner).ConvertToFlashcardsAsync(context);
        var quiz = await new QuizConversionService(backend, planner).ConvertToQuizAsync("Plants", context);
        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync("Plants", context);

        Assert.Equal(answer, Assert.Single(cards).Answer);
        Assert.Equal(answer, Assert.Single(quiz!.Questions).PlainTextAnswer);
        Assert.Contains(map!.Children.Concat(map.Children.SelectMany(node => node.Children)), node =>
            node.Text.Contains(answer, StringComparison.Ordinal)
            && node.Text.Length < evidence.Length);
        Assert.DoesNotContain(map.Children, node => node.Text is "Term" or "Facts");
        Assert.Equal(0, backend.GenerateCallCount);
    }
}
