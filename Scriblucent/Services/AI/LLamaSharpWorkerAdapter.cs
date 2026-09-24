using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Scriblucent.AI.Protocol;

namespace Scriblucent.Services.AI;

internal sealed class LLamaSharpWorkerAdapter : ITextGenerationBackend
{
    private static readonly TimeSpan IdleUnloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InferenceStallTimeout = TimeSpan.FromSeconds(45);

    private enum WorkerBackend
    {
        Cpu,
        Vulkan
    }

    private Process? _workerProcess;
    private NamedPipeClientStream? _pipe;
    private WorkerLineReader? _reader;
    private string _pipeName = "";
    private string _loadedModelPath = "";
    private int _loadedContextSize;
    private WorkerBackend _activeBackend = WorkerBackend.Cpu;
    private bool _vulkanUnavailableForSession;
    private bool _disposed;
    private readonly bool? _useGpuAccelerationOverride;

    private readonly SemaphoreSlim _pipeLock = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;

    public LLamaSharpWorkerAdapter(bool? useGpuAccelerationOverride = null)
    {
        _useGpuAccelerationOverride = useGpuAccelerationOverride;
        _idleTimer = new System.Threading.Timer(OnIdleTimerFired, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal string ActiveBackendName => _activeBackend.ToString().ToLowerInvariant();

    public int GetEffectiveContextSize(string? modelKey)
    {
        var requested = AiModelCapabilities.Resolve(modelKey ?? string.Empty).ContextSize;
        return _loadedContextSize > 0 ? Math.Min(requested, _loadedContextSize) : requested;
    }

    public async Task<int> CountTokensAsync(string text, string? modelKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var resolvedModelKey = ResolveModelKey(modelKey);
        var modelPath = ResolveModelPath(resolvedModelKey);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidOperationException($"Model file not found for key '{resolvedModelKey}'.");

        // Tokenization is quick; 60s overall timeout prevents a hung worker from blocking
        // counting forever (mirrors the generate-path stall guard below, but shorter).
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedCt = linkedCts.Token;

        await _pipeLock.WaitAsync(linkedCt);
        try
        {
            await EnsureWorkerReadyAsync(modelPath, resolvedModelKey, progress: null, linkedCt);
            ThrowIfDisposedAfterEnsureReady();

            try
            {
                var tokenizeRequest = new WorkerTokenizeRequest { Text = text };
                await WorkerWire.WriteRequestAsync(_pipe!, tokenizeRequest, linkedCt);

                var readTask = _reader!.ReadResponseAsync(linkedCt);
                var stallTask = Task.Delay(InferenceStallTimeout, linkedCt);
                var completed = await Task.WhenAny(readTask, stallTask);
                if (completed == stallTask && !linkedCt.IsCancellationRequested)
                    throw new TimeoutException($"Tokenization stalled - no response for {InferenceStallTimeout.TotalSeconds:F0}s.");

                var response = await readTask;
                if (response is WorkerTokenizeResponse tokenizeResponse)
                    return tokenizeResponse.TokenCount;

                if (response is WorkerErrorResponse error)
                    throw new InvalidOperationException($"Worker tokenize error: {error.Message}");

                throw new InvalidOperationException($"Unexpected worker response: {response?.GetType().Name ?? "null"}");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                PoisonConnection();
                throw new TimeoutException("Tokenization timed out after 60s.");
            }
            catch
            {
                // Any failure while talking to the worker (including cancellation) may have left
                // the wire desynchronized, or the worker may still be writing a response nobody
                // is waiting for anymore - never reuse this connection. The next call reconnects
                // (and reloads the model) from scratch rather than risk reading a stale response.
                PoisonConnection();
                throw;
            }
        }
        finally
        {
            RearmIdleTimer();
            _pipeLock.Release();
        }
    }

    public async Task<AiGenerationResponse> GenerateAsync(AiGenerationRequest request)
    {
        try
        {
            return await GenerateOnceAsync(request);
        }
        catch (Exception ex) when (IsRecoverableVulkanFailure(ex, request.CancellationToken))
        {
            await DisableVulkanForSessionAsync(request.CancellationToken);
            request.Progress?.Report(new AiGenerationProgress(AiGenerationStatus.FallingBackToCpu));
            return await GenerateOnceAsync(request);
        }
    }

    private async Task<AiGenerationResponse> GenerateOnceAsync(AiGenerationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The coordinator sets ModelKey once so every planning call targets the same model.
        var resolvedModelKey = ResolveModelKey(request.ModelKey);
        var modelPath = ResolveModelPath(resolvedModelKey);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidOperationException($"Model file not found for key '{resolvedModelKey}'. Ensure the model is downloaded.");

        var inferenceTimeout = GetInferenceTimeout(resolvedModelKey);
        using var overallTimeoutCts = new CancellationTokenSource(inferenceTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, overallTimeoutCts.Token);
        var ct = linkedCts.Token;

        await _pipeLock.WaitAsync(ct);
        try
        {
            await EnsureWorkerReadyAsync(modelPath, resolvedModelKey, request.Progress, ct);
            ThrowIfDisposedAfterEnsureReady();

            try
            {
                // Send generate request
                var requestId = Guid.NewGuid().ToString("N");
                var generateRequest = new WorkerGenerateRequest
                {
                    RequestId = requestId,
                    Prompt = request.Prompt,
                    MaxTokens = request.MaxTokens,
                    Temperature = request.Temperature,
                    TopP = request.TopP,
                    TopK = request.TopK,
                    PresencePenalty = request.PresencePenalty,
                    RepeatPenalty = request.RepetitionPenalty,
                    Grammar = request.Grammar,
                    GrammarRoot = request.GrammarRoot,
                };

                await WorkerWire.WriteRequestAsync(_pipe!, generateRequest, ct);

                // Read responses until complete or error. Cancelling a request doesn't tell the
                // worker to stop generating (it isn't wired to observe per-request cancellation),
                // so a still-in-flight worker can keep writing responses for an abandoned request
                // after we've moved on - only accept responses that carry this call's RequestId.
                while (true)
                {
                    // If the worker stops sending tokens without closing the pipe, fail before
                    // the overall model-specific inference timeout expires.
                    var readTask = _reader!.ReadResponseAsync(ct);
                    var stallTask = Task.Delay(InferenceStallTimeout, ct);
                    var completed = await Task.WhenAny(readTask, stallTask);
                    if (completed == stallTask)
                    {
                        // Don't let the still-pending readTask continue to observe ct after we throw
                        // - it would surface as an unobserved OperationCanceledException later.
                        if (overallTimeoutCts.IsCancellationRequested)
                            throw new TimeoutException($"AI generation timed out after {inferenceTimeout.TotalMinutes:F0} minutes.");

                        throw new TimeoutException($"AI generation stalled - no output for {InferenceStallTimeout.TotalSeconds:F0}s.");
                    }

                    var response = await readTask;
                    if (response is null)
                        throw new InvalidOperationException("Worker pipe closed unexpectedly.");

                    var responseRequestId = response switch
                    {
                        WorkerTokenResponse token => token.RequestId,
                        WorkerGenerateCompleteResponse complete => complete.RequestId,
                        WorkerErrorResponse error => error.RequestId,
                        _ => null
                    };
                    if (responseRequestId is not null && !string.Equals(responseRequestId, requestId, StringComparison.Ordinal))
                        continue;

                    switch (response)
                    {
                        case WorkerTokenResponse token:
                            try
                            {
                                request.TokenObserver?.Invoke(token.Text);
                            }
                            catch
                            {
                                // A tracing observer must never be able to break generation.
                            }

                            request.Progress?.Report(new AiGenerationProgress(
                                AiGenerationStatus.Processing,
                                GeneratedChars: token.GeneratedChars));
                            break;

                        case WorkerGenerateCompleteResponse complete:
                            return new AiGenerationResponse(
                                Output: complete.Output,
                                GeneratedChars: complete.GeneratedChars,
                                Elapsed: TimeSpan.FromMilliseconds(complete.ElapsedMs));

                        case WorkerErrorResponse error:
                            throw new InvalidOperationException($"Worker error: {error.Message}");

                        default:
                            throw new InvalidOperationException($"Unexpected worker response type: {response.GetType().Name}");
                    }
                }
            }
            catch (OperationCanceledException) when (overallTimeoutCts.IsCancellationRequested && !request.CancellationToken.IsCancellationRequested)
            {
                PoisonConnection();
                throw new TimeoutException($"AI generation timed out after {inferenceTimeout.TotalMinutes:F0} minutes.");
            }
            catch
            {
                // See CountTokensAsync - poison on any failure so a cancelled/broken request can
                // never bleed a stale response into a later, unrelated call on this adapter.
                PoisonConnection();
                throw;
            }
        }
        finally
        {
            RearmIdleTimer();
            _pipeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stops future idle callbacks from starting; does not abort one already running.
        _idleTimer.Dispose();

        // Non-blocking probe: Dispose() may run on the UI thread during tab-close, so it must not
        // block waiting for an in-flight generate/tokenize/start call to release the lock -
        // ForceTeardownCurrentWorker() below runs either way, best-effort, against whatever is
        // currently in the fields. `lockAcquired` only decides whether we also release the lock.
        // Deliberately not disposing _pipeLock itself, to avoid a teardown race with a
        // concurrently-firing idle callback. If a StartWorkerAsync that's still holding the lock
        // assigns a live process/pipe *after* this snapshot, ThrowIfDisposedAfterEnsureReady()
        // (called right after EnsureWorkerReadyAsync, still under that same lock) catches it and
        // tears it down instead of leaving it leaked and unmanaged.
        var lockAcquired = _pipeLock.Wait(0);
        try
        {
            ForceTeardownCurrentWorker();
        }
        finally
        {
            if (lockAcquired)
                _pipeLock.Release();
        }
    }

    /// Snapshot-then-null and tear down whatever is currently assigned to _pipe/_workerProcess -
    /// shared by Dispose() and ThrowIfDisposedAfterEnsureReady() so both close the connection and
    /// kill the process the same way. Caller must hold _pipeLock (or, for Dispose's best-effort
    /// path, accept that it might not).
    private void ForceTeardownCurrentWorker()
    {
        // Snapshot-then-null: a racing in-flight call reading the fields sees either the
        // fully-intact original state or a clean null, never a half-torn-down object. If it
        // does race in after this point, its own try/catch (see GenerateAsync/CountTokensAsync)
        // treats the resulting exception as "poison and fail" - a normal, handled failure, not
        // a corrupted shared state.
        var pipe = _pipe;
        _pipe = null;
        _reader = null;
        var process = _workerProcess;
        _workerProcess = null;
        _loadedModelPath = "";
        _loadedContextSize = 0;

        // No WaitForExit grace period here - this path can
        // run concurrently with an in-flight GenerateAsync still reading this same pipe on another
        // thread (Dispose() doesn't wait for the lock), and the worker can't even read a shutdown
        // message while it's mid-generation anyway (it's not back at its request loop yet). Since
        // we're tearing this down unconditionally regardless of what the worker is doing, skip
        // straight to a forceful kill instead of asking nicely first and waiting up to 3s for a
        // cooperative exit that may never come.
        pipe?.Dispose();

        if (process is { HasExited: false })
        {
            try { process.Kill(true); }
            catch { /* best effort */ }
        }

        process?.Dispose();
    }

    /// Closes the gap Dispose()'s non-blocking lock probe can miss: Dispose() doesn't wait for
    /// an in-flight StartWorkerAsync/ReconnectWorkerAsync, so it can snapshot _pipe/_workerProcess
    /// as null, tear down nothing, and return - while that in-flight call goes on to assign a
    /// live process/pipe to an adapter that's now permanently _disposed and will never call
    /// cleanup again. Called immediately after EnsureWorkerReadyAsync, still holding _pipeLock:
    /// by then any concurrent Dispose() has already set _disposed (that happens synchronously,
    /// before Dispose even tries the lock), so if it's true here, this call itself tears down
    /// whatever was just started instead of leaving it assigned and unmanaged.
    private void ThrowIfDisposedAfterEnsureReady()
    {
        if (!_disposed)
            return;

        ForceTeardownCurrentWorker();
        throw new ObjectDisposedException(nameof(LLamaSharpWorkerAdapter));
    }

    private string ResolveModelKey(string? modelKey)
    {
        if (!string.IsNullOrWhiteSpace(modelKey))
        {
            if (AiModelManifest.Instance.FindByKey(modelKey) is not null)
            {
                return modelKey;
            }

            throw new ArgumentException($"Unknown AI model key '{modelKey}'.", nameof(modelKey));
        }

        return AiModelManifest.Instance.GetSelectedModel().Key;
    }

    private static string? ResolveModelPath(string modelKey)
        => BundledModelHostService.GetModelFilePath(modelKey);

    /// Best-effort teardown of a connection that may be desynchronized or partially broken -
    /// caller must hold _pipeLock. Deliberately does not touch _workerProcess: a poisoned pipe
    /// still gets a fresh reconnect attempt (cheaper than a full respawn) via EnsureWorkerReadyAsync.
    private void PoisonConnection()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _reader = null;
    }

    /// Caller must hold _pipeLock.
    private async Task EnsureWorkerReadyAsync(
        string modelPath,
        string modelKey,
        IProgress<AiGenerationProgress>? progress,
        CancellationToken ct)
    {
        var desiredBackend = GetDesiredBackend();
        if (_workerProcess is { HasExited: false } && _activeBackend != desiredBackend)
            ForceTeardownCurrentWorker();

        if (_workerProcess is null || _workerProcess.HasExited)
        {
            try
            {
                await StartWorkerAsync(modelPath, modelKey, desiredBackend, ct);
            }
            catch when (desiredBackend == WorkerBackend.Vulkan && !ct.IsCancellationRequested)
            {
                ForceTeardownCurrentWorker();
                _vulkanUnavailableForSession = true;
                progress?.Report(new AiGenerationProgress(AiGenerationStatus.FallingBackToCpu));
                await StartWorkerAsync(modelPath, modelKey, WorkerBackend.Cpu, ct);
            }
            return;
        }

        if (_pipe is null || !_pipe.IsConnected)
        {
            await ReconnectWorkerAsync(modelPath, modelKey, ct);
            return;
        }

        if (!string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            await LoadModelAsync(modelPath, modelKey, ct);
    }

    private async Task StartWorkerAsync(string modelPath, string modelKey, WorkerBackend backend, CancellationToken ct)
    {
        // Reached when the previous worker process crashed/exited on its own (EnsureWorkerReadyAsync's
        // HasExited check) rather than through PoisonConnection/Dispose - dispose whatever's left
        // over before replacing it, matching ReconnectWorkerAsync's equivalent cleanup.
        _pipe?.Dispose();
        _pipe = null;
        _reader = null;
        _workerProcess?.Dispose();
        _workerProcess = null;

        _pipeName = $"scriblucent-ai-worker-{Guid.NewGuid():N}";

        var workerExePath = Path.Combine(AppContext.BaseDirectory, "AIWorker", "Scriblucent.AI.Worker.exe");
        if (!File.Exists(workerExePath))
            throw new FileNotFoundException("Worker executable not found.", workerExePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(_pipeName);
        startInfo.ArgumentList.Add("--backend");
        startInfo.ArgumentList.Add(backend.ToString().ToLowerInvariant());

        var process = new Process { StartInfo = startInfo };

        process.Start();
        WorkerProcessJobObject.Track(process);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* Some systems deny priority changes; inference can still proceed. */ }
        _workerProcess = process;
        _activeBackend = backend;

        // Connect to worker's named pipe
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.ConnectAsync(10000, ct);
        _pipe = pipe;
        _reader = new WorkerLineReader(pipe);

        var started = await StartModelWithContextFallbackAsync(pipe, _reader, modelPath, modelKey, ct);
        ValidateStartedBackend(started, backend);
        _loadedModelPath = modelPath;
        _loadedContextSize = started.ContextSize;
    }

    /// Reconnects to an already-running worker process (post idle-unload) on its existing
    /// pipe name, instead of spawning a new process - see Program.cs's RunPipeServerAsync,
    /// which keeps listening on the same pipe name after a WorkerStopRequest unloads the model.
    private async Task ReconnectWorkerAsync(string modelPath, string modelKey, CancellationToken ct)
    {
        _pipe?.Dispose();
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.ConnectAsync(10000, ct);
        _pipe = pipe;
        _reader = new WorkerLineReader(pipe);

        var started = await StartModelWithContextFallbackAsync(pipe, _reader, modelPath, modelKey, ct);
        ValidateStartedBackend(started, _activeBackend);
        _loadedModelPath = modelPath;
        _loadedContextSize = started.ContextSize;
    }

    private async Task LoadModelAsync(string modelPath, string modelKey, CancellationToken ct)
    {
        var started = await StartModelWithContextFallbackAsync(_pipe!, _reader!, modelPath, modelKey, ct);
        ValidateStartedBackend(started, _activeBackend);
        _loadedModelPath = modelPath;
        _loadedContextSize = started.ContextSize;
    }

    private static async Task<WorkerStartedResponse> StartModelWithContextFallbackAsync(
        Stream pipe,
        WorkerLineReader reader,
        string modelPath,
        string modelKey,
        CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var contextSize in AiModelCapabilities.Resolve(modelKey).ContextFallbackSizes)
        {
            try
            {
                await WorkerWire.WriteRequestAsync(pipe, new WorkerStartRequest
                {
                    ModelPath = modelPath,
                    ContextSize = contextSize
                }, ct);
                return await WaitForStartedResponseAsync(reader, ct);
            }
            catch (InvalidOperationException ex) when (contextSize > 4096 && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Worker could not load the requested model context.");
    }

    private static async Task<WorkerStartedResponse> WaitForStartedResponseAsync(WorkerLineReader reader, CancellationToken ct)
    {
        while (true)
        {
            var response = await reader.ReadResponseAsync(ct);
            if (response is null)
                throw new InvalidOperationException("Worker closed pipe during startup.");

            switch (response)
            {
                case WorkerStartedResponse started:
                    return started;

                case WorkerErrorResponse error:
                    throw new InvalidOperationException($"Worker failed to start: {error.Message}");

                default:
                    throw new InvalidOperationException($"Unexpected response during startup: {response.GetType().Name}");
            }
        }
    }

    private WorkerBackend GetDesiredBackend()
    {
        var enabled = _useGpuAccelerationOverride ?? AppSettingsService.Load().UseGpuAcceleration;
        return enabled
            && !_vulkanUnavailableForSession
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? WorkerBackend.Vulkan
                : WorkerBackend.Cpu;
    }

    private static TimeSpan GetInferenceTimeout(string modelKey)
        => AiModelCapabilities.Resolve(modelKey).InferenceTimeout;

    private bool IsRecoverableVulkanFailure(Exception ex, CancellationToken requestCancellationToken)
        => _activeBackend == WorkerBackend.Vulkan
           && !_vulkanUnavailableForSession
           && !requestCancellationToken.IsCancellationRequested
           && ex is IOException or InvalidOperationException or TimeoutException;

    private async Task DisableVulkanForSessionAsync(CancellationToken ct)
    {
        await _pipeLock.WaitAsync(ct);
        try
        {
            _vulkanUnavailableForSession = true;
            ForceTeardownCurrentWorker();
        }
        finally
        {
            _pipeLock.Release();
        }
    }

    private static void ValidateStartedBackend(WorkerStartedResponse started, WorkerBackend expected)
    {
        var expectedName = expected.ToString().ToLowerInvariant();
        if (!string.Equals(started.Backend, expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Worker started with backend '{started.Backend}', expected '{expectedName}'.");
    }

    private void RearmIdleTimer()
    {
        try { _idleTimer.Change(IdleUnloadTimeout, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* Dispose() raced us; fine to drop */ }
    }

    private async void OnIdleTimerFired(object? state)
    {
        try
        {
            if (_disposed)
                return;

            // Non-blocking try-acquire: if a generate/tokenize call currently holds the lock,
            // just skip this attempt - that call's own `finally { RearmIdleTimer(); }` will
            // re-schedule idle-unload once it completes, so nothing is lost.
            if (!await _pipeLock.WaitAsync(0))
                return;

            try
            {
                if (!_disposed)
                    await UnloadIdleAsync();
            }
            finally
            {
                _pipeLock.Release();
            }
        }
        catch
        {
            // best effort; a timer callback must never throw
        }
    }

    /// Caller must hold _pipeLock. Unloads the model but deliberately leaves _workerProcess
    /// running so the next call can reconnect (ReconnectWorkerAsync) instead of paying a full
    /// process-spawn cost.
    private async Task UnloadIdleAsync()
    {
        if (_pipe is { IsConnected: true })
        {
            try
            {
                await WorkerWire.WriteRequestAsync(_pipe, new WorkerStopRequest(), CancellationToken.None);
            }
            catch { /* best effort; pipe may already be broken */ }
        }

        _pipe?.Dispose();
        _pipe = null;
        _reader = null;
        _loadedModelPath = "";
    }
}
