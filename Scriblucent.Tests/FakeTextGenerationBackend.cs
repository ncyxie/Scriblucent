using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

internal sealed class FakeTextGenerationBackend : ITextGenerationBackend
{
    private readonly Queue<AiGenerationResponse> _responses = new();
    private bool _disposed;

    public int OpenCallCount { get; private set; }
    public int GenerateCallCount { get; private set; }
    public List<AiGenerationRequest> Requests { get; } = new();

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

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        OpenCallCount++;
        return Task.CompletedTask;
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

    public void Dispose()
    {
        _disposed = true;
    }
}
