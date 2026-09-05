namespace Scriblucent.Services.AI;

internal sealed record AiGenerationRequest(
    string Prompt,
    int MaxTokens,
    double Temperature,
    double TopP = 0.8,
    int TopK = 20,
    double PresencePenalty = 0.0,
    double RepetitionPenalty = 1.0,
    string? Grammar = null,
    string? GrammarRoot = null,
    IProgress<AiGenerationProgress>? Progress = null,
    CancellationToken CancellationToken = default,
    // Resolved once per generation job (see AiGenerationPipeline.RunAsync) so every chunk of the
    // same job targets the same model even if the user changes the selection mid-job. Empty means
    // "not job-scoped" - the backend falls back to resolving the currently-selected model itself.
    string ModelKey = "",
    // TEMPORARY trace hook: invoked with each streamed token as it arrives from the worker, so
    // AiGenerationTracer can capture the live output (including partial text on stall/cancel).
    Action<string>? TokenObserver = null
);
