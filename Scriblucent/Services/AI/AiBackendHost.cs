namespace Scriblucent.Services.AI;

/// <summary>
/// App-scoped AI worker adapter. Requests from editor tabs are serialized by the adapter;
/// the worker is disposed when the app exits.
/// </summary>
internal static class AiBackendHost
{
    private static readonly LLamaSharpWorkerAdapter _shared = new();
    private static bool _shutdown;

    public static LLamaSharpWorkerAdapter Shared
    {
        get
        {
            if (_shutdown)
                throw new ObjectDisposedException(nameof(AiBackendHost));
            return _shared;
        }
    }

    public static void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        try { _shared.Dispose(); } catch { }
    }
}
