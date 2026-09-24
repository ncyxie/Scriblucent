using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

internal sealed class FakeTextGenerationBackend : ITextGenerationBackend
{
    private readonly Queue<AiGenerationResponse> _responses = new();
    private bool _disposed;

    public int GenerateCallCount { get; private set; }
    public List<AiGenerationRequest> Requests { get; } = new();
    public int? TokenCountOverride { get; set; }
    public int EffectiveContextSize { get; set; } = 8192;

    public void EnqueueResponse(string output)
    {
        _responses.Enqueue(new AiGenerationResponse(
            Output: output,
            GeneratedChars: output.Length,
            Elapsed: TimeSpan.FromMilliseconds(50)));
    }

    public void EnqueueResponses(params string[] outputs)
    {
        foreach (var output in outputs)
            EnqueueResponse(output);
    }

    public Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GenerateCallCount++;
        Requests.Add(request);

        if (_responses.Count == 0)
            throw new InvalidOperationException("No more canned responses in FakeTextGenerationBackend.");

        return Task.FromResult(_responses.Dequeue());
    }

    public Task<int> CountTokensAsync(string text, string? modelKey, CancellationToken ct = default)
        => Task.FromResult(TokenCountOverride
            ?? Math.Max(1, (int)Math.Ceiling(text.Length / 3.25)));

    public int GetEffectiveContextSize(string? modelKey) => EffectiveContextSize;

    public void Dispose()
    {
        _disposed = true;
    }
}
