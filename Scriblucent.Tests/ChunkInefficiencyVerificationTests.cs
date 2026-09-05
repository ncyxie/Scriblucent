using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class ChunkInefficiencyVerificationTests
{
    [Fact]
    public async Task TinyHeadingBeforeHugeParagraph_NoTinyChunk()
    {
        // Keep a short heading with the following paragraph instead of emitting a heading-only chunk.
        var heading45 = new string('X', 45);
        var largePara = string.Join(" ", Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit.", 80));
        var note = $"Intro paragraph with some content. {string.Join(" ", Enumerable.Repeat("Photosynthesis converts light.", 20))}\n\n{heading45}\n\n{largePara}\n\nConclusion.";

        var chunks = await AiChunkingPipeline.PrepareChunksAsync(note, AiChunkingPurpose.Flashcards, tokenCounter: null);

        // No chunk should be tiny (< 100 chars) except possibly single-chunk case
        if (chunks.Count > 1)
        {
            foreach (var c in chunks)
                Assert.True(c.Text.Length >= 100, $"Tiny chunk {c.Text.Length} chars: '{c.Text.Substring(0, Math.Min(50, c.Text.Length))}'");
        }

        // Variance should be < 5x (previously 105x for tail case)
        if (chunks.Count > 1)
        {
            var min = chunks.Min(c => c.Text.Length);
            var max = chunks.Max(c => c.Text.Length);
            Assert.True((double)max / min < 5, $"Variance {max}/{min} = {(double)max/min:F1}x too high");
        }
    }

    [Fact]
    public async Task TinyTail17Chars_Merged()
    {
        // From earlier simulation: "References:" 11 + 1800 + "Final conclusion." 17 tail should not remain 17 alone
        var large = string.Join(" ", Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit.", 45)); // ~2565
        var note = $"Intro content. {new string('a', 500)}\n\nReferences:\n\n{large.Substring(0, 1800)}\n\nFinal conclusion.";

        var chunks = await AiChunkingPipeline.PrepareChunksAsync(note, AiChunkingPurpose.Flashcards, tokenCounter: null);

        if (chunks.Count > 1)
        {
            foreach (var c in chunks)
                Assert.True(c.Text.Length >= 100, $"Tiny tail {c.Text.Length} not merged");
        }
    }
}
