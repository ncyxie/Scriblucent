using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiChunkingPipelineTests
{
    private const string RealisticBiologyNote = """
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
        Light intensity directly influences the rate of the light-dependent reactions.
        Carbon dioxide concentration is often the limiting factor in photosynthesis.
       """;

    private const string LongBiologyNote = """
        Photosynthesis is the fundamental process that sustains most life on Earth.
        Plants, algae, and some bacteria use sunlight to synthesize organic compounds.
        The process occurs in two main stages: light-dependent reactions and the Calvin cycle.
        During light reactions, water molecules are split and oxygen is released.
        ATP and NADPH are generated as energy carriers for the next stage.
        The Calvin cycle uses these carriers to fix carbon dioxide into glucose.
        This glucose serves as the primary energy source for plant metabolism.
        Plants also use glucose to build cellulose for cell walls and starch for storage.
        The rate of photosynthesis depends on light intensity, temperature, and CO2 levels.
        In bright sunlight, photosynthesis can produce oxygen faster than respiration consumes it.
        At night, plants only perform respiration and consume stored glucose.
        Desert plants have evolved CAM photosynthesis to conserve water during hot days.
        C4 plants like corn and sugarcane concentrate CO2 around RuBisCO to reduce photorespiration.
        Global climate change affects photosynthesis through increased temperatures and CO2 levels.
        Understanding photosynthesis is crucial for agriculture and food security worldwide.
        """;

    [Fact]
    public async Task PrepareChunksAsync_EmptyText_ReturnsEmpty()
    {
        var counter = new FakeTokenCounter(100);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            "", AiChunkingPurpose.Flashcards, counter);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task PrepareChunksAsync_ShortText_ReturnsSingleChunk()
    {
        var counter = new FakeTokenCounter(20);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            RealisticBiologyNote, AiChunkingPurpose.Flashcards, counter);

        Assert.NotEmpty(chunks);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(chunks.Count, chunks[0].TotalCount);
        Assert.Equal(0, chunks[0].StartCharOffset);
        Assert.True(chunks[0].EndCharOffset > 0);
        Assert.True(chunks[0].TokenCount > 0);
    }

    [Fact]
    public async Task PrepareChunksAsync_LongText_ProducesMultipleChunks()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Flashcards, counter);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task PrepareChunksAsync_ChunksHaveSequentialIndices()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Flashcards, counter);

        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
            Assert.Equal(chunks.Count, chunks[i].TotalCount);
        }
    }

    [Fact]
    public async Task PrepareChunksAsync_ChunksHaveValidOffsets()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Flashcards, counter);

        foreach (var chunk in chunks)
        {
            Assert.True(chunk.StartCharOffset >= 0, "StartCharOffset must be >= 0");
            Assert.True(chunk.EndCharOffset > chunk.StartCharOffset, "EndCharOffset must be > StartCharOffset");
        }
    }

    [Fact]
    public void SplitIntoNaturalSegments_MultiLineParagraph_JoinsLinesWithLfNotCrlf()
    {
        // Regression test: SplitIntoNaturalSegments used to join a paragraph's lines with
        // StringBuilder.AppendLine() (Environment.NewLine, i.e. \r\n on Windows), even though
        // NormalizeText had already collapsed the source text to LF-only - so a multi-line
        // paragraph's rebuilt segment silently gained \r characters the LF-only source never
        // had, and FindCharOffset's ordinal IndexOf search against that source would then fail
        // to locate it, falling back to a wrong offset. Calling the private method directly
        // (rather than going through the full public chunking pipeline, where the emergent
        // segment/chunk boundaries depend on note-length-sensitive threshold math) verifies the
        // exact mechanism that changed.
        var method = typeof(AiChunkingPipeline).GetMethod("SplitIntoNaturalSegments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var text = "Line one of a paragraph.\nLine two of the same paragraph, still going.\nLine three finishes it off.";
        var segments = (List<string>)method.Invoke(null, [text, 1000])!;

        var segment = Assert.Single(segments);
        Assert.DoesNotContain('\r', segment);
        Assert.Contains("Line one", segment);
        Assert.Contains("Line three", segment);
    }

    [Fact]
    public void PackSegments_MultipleSegmentsInOneChunk_JoinsWithLfNotCrlf()
    {
        // Same regression as above, for PackSegments' blank-line separator between segments
        // packed into a single chunk.
        var method = typeof(AiChunkingPipeline).GetMethod("PackSegments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        string[] segments = ["First segment text.", "Second segment text.", "Third segment text."];
        var chunks = (List<string>)method.Invoke(null, [segments, 1000, 1200, 10])!;

        var chunk = Assert.Single(chunks);
        Assert.DoesNotContain('\r', chunk);
        Assert.Contains("First segment", chunk);
        Assert.Contains("Third segment", chunk);
    }

    [Fact]
    public async Task PrepareChunksAsync_NullTokenCounter_FallsBackGracefully()
    {
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Flashcards, tokenCounter: null);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task PrepareChunksAsync_NullTokenCounter_ShortSingleWordText_DoesNotThrow()
    {
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            "Photosynthesis", AiChunkingPurpose.Flashcards, tokenCounter: null);

        Assert.NotEmpty(chunks);
        Assert.True(chunks[0].TokenCount > 0);
    }

    [Fact]
    public async Task PrepareChunksAsync_MindMapPurpose_Works()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.MindMap, counter);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task PrepareChunksAsync_QuizPurpose_Works()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Quiz, counter);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task PrepareChunksAsync_AllChunksHaveNonEmptyText()
    {
        var counter = new FakeTokenCounter(500);
        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            LongBiologyNote, AiChunkingPurpose.Flashcards, counter);

        foreach (var chunk in chunks)
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk.Text), "Chunk text must not be empty");
        }
    }
}

internal sealed class FakeTokenCounter : ITokenCounter
{
    private readonly int _maxTokens;

    public FakeTokenCounter(int maxTokens)
    {
        _maxTokens = maxTokens;
    }

    public Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        var estimated = Math.Max(1, text.Length / 4);
        return Task.FromResult(Math.Min(estimated, _maxTokens));
    }
}
