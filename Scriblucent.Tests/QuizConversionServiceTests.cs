using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class QuizConversionServiceTests
{
    private const string StudyNote = """
        Photosynthesis is the process by which plants convert light energy into chemical energy.
        Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
        The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
        Water molecules are split during photolysis, releasing oxygen as a byproduct.
        ATP and NADPH produced in the light reactions power the Calvin cycle.
        The Calvin cycle fixes carbon dioxide into glucose through carbon fixation.
        RuBisCO is the enzyme responsible for fixing CO2 in the Calvin cycle.
        C3 plants use the Calvin cycle directly while C4 plants have an additional pathway.
        CAM plants open their stomata at night to reduce water loss in arid environments.
        Temperature affects enzyme activity and therefore the rate of photosynthesis.

        The Roman Empire expanded across the Mediterranean over several centuries of conquest.
        Roman roads connected distant provinces and enabled rapid movement of troops and goods.
        The Senate governed during the Republic before power consolidated under the emperors.
        Aqueducts carried fresh water into cities, supporting public baths and fountains.
        Roman law established legal principles that still influence modern justice systems.
        The legions were organized into disciplined units trained for coordinated combat.
        Trade routes brought silk, spices, and grain from provinces to the capital.
        Latin became the administrative language across the empire's diverse territories.
        """;

    private const string ValidQuizOutput = """
        title: Biology Quiz

        q: Which molecule stores energy in cells?
        type: single
        correct: ATP
        wrong: DNA
        wrong: Water
        explanation: ATP stores chemical energy in cells.

        q: Chlorophyll absorbs light.
        type: truefalse
        answer: true
        explanation: Chlorophyll absorbs light for the process.
        """;

    [Fact]
    public async Task ConvertToQuizAsync_ValidOutput_ReturnsDocument()
    {
        // StudyNote chunks into 2-3 sections; enqueue one valid response per chunk plus one for potential repair
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 5; i++)
            backend.EnqueueResponse(ValidQuizOutput);

        var service = new QuizConversionService(backend);
        var quiz = await service.ConvertToQuizAsync("Biology", StudyNote);

        Assert.NotNull(quiz);
        Assert.True(quiz!.Questions.Count >= 1);
        Assert.False(string.IsNullOrWhiteSpace(quiz.Title));
    }

    [Fact]
    public async Task ConvertToQuizAsync_TypedWellFormedOutput_AllSurviveDisplayFiltering()
    {
        // Locks the parser/display pipeline against the shapes the reworked QuizGrammar now
        // guarantees: singles with 1 correct + 2 wrong (>=3 options, previously the model's
        // 1+1 minimum passed parsing but every such single was dropped at display filtering),
        // multi with >=2 correct lines, and truefalse as a statement with answer:true/false.
        const string ShortNote = """
            Photosynthesis is the process by which plants convert light energy into chemical energy.
            Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
            The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
            Water molecules are split during photolysis, releasing oxygen as a byproduct.
            ATP and NADPH produced in the light reactions power the Calvin cycle.
            """;

        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("""
            title: Biology Quiz

            q: Which molecule stores energy in cells?
            type: single
            correct: ATP
            wrong: DNA
            wrong: Water
            explanation: ATP stores chemical energy in cells.

            q: Which molecules are produced by the light reactions?
            type: multi
            correct: ATP
            correct: NADPH
            wrong: Glucose
            wrong: Oxygen
            explanation: The light reactions produce ATP and NADPH.

            q: Chlorophyll absorbs red and blue light.
            type: truefalse
            answer: true
            explanation: Chlorophyll absorbs red and blue light, reflecting green.
            """);

        var service = new QuizConversionService(backend);
        var quiz = await service.ConvertToQuizAsync("Biology", ShortNote);

        Assert.NotNull(quiz);
        Assert.Equal(3, quiz!.Questions.Count);
    }

    [Fact]
    public async Task ConvertToQuizAsync_AllChunksRefuse_ThrowsAiInputRejectedException()
    {
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse(AiInputGuard.RefusalOutput);

        var service = new QuizConversionService(backend);

        await Assert.ThrowsAsync<AiInputRejectedException>(
            () => service.ConvertToQuizAsync("Biology", StudyNote));
    }

    [Fact]
    public async Task ConvertToQuizAsync_UnparseableOutput_Throws()
    {
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("this output does not contain any valid quiz records at all.");

        var service = new QuizConversionService(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConvertToQuizAsync("Biology", StudyNote));
    }
}
