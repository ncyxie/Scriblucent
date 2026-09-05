using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiGenerationPipelineTests
{
    // Two genuinely distinct, varied topics (not repeated boilerplate - AiInputGuard's
    // suitability heuristics reject repetitive/low-variety text) tagged with a keyword each, long
    // enough combined to reliably split into multiple chunks regardless of chunking-threshold
    // tuning, so repair-prompt content can be asserted directly rather than assuming an exact
    // chunk count.
    private const string Section0 = """
        Section0Keyword Photosynthesis is the process by which plants convert light energy into chemical energy.
        Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
        The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
        Water molecules are split during photolysis, releasing oxygen as a byproduct.
        ATP and NADPH produced in the light reactions power the Calvin cycle.
        The Calvin cycle fixes carbon dioxide into glucose through carbon fixation.
        RuBisCO is the enzyme responsible for fixing CO2 in the Calvin cycle.
        C3 plants use the Calvin cycle directly while C4 plants have an additional pathway.
        CAM plants open their stomata at night to reduce water loss in arid environments.
        Temperature affects enzyme activity and therefore the rate of photosynthesis.
        Light intensity directly influences the rate of the light-dependent reactions.
        Carbon dioxide concentration is often the limiting factor in photosynthesis.
        """;

    private const string Section1 = """
        Section1Keyword The Roman Empire expanded across the Mediterranean over several centuries of conquest.
        Roman roads connected distant provinces and enabled rapid movement of troops and goods.
        The Senate governed during the Republic before power consolidated under the emperors.
        Aqueducts carried fresh water into cities, supporting public baths and fountains.
        Roman law established legal principles that still influence modern justice systems.
        The legions were organized into disciplined units trained for coordinated combat.
        Trade routes brought silk, spices, and grain from provinces to the capital.
        Latin became the administrative language across the empire's diverse territories.
        Roman architecture introduced the arch and concrete for durable construction.
        The empire eventually split into western and eastern halves for administration.
        Currency debasement and invasions contributed to instability in later centuries.
        Byzantine successors preserved Roman traditions for another thousand years.
        """;

    private static string BuildMultiSectionNote() => Section0 + "\n\n" + Section1;

    // System.Progress<T>.Report posts to a captured SynchronizationContext asynchronously rather
    // than invoking the handler inline - fine for the real UI (single-threaded dispatcher queue
    // keeps ordering correct) but makes "assert right after await" flaky in a test with no
    // synchronization context. This double invokes the callback synchronously instead.
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    [Fact]
    public async Task RunAsync_ChunkScopedRepair_RepairPromptExcludesSuccessfulChunkText()
    {
        var noteText = BuildMultiSectionNote();
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("chunk output content that is long enough to not look like a refusal message.");

        string? repairPromptSeen = null;

        var callbacks = new AiGenerationCallbacks
        {
            BuildChunkPrompt = (chunk, chunks) => $"chunk-prompt-{chunk.Index}",
            BuildRepairPrompt = (failedChunkText, chunks, count) =>
            {
                repairPromptSeen = failedChunkText;
                return "repair-prompt";
            },
            MaxTokens = 100,
            Temperature = 0.5,
            BuildChunkGrammar = _ => "",
            BuildRepairGrammar = _ => "",
            GrammarRoot = null,
            // Only chunk 0 "fails" to contribute anything - every other chunk succeeds.
            OnChunkGenerated = (output, chunks, index) => Task.FromResult(index == 0 ? 0 : 1),
            OnRepairGenerated = (repaired, totalChunkCount, ct) => Task.FromResult("recovered"),
            // Repair now runs whenever any chunk failed, BEFORE the first finalize checkpoint
            // (previously finalize had to return null first to reach the repair branch), so a
            // plain result here still exercises the repair pass.
            Finalize = () => Task.FromResult<object?>("done")
        };

        var result = await AiGenerationPipeline.RunAsync<string>(
            noteText, AiChunkingPurpose.Flashcards, backend, callbacks, progress: null, ct: default);

        Assert.Equal("done", result);
        Assert.NotNull(repairPromptSeen);
        Assert.Contains("Section0Keyword", repairPromptSeen);
        // Section1 is guaranteed to land in a different, successful chunk whenever chunking
        // actually split the note (index 0 always fails, the highest index always succeeds) -
        // its text must not have been resubmitted for repair.
        Assert.DoesNotContain("Section1Keyword", repairPromptSeen);
    }

    [Fact]
    public async Task RunAsync_NoFailedChunks_ReportsFullCoverage()
    {
        var noteText = BuildMultiSectionNote();
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("chunk output content that is long enough to not look like a refusal message.");

        AiGenerationProgress? lastCoverageReport = null;
        var progress = new SyncProgress<AiGenerationProgress>(status =>
        {
            if (status.SuccessfulChunkCount.HasValue)
                lastCoverageReport = status;
        });

        var callbacks = new AiGenerationCallbacks
        {
            BuildChunkPrompt = (chunk, chunks) => $"chunk-prompt-{chunk.Index}",
            MaxTokens = 100,
            Temperature = 0.5,
            BuildChunkGrammar = _ => "",
            GrammarRoot = null,
            OnChunkGenerated = (output, chunks, index) => Task.FromResult(1),
            Finalize = () => Task.FromResult<object?>("done")
        };

        var result = await AiGenerationPipeline.RunAsync<string>(
            noteText, AiChunkingPurpose.Flashcards, backend, callbacks, progress, ct: default);

        Assert.Equal("done", result);
        Assert.NotNull(lastCoverageReport);
        Assert.Equal(lastCoverageReport!.TotalChunkCount, lastCoverageReport.SuccessfulChunkCount);
    }

    [Fact]
    public async Task RunAsync_FailedChunkRecoveredByRepair_ReportsConservativePreRepairCoverage()
    {
        var noteText = BuildMultiSectionNote();
        using var backend = new FakeTextGenerationBackend();
        for (var i = 0; i < 20; i++)
            backend.EnqueueResponse("chunk output content that is long enough to not look like a refusal message.");

        AiGenerationProgress? lastCoverageReport = null;
        var progress = new SyncProgress<AiGenerationProgress>(status =>
        {
            if (status.SuccessfulChunkCount.HasValue)
                lastCoverageReport = status;
        });

        var callbacks = new AiGenerationCallbacks
        {
            BuildChunkPrompt = (chunk, chunks) => $"chunk-prompt-{chunk.Index}",
            BuildRepairPrompt = (failedChunkText, chunks, count) => "repair-prompt",
            MaxTokens = 100,
            Temperature = 0.5,
            BuildChunkGrammar = _ => "",
            BuildRepairGrammar = _ => "",
            GrammarRoot = null,
            OnChunkGenerated = (output, chunks, index) => Task.FromResult(index == 0 ? 0 : 1),
            OnRepairGenerated = (repaired, totalChunkCount, ct) => Task.FromResult("recovered"),
            // See the matching comment above: repair precedes finalize now, so a plain result
            // still exercises the repair pass.
            Finalize = () => Task.FromResult<object?>("done")
        };

        var result = await AiGenerationPipeline.RunAsync<string>(
            noteText, AiChunkingPurpose.Flashcards, backend, callbacks, progress, ct: default);

        Assert.Equal("done", result);
        Assert.NotNull(lastCoverageReport);
        // A single batched repair call has no per-chunk attribution in its response, so a
        // successful repair does NOT get optimistically credited with recovering every chunk it
        // targeted - coverage keeps its accurate pre-repair value (only chunk 0 failed here).
        Assert.Equal(lastCoverageReport!.TotalChunkCount - 1, lastCoverageReport.SuccessfulChunkCount);
    }
}
