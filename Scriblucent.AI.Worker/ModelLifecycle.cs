using LLama;

namespace Scriblucent.AI.Worker;

internal sealed class ModelLifecycle : IDisposable
{
    private readonly LLamaInferenceEngine _engine = new();
    private string _currentModelPath = "";
    private bool _disposed;

    public bool IsLoaded => _engine.IsLoaded;
    public string CurrentModelPath => _currentModelPath;
    public LLamaWeights? Weights => _engine.Weights;

    public async Task LoadModelAsync(string modelPath, int contextSize = 4096, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.Equals(_currentModelPath, modelPath, StringComparison.OrdinalIgnoreCase) && _engine.IsLoaded)
            return;

        _engine.Unload();
        await _engine.LoadAsync(modelPath, contextSize, ct);
        _currentModelPath = modelPath;
    }

    public Task<(string Output, int GeneratedChars, long ElapsedMs)> GenerateAsync(
        string prompt,
        int maxTokens,
        double temperature,
        double topP,
        int topK,
        double presencePenalty,
        double repeatPenalty,
        string? grammar = null,
        string? grammarRoot = null,
        Func<string, int, Task>? onToken = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_engine.IsLoaded)
            throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

        return _engine.GenerateAsync(
            prompt, maxTokens, temperature, topP, topK,
            presencePenalty, repeatPenalty, grammar, grammarRoot, onToken, ct);
    }

    public void UnloadModel()
    {
        _engine.Unload();
        _currentModelPath = "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _engine.Dispose();
    }
}
