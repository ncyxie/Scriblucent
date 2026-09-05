using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Scriblucent.AI.Worker;

internal sealed class LLamaInferenceEngine : IDisposable
{
    private LLamaWeights? _model;
    private ModelParams? _parameters;
    private string _loadedModelPath = "";
    private bool _disposed;

    public bool IsLoaded => _model is not null && !_disposed;

    public string LoadedModelPath => _loadedModelPath;

    public LLamaWeights? Weights => _model;

    public Task LoadAsync(string modelPath, int contextSize = 4096, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_model is not null)
            Unload();

        _parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = 0,
            Threads = Math.Max(1, Environment.ProcessorCount - 1)
        };

        _model = LLamaWeights.LoadFromFile(_parameters);
        _loadedModelPath = modelPath;

        return Task.CompletedTask;
    }

    public async Task<(string Output, int GeneratedChars, long ElapsedMs)> GenerateAsync(
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

        if (_model is null || _parameters is null)
            throw new InvalidOperationException("No model loaded. Call LoadAsync first.");

        var sw = Stopwatch.StartNew();
        var output = new System.Text.StringBuilder();
        var generatedChars = 0;

        var grammarObj = !string.IsNullOrWhiteSpace(grammar)
            ? new Grammar(grammar, grammarRoot ?? "root")
            : null;

        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = (float)temperature,
            TopP = (float)topP,
            TopK = topK,
            PresencePenalty = (float)presencePenalty,
            RepeatPenalty = (float)repeatPenalty,
            Grammar = grammarObj
        };

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = new List<string> { "<|im_end|>" },
            SamplingPipeline = pipeline
        };

        // Qwen is chat/instruct-tuned - StatelessExecutor sends the prompt as raw continuation
        // text unless explicitly told to wrap it in the model's chat template.
        var executor = new StatelessExecutor(_model, _parameters)
        {
            ApplyTemplate = true
        };

        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
        {
            output.Append(token);
            generatedChars += token.Length;

            if (onToken is not null)
                await onToken(token, generatedChars);
        }

        sw.Stop();
        return (output.ToString(), generatedChars, sw.ElapsedMilliseconds);
    }

    public void Unload()
    {
        // Snapshot-then-null-then-dispose: if Dispose() throws (e.g. disposing a LLamaWeights
        // whose StatelessExecutor was cancelled mid-InferAsync, leaving native context state in
        // an unexpected shape), the fields are already cleared by the time the exception
        // propagates. Without this ordering, a throwing Dispose() would leave `_model` non-null
        // and IsLoaded reporting true for an object that's actually disposed/broken - a later
        // reconnect would then see IsLoaded==true, skip reloading, and hand back a dead model.
        var model = _model;
        _model = null;
        _parameters = null;
        _loadedModelPath = "";
        model?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Unload();
    }
}
