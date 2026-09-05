using System.Diagnostics;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiPerformanceSmokeTests
{
    [Fact]
    public async Task ChunkingPipeline_1200Words_CompletesQuickly()
    {
        var note = string.Join(" ", Enumerable.Repeat(
            "Photosynthesis converts light energy into chemical energy in chloroplasts. ", 200));

        var sw = Stopwatch.StartNew();
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            note, AiChunkingPurpose.Flashcards, tokenCounter: null, ct: CancellationToken.None);
        sw.Stop();

        Assert.True(chunks.Count >= 2);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Chunking took {sw.ElapsedMilliseconds}ms, expected <500ms");
    }

    [Fact]
    public void OutputGrammars_BoundedRepetition_HasNoUnboundedPlus()
    {
        var flash = OutputGrammars.FlashcardGrammar(3, 6);
        var mind = OutputGrammars.MindMapGrammar(3, 6);
        var quiz = OutputGrammars.QuizGrammar(4, 8);

        // Bounded {min,max} must be present, unbounded "+" on repeating rule must not
        Assert.Contains("{3,6}", flash);
        Assert.Contains("{3,6}", mind);
        Assert.Contains("{4,8}", quiz);

        // MindMap/Quiz must not crash on "_" rule name (verified native AV in 0.27.0)
        Assert.DoesNotContain("root_line", mind);
        Assert.DoesNotContain("correct_line", quiz);
    }

    [Fact]
    public async Task Pipeline_FakeBackend_DoesNotHangOnPerChunkRepair()
    {
        const string note = """
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

            Additional section on ecosystems: Forests recycle nutrients through decomposition.
            Coral reefs support immense biodiversity despite covering less than 1% of ocean floor.
            Wetlands filter water and buffer floods while providing habitat for migratory birds.
            """;

        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("q: What is photosynthesis?\na: Process converting light energy into chemical energy in chloroplasts.\n");

        var service = new Scriblucent.Services.FlashcardConversionService(backend);
        var sw = Stopwatch.StartNew();
        var cards = await service.ConvertToFlashcardsAsync(note);
        sw.Stop();

        Assert.True(cards.Count > 0);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Pipeline hung: {sw.ElapsedMilliseconds}ms");
    }
}
