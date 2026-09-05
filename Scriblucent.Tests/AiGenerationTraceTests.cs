using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiGenerationTraceTests
{
    private static string DiagnosticsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Scriblucent",
        "AiCache",
        "diagnostics");

    private static IReadOnlyList<string> SnapshotGenLogs(string tagPrefix)
        => Directory.Exists(DiagnosticsDir)
            ? Directory.GetFiles(DiagnosticsDir, $"gen-{tagPrefix}-*.log")
            : Array.Empty<string>();

    private static void DeleteNewGenLogs(IReadOnlyList<string> before, string tagPrefix)
    {
        if (!Directory.Exists(DiagnosticsDir))
            return;

        foreach (var file in Directory.GetFiles(DiagnosticsDir, $"gen-{tagPrefix}-*.log"))
        {
            if (!before.Contains(file))
            {
                try { File.Delete(file); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    [Fact]
    public void StartJob_ForceEnabled_WritesJobHeader()
    {
        var before = SnapshotGenLogs("test-trace");
        try
        {
            using var tracer = AiGenerationTracer.StartJob("test-trace", "Flashcards", "fake-model", "note body", forceEnable: true);

            Assert.NotNull(tracer);
            var after = SnapshotGenLogs("test-trace");
            var created = Assert.Single(after.Except(before));
            var content = File.ReadAllText(created);

            Assert.Contains("=== JOB START ===", content);
            Assert.Contains("Purpose: Flashcards", content);
            Assert.Contains("ModelKey: fake-model", content);
            Assert.Contains("--- SOURCE NOTE ---", content);
            Assert.Contains("note body", content);
        }
        finally
        {
            DeleteNewGenLogs(before, "test-trace");
        }
    }

    [Fact]
    public void Tracer_Sections_AppearInOrderWithContent()
    {
        var before = SnapshotGenLogs("test-trace");
        try
        {
            using var tracer = AiGenerationTracer.StartJob("test-trace", "Quiz", "fake-model", "note", forceEnable: true);
            Assert.NotNull(tracer);

            tracer.LogChunkPlan(
            [
                new TextChunk("chunk zero text", 0, 2, 0, 15, 4),
                new TextChunk("chunk one text", 1, 2, 15, 29, 4)
            ]);

            var request = new AiGenerationRequest(
                Prompt: "the prompt body",
                MaxTokens: 1600,
                Temperature: 0.7,
                Grammar: "root ::= \"x\"\n",
                GrammarRoot: "quiz");
            tracer.LogCallStart(0, "chunk 1/2", request);
            tracer.AppendStream("tok");
            tracer.AppendStream("ens");
            tracer.LogCallEnd(0, new AiGenerationResponse(Output: "tokens", GeneratedChars: 6, Elapsed: TimeSpan.FromMilliseconds(1234)));
            tracer.LogVerdict(1, addedItems: 3, refusal: false, refusalReason: AiInputGuard.RefusalReasonNone);
            tracer.LogOutcome("success: coverage=1/2");

            var after = SnapshotGenLogs("test-trace");
            var created = Assert.Single(after.Except(before));
            var content = File.ReadAllText(created);

            Assert.Contains("=== CHUNK PLAN ===", content);
            Assert.Contains("Chunk[0] of 2: chars=15 tokens=4 offsets=[0..15]", content);
            Assert.Contains("Chunk[1] of 2: chars=14 tokens=4 offsets=[15..29]", content);
            Assert.Contains("=== CALL #0 [chunk 1/2] START ===", content);
            Assert.Contains("MaxTokens=1600 Temp=0.7", content);
            Assert.Contains("--- GRAMMAR ---", content);
            Assert.Contains("root ::= \"x\"", content);
            Assert.Contains("--- PROMPT ---", content);
            Assert.Contains("the prompt body", content);
            Assert.Contains("--- STREAM ---", content);
            Assert.Contains("tok", content);
            Assert.Contains("<<STREAM END>>", content);
            Assert.Contains("=== CALL #0 END ===", content);
            Assert.Contains("--- RAW OUTPUT ---", content);
            Assert.Contains("tokens", content);
            Assert.Contains("ElapsedMs=1234", content);
            Assert.Contains("=== VERDICT chunk 1 === AddedItems=3 Refusal=False Reason=none", content);
            Assert.Contains("=== JOB OUTCOME === success: coverage=1/2", content);
        }
        finally
        {
            DeleteNewGenLogs(before, "test-trace");
        }
    }

    [Fact]
    public void Tracer_AbortedCall_ClosesStreamAndRecordsReason()
    {
        var before = SnapshotGenLogs("test-trace");
        try
        {
            using var tracer = AiGenerationTracer.StartJob("test-trace", "MindMap", "fake-model", "note", forceEnable: true);
            Assert.NotNull(tracer);

            tracer.LogCallStart(0, "chunk 1/1", new AiGenerationRequest("prompt", 700, 0.7));
            tracer.AppendStream("partial output before stall");
            tracer.LogCallAborted(0, "TimeoutException: AI generation stalled");

            var after = SnapshotGenLogs("test-trace");
            var created = Assert.Single(after.Except(before));
            var content = File.ReadAllText(created);

            Assert.Contains("partial output before stall", content);
            Assert.Contains("<<STREAM END>>", content);
            Assert.Contains("=== CALL #0 ABORTED === TimeoutException: AI generation stalled", content);
        }
        finally
        {
            DeleteNewGenLogs(before, "test-trace");
        }
    }

    [Fact]
    public async Task Pipeline_FlashcardRun_WritesFullGenerationTrace()
    {
        const string ShortNote = """
            Photosynthesis is the process by which plants convert light energy into chemical energy.
            Chlorophyll absorbs red and blue light, reflecting green light which gives leaves their color.
            The light-dependent reactions occur in the thylakoid membrane of chloroplasts.
            Water molecules are split during photolysis, releasing oxygen as a byproduct.
            ATP and NADPH produced in the light reactions power the Calvin cycle.
            """;

        var before = SnapshotGenLogs("flashcard");
        try
        {
            using var backend = new FakeTextGenerationBackend();
            backend.EnqueueResponse("""
                q: What do thylakoid membranes host?
                a: The light-dependent reactions of photosynthesis.

                q: What does chlorophyll absorb?
                a: Red and blue light.
                """);

            var service = new FlashcardConversionService(backend);
            var cards = await service.ConvertToFlashcardsAsync(ShortNote);
            Assert.Equal(2, cards.Count);

            // Other AI tests run in parallel and also emit gen-flashcard logs (diagnostic logging
            // is on), so identify OUR job's file by its unique enqueued response text rather than
            // assuming exactly one new file appeared.
            var after = SnapshotGenLogs("flashcard");
            var ours = after
                .Except(before)
                .Select(file => (File: file, Content: File.ReadAllText(file)))
                .Where(entry => entry.Content.Contains("q: What do thylakoid membranes host?"))
                .ToList();
            var created = Assert.Single(ours);

            var content = created.Content;
            Assert.Contains("=== JOB START ===", content);
            Assert.Contains("Purpose: Flashcards", content);
            Assert.Contains("=== CHUNK PLAN ===", content);
            Assert.Contains("=== CALL #0 [chunk 1/1] START ===", content);
            Assert.Contains("--- PROMPT ---", content);
            Assert.Contains("--- GRAMMAR ---", content);
            Assert.Contains("flashcards ::=", content);
            Assert.Contains("=== CALL #0 END ===", content);
            Assert.Contains("--- RAW OUTPUT ---", content);
            Assert.Contains("=== VERDICT chunk 1 === AddedItems=2 Refusal=False Reason=none", content);
            Assert.Contains("=== JOB OUTCOME === success: coverage=1/1", content);

            try { File.Delete(created.File); }
            catch { /* best effort cleanup */ }
        }
        finally
        {
            DeleteNewGenLogs(before, "flashcard");
        }
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData(AiInputGuard.RefusalOutput, "exact-prefix")]
    [InlineData("  REFUSE: INSUFFICIENT MEANINGFUL NOTE CONTENT", "exact-prefix")]
    [InlineData("ok", "under-20-chars")]
    [InlineData("yes I agree!", "under-20-chars")]
    [InlineData("I cannot generate flashcards from this content.", "i-cannot-prefix")]
    [InlineData("I can't process this note.", "i-cannot-prefix")]
    [InlineData("There is insufficient content to create study material.", "insufficient-content")]
    [InlineData("Not enough meaningful information in the note.", "not-enough-info")]
    [InlineData("Refuse: the input is not suitable.", "refuse-prefix")]
    [InlineData("refusal: cannot help", "refuse-prefix")]
    public void ExplainRefusalOutput_MapsEachRuleToItsReason(string? output, string expectedReason)
    {
        Assert.Equal(expectedReason, AiInputGuard.ExplainRefusalOutput(output));
    }

    [Fact]
    public void ExplainRefusalOutput_ValidOutputs_ReturnNone()
    {
        Assert.Equal(AiInputGuard.RefusalReasonNone, AiInputGuard.ExplainRefusalOutput("q: What is ATP?\na: Energy currency of cells."));
        Assert.Equal(AiInputGuard.RefusalReasonNone, AiInputGuard.ExplainRefusalOutput("ROOT: Biology Notes\n- Photosynthesis\n- Cellular respiration"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsRefusalOutput_EmptyOrNull_StillReturnsFalse(string? output)
    {
        Assert.False(AiInputGuard.IsRefusalOutput(output));
        Assert.Equal(AiInputGuard.RefusalReasonNone, AiInputGuard.ExplainRefusalOutput(output));
    }

    [Fact]
    public void IsRefusalOutput_AgreesWithExplainRefusalReason()
    {
        string[] samples =
        [
            AiInputGuard.RefusalOutput,
            "ok",
            "I cannot do that.",
            "There is insufficient content here.",
            "Not enough information provided.",
            "Refuse: bad input.",
            "q: What is ATP?\na: Energy currency of cells.",
            "This is a perfectly normal long response about photosynthesis."
        ];

        foreach (var sample in samples)
            Assert.Equal(
                AiInputGuard.ExplainRefusalOutput(sample) != AiInputGuard.RefusalReasonNone,
                AiInputGuard.IsRefusalOutput(sample));
    }
}
