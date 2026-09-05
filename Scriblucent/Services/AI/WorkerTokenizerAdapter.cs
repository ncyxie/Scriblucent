namespace Scriblucent.Services.AI;

internal sealed class WorkerTokenizerAdapter : ITokenCounter
{
    private readonly LLamaSharpWorkerAdapter _worker;
    private readonly string _modelKey;

    public WorkerTokenizerAdapter(LLamaSharpWorkerAdapter worker, string modelKey)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _modelKey = modelKey ?? throw new ArgumentNullException(nameof(modelKey));
    }

    public Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        return _worker.CountTokensAsync(text, _modelKey, ct);
    }
}
