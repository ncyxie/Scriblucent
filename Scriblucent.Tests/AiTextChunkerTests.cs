using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class AiTextChunkerTests
{
    [Fact]
    public void Split_EmptyText_ReturnsNoChunks()
    {
        Assert.Empty(AiTextChunker.Split("  \r\n  ", AiChunkingPurpose.Flashcards));
    }

    [Fact]
    public void Split_ShortStudyText_ReturnsOneNormalizedChunk()
    {
        var text = "First paragraph contains a useful definition.\r\n\r\nSecond paragraph contains another useful fact.";

        var chunks = AiTextChunker.Split(text, AiChunkingPurpose.Flashcards, AiChunkingModelProfile.Small);

        var chunk = Assert.Single(chunks);
        var normalizedChunk = chunk.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain('\r', normalizedChunk);
        Assert.Contains("First paragraph", normalizedChunk);
        Assert.Contains("Second paragraph", normalizedChunk);
    }

    [Fact]
    public void Split_LongText_PreservesEverySectionMarker()
    {
        var text = BuildLongText(32);

        var chunks = AiTextChunker.Split(text, AiChunkingPurpose.Quiz, AiChunkingModelProfile.Medium);

        Assert.True(chunks.Count > 1);
        var combined = string.Join("\n", chunks);
        for (var i = 1; i <= 32; i++)
            Assert.Contains($"Section {i}:", combined);
    }

    [Fact]
    public void Split_SmallerModelProfile_DoesNotProduceFewerChunksThanLargeProfile()
    {
        var text = BuildLongText(36);

        var smallChunks = AiTextChunker.Split(text, AiChunkingPurpose.MindMap, AiChunkingModelProfile.Small);
        var largeChunks = AiTextChunker.Split(text, AiChunkingPurpose.MindMap, AiChunkingModelProfile.Large);

        Assert.True(smallChunks.Count >= largeChunks.Count);
    }

    [Fact]
    public void Split_NormalizesCarriageReturnsInAllChunks()
    {
        var text = BuildLongText(18).Replace('\n', '\r');

        var chunks = AiTextChunker.Split(text, AiChunkingPurpose.Flashcards);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            var normalizedChunk = chunk.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.DoesNotContain('\r', normalizedChunk);
        });
    }

    private static string BuildLongText(int sectionCount)
    {
        return string.Join(
            "\n\n",
            Enumerable.Range(1, sectionCount)
                .Select(index =>
                    $"Section {index}: This section explains a distinct educational concept with a definition, a consequence, and a useful example for study."));
    }
}
