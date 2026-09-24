namespace Scriblucent.Services.AI;

internal interface ITextGenerationBackend : IDisposable
{
    Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request);

    // Token counting for contextual packet budgeting. Default implementation estimates from characters so
    // test doubles (FakeTextGenerationBackend) don't need a real tokenizer. Worker
    // adapter overrides to use the real model tokenizer bound to the job's ModelKey.
    Task<int> CountTokensAsync(string text, string? modelKey, CancellationToken ct = default)
        => Task.FromResult(Math.Max(1, (int)Math.Ceiling(text.Length / 3.25)));

    int GetEffectiveContextSize(string? modelKey)
        => AiModelCapabilities.Resolve(modelKey ?? string.Empty).ContextSize;
}
