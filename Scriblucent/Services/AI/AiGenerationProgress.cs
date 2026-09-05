namespace Scriblucent.Services.AI;

internal sealed record AiGenerationProgress(
    AiGenerationStatus Status,
    int? Percent = null,
    int? GeneratedChars = null,
    int? ChunkIndex = null,
    int? ChunkCount = null,
    // Fired once on completion (see AiGenerationPipeline.ReportCoverage) - distinct from
    // ChunkIndex/ChunkCount, which track the currently-in-flight chunk during generation.
    // SuccessfulChunkCount < TotalChunkCount means the result is only partial coverage.
    int? SuccessfulChunkCount = null,
    int? TotalChunkCount = null
);
