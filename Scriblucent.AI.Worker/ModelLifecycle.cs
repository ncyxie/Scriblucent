using LLama;

namespace Scriblucent.AI.Worker;

internal sealed class ModelLifecycle : IDisposable
{
    private readonly LLamaInferenceEngine _engine;
    private string _currentModelPath = "";
    private int _currentContextSize;
    private bool _disposed;

    public ModelLifecycle(bool useGpu)
    {
        _engine = new LLamaInferenceEngine(useGpu);
        Backend = useGpu ? "vulkan" : "cpu";
    }

    public string Backend { get; }

    public bool IsLoaded => _engine.IsLoaded;
    public string CurrentModelPath => _currentModelPath;
    public LLamaWeights? Weights => _engine.Weights;
    public int CurrentContextSize => _currentContextSize;

    public async Task LoadModelAsync(string modelPath, int contextSize = 4096, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.Equals(_currentModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
            && _currentContextSize == contextSize
            && _engine.IsLoaded)
            return;

        _engine.Unload();
        _currentModelPath = "";
        _currentContextSize = 0;
        await _engine.LoadAsync(modelPath, contextSize, ct);
        _currentModelPath = modelPath;
        _currentContextSize = contextSize;
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
        _currentContextSize = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _engine.Dispose();
    }
}
