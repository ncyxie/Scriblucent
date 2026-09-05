namespace Scriblucent.Services.AI;

/// <summary>
/// App-scoped singleton for the AI worker. Previous per-tab <see cref="LLamaSharpWorkerAdapter"/>
/// meant N tabs = N worker processes and N × model memory (each ~0.5–2.6GB). The global
/// <c>IsAiGenerationInProgress</c> only gated the Convert button, not process count - two tabs
/// could race to <c>StartWorkerAsync</c> before the flag propagated and each load the model.
/// This host ensures one process, one model load, serialized via the adapter's own
/// <c>SemaphoreSlim</c> (so concurrent tab requests queue rather than spawn).
/// Disposal is app-lifetime only; per-tab <c>DisposeHostedEditor</c> no longer tears it down.
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
