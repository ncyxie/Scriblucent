using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class FlashcardConversionServiceTests
{
    // Varied, non-repetitive prose (AiInputGuard rejects repetitive/low-variety text) long enough
    // to reliably chunk into multiple sections regardless of chunking-threshold tuning.
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

    [Fact]
    public async Task ConvertToFlashcardsAsync_AllChunksRefuse_ThrowsAiInputRejectedException()
    {
        // Regression test for a bug where FlashcardConversionService.Finalize always returned a
        // non-null (possibly empty) list, so AiGenerationPipeline.RunAsync's IsDefault check
        // never saw "no result yet" - short-circuiting past the all-refusal check, the repair
        // pass, and the diagnostic-log-and-throw fallback for every single Flashcard generation.
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse(AiInputGuard.RefusalOutput);

        var service = new FlashcardConversionService(backend);

        await Assert.ThrowsAsync<AiInputRejectedException>(
            () => service.ConvertToFlashcardsAsync(StudyNote));
    }

    [Fact]
    public async Task ConvertToFlashcardsAsync_DuplicateQuestionTextDifferentAnswers_CollapsesToFirstOccurrence()
    {
        // Regression test: DeduplicateFlashcards used to key on question+answer combined, so cards
        // sharing the same generic question text (e.g. "What is the main concept?") but different
        // answers all passed through as "different" cards - a bad flashcard set regardless of
        // whether the answers differ.
        const string ShortNote = """
            Photosynthesis is the process by which plants convert light energy into chemical energy.
            Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
            The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
            Water molecules are split during photolysis, releasing oxygen as a byproduct.
            ATP and NADPH produced in the light reactions power the Calvin cycle.
            """;

        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("""
            q: What is the main concept?
            a: Photosynthesis converts light energy into chemical energy.

            q: What is the main concept?
            a: The Calvin cycle is powered by ATP and NADPH.

            q: What is chlorophyll?
            a: Chlorophyll absorbs red and blue light.
            """);

        var service = new FlashcardConversionService(backend);
        var cards = await service.ConvertToFlashcardsAsync(ShortNote);

        Assert.Equal(2, cards.Count);
        var mainConceptCard = Assert.Single(cards, c => c.Question == "What is the main concept?");
        Assert.Equal("Photosynthesis converts light energy into chemical energy.", mainConceptCard.Answer);
        Assert.Contains(cards, c => c.Question == "What is chlorophyll?");
    }

    [Fact]
    public async Task ConvertToFlashcardsAsync_RefusalTailAfterValidCards_KeepsCards()
    {
        // Regression for a traced real-model failure (AiGenerationTracer log gen-flashcard-
        // 20260821-224224189): the model emitted five valid cards and THEN appended a literal
        // "REFUSE:" tail (echoing the prompt's instruction sentence back). Refusal classification
        // ran before parsing, so the whole output - including every good card - was discarded as a
        // refusal. Pipeline semantics now parse first: any output that yields records succeeds,
        // whatever refusal-looking phrases it contains.
        const string ShortNote = """
            Photosynthesis is the process by which plants convert light energy into chemical energy.
            Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
            The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
            Water molecules are split during photolysis, releasing oxygen as a byproduct.
            ATP and NADPH produced in the light reactions power the Calvin cycle.
            """;

        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("""
            q: What do thylakoid membranes host?
            a: The light-dependent reactions of photosynthesis.

            q: What does chlorophyll absorb?
            a: Red and blue light.

            REFUSE: insufficient meaningful note content if no content is found, else output flashcards above. You are required to use the exact format shown.
            """);

        var service = new FlashcardConversionService(backend);
        var cards = await service.ConvertToFlashcardsAsync(ShortNote);

        Assert.Equal(2, cards.Count);
        Assert.Equal(1, backend.GenerateCallCount);
    }

    [Fact]
    public async Task ConvertToFlashcardsAsync_PartialSuccessStillRecoversFailedChunks()
    {
        // Regression for the same traced run's second half: finalize used to short-circuit before
        // the batched repair whenever at least one chunk succeeded, so a 1/3 coverage was returned
        // as success with zero recovery attempts for the failed chunks. Repair now runs whenever
        // any chunk contributed nothing. Chunk counts vary with tuning, so this backend is
        // role-aware instead of queueing fixed responses: first chunk succeeds, every other chunk
        // fails, and the repair call (identified by its prompt) recovers a distinct card.
        using var backend = new FirstChunkSucceedsBackend();

        var service = new FlashcardConversionService(backend);
        var cards = await service.ConvertToFlashcardsAsync(StudyNote);

        Assert.Contains(cards, c => c.Question == "What powers the Calvin cycle?");
        Assert.Contains(cards, c => c.Question == "What did RuBisCO recover?");
        Assert.True(backend.ChunkCalls >= 2, "note must split into multiple chunks for this scenario");
        Assert.Equal(1, backend.RepairCalls);
    }

    private sealed class FirstChunkSucceedsBackend : ITextGenerationBackend
    {
        private const string GoodCard = """
            q: What powers the Calvin cycle?
            a: ATP and NADPH from the light reactions.
            """;
        private const string Garbage = "this output does not contain any q:/a: flashcard records at all.";
        private const string RepairGoodCard = """
            q: What did RuBisCO recover?
            a: The batched-repair regression test.
            """;

        private int _chunkCalls;

        public int ChunkCalls => _chunkCalls;
        public int RepairCalls { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request)
        {
            // The repair prompt is the only one that starts with "Repair the flashcards".
            if (request.Prompt.StartsWith("Repair the flashcards", StringComparison.Ordinal))
            {
                RepairCalls++;
                return Task.FromResult(new AiGenerationResponse(RepairGoodCard, RepairGoodCard.Length, TimeSpan.FromMilliseconds(1)));
            }

            var output = _chunkCalls++ == 0 ? GoodCard : Garbage;
            return Task.FromResult(new AiGenerationResponse(output, output.Length, TimeSpan.FromMilliseconds(1)));
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task ConvertToFlashcardsAsync_UnparseableOutputEvenAfterRepair_ThrowsInsteadOfReturningEmpty()
    {
        // Every chunk (and the repair pass) produces output that isn't a refusal but also isn't
        // parseable into any card - Finalize must still see "nothing yet" at every checkpoint so
        // the pipeline reaches its final diagnostic-log-and-throw fallback instead of silently
        // returning an empty flashcard list as if generation had succeeded.
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("this output does not contain any q:/a: flashcard records at all.");

        var service = new FlashcardConversionService(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConvertToFlashcardsAsync(StudyNote));
    }
}
