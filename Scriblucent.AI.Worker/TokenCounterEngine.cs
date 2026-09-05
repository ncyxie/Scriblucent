using System.Text;
using LLama;

namespace Scriblucent.AI.Worker;

internal sealed class TokenCounterEngine : IDisposable
{
    private LLamaWeights? _weights;
    private bool _disposed;

    public bool IsLoaded => _weights is not null && !_disposed;

    public void SetWeights(LLamaWeights weights)
    {
        _weights = weights;
    }

    public int CountTokens(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_weights is null)
            throw new InvalidOperationException("No model loaded. Cannot count tokens.");

        var tokens = _weights.Tokenize(text, false, false, Encoding.UTF8);
        return tokens.Length;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _weights = null;
    }
}
