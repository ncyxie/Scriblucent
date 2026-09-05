namespace Scriblucent.Services.AI;

internal sealed class AiGenerationCallbacks
{
    public required Func<TextChunk, IReadOnlyList<TextChunk>, string> BuildChunkPrompt { get; init; }
    public Func<string, IReadOnlyList<TextChunk>, int, string>? BuildRepairPrompt { get; init; }
    public required int MaxTokens { get; init; }
    public int RepairMaxTokens { get; init; }
    public required double Temperature { get; init; }
    public double RepairTemperature { get; init; } = 0.1;
    public double TopP { get; init; } = 0.8;
    public double RepairTopP { get; init; } = 0.8;
    public int TopK { get; init; } = 20;
    public int RepairTopK { get; init; } = 20;
    public double PresencePenalty { get; init; } = 0.0;
    public double RepairPresencePenalty { get; init; } = 0.0;
    public double RepetitionPenalty { get; init; } = 1.08;
    public double RepairRepetitionPenalty { get; init; } = 1.08;
    // Grammar depends on each chunk's own text (its bounded item-count band), so it's computed
    // per chunk like BuildChunkPrompt, not fixed once for the whole job.
    public required Func<TextChunk, string> BuildChunkGrammar { get; init; }
    public Func<string, string>? BuildRepairGrammar { get; init; }
    public required string? GrammarRoot { get; init; }
    // Returns the count of items actually added by this chunk's output - lets the pipeline tell
    // a chunk that contributed nothing from one that succeeded (see failedChunkIndexes in
    // AiGenerationPipeline.RunAsync).
    public required Func<string, IReadOnlyList<TextChunk>, int, Task<int>> OnChunkGenerated { get; init; }
    // totalChunkCount lets callers stamp recovered items with a set index distinct from any real
    // chunk's - a single batched repair call has no per-chunk attribution, so items it recovers
    // can't honestly be credited to any one original chunk.
    public Func<string, int, CancellationToken, Task<string>>? OnRepairGenerated { get; init; }
    public Func<Task<object?>>? Finalize { get; init; }
    public Func<string, string>? DiagnosticTag { get; init; }

    // When true, failed chunks are repaired immediately per-chunk (with previous output) instead of
    // one batched repair covering all failed chunks together. MindMap needs this (its fallback is
    // per-chunk and its grammar band is per-chunk); Flashcards/Quiz keep the batched model.
    public bool PerChunkRepair { get; init; }
    public Func<TextChunk, string, IReadOnlyList<TextChunk>, string>? BuildPerChunkRepairPrompt { get; init; }
    public Func<TextChunk, string>? BuildPerChunkRepairGrammar { get; init; }
}
