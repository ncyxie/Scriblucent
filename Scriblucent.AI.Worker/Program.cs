using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LLama.Native;
using Scriblucent.AI.Protocol;

namespace Scriblucent.AI.Worker;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var pipeName = GetPipeName(args);
        var requestedBackend = GetArgument(args, "--backend") ?? "cpu";
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("Usage: Scriblucent.AI.Worker.exe --pipe <pipeName>");
            return 1;
        }

        var useVulkan = string.Equals(requestedBackend, "vulkan", StringComparison.OrdinalIgnoreCase)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;

        NativeLibraryConfig.All
            .WithCuda(false)
            .WithVulkan(useVulkan)
            .WithAutoFallback(!useVulkan);

        try
        {
            await RunPipeServerAsync(pipeName, useVulkan);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Worker fatal error: {ex.Message}");
            return 1;
        }
    }

    private static string GetPipeName(string[] args) => GetArgument(args, "--pipe") ?? "";

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static bool IsValidPipeName(string pipeName)
    {
        // Host generates scriblucent-ai-worker-{Guid:N} (32 hex chars). Enforce to prevent
        // directory-junction style abuse when worker is invoked manually.
        if (string.IsNullOrWhiteSpace(pipeName))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            pipeName,
            @"^scriblucent-ai-worker-[0-9a-fA-F]{32}$");
    }

    private static bool IsAllowedModelPath(string modelPath)
    {
        try
        {
            var full = Path.GetFullPath(modelPath);
            var cacheRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent", "AiCache", "models"));
            var assetsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Models"));

            return IsWithinRoot(full, cacheRoot)
                || IsWithinRoot(full, assetsRoot);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static async Task RunPipeServerAsync(string pipeName, bool useVulkan)
    {
        if (!IsValidPipeName(pipeName))
            throw new ArgumentException($"Invalid pipe name '{pipeName}'.", nameof(pipeName));

        using var lifecycle = new ModelLifecycle(useVulkan);
        using var tokenCounter = new TokenCounterEngine();

        while (true)
        {
            await using var server = new System.IO.Pipes.NamedPipeServerStream(
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var keepRunning = await HandleClientAsync(
                server,
                lifecycle,
                tokenCounter);
            if (!keepRunning)
                break;
        }
    }

    /// <returns>true to keep the process alive and wait for a new connection.</returns>
    private static async Task<bool> HandleClientAsync(
        System.IO.Pipes.NamedPipeServerStream pipe,
        ModelLifecycle lifecycle,
        TokenCounterEngine tokenCounter)
    {
        using var cts = new CancellationTokenSource();
        var reader = new WorkerLineReader(pipe);

        try
        {
            while (pipe.IsConnected)
            {
                var request = await reader.ReadRequestAsync(cts.Token);
                if (request is null)
                    break;

                switch (request)
                {
                    case WorkerStartRequest start:
                        await HandleStartAsync(
                            pipe,
                            lifecycle,
                            tokenCounter,
                            start,
                            cts.Token);
                        break;

                    case WorkerGenerateRequest generate:
                        await HandleGenerateAsync(pipe, lifecycle, generate, cts.Token);
                        break;

                    case WorkerStopRequest:
                        lifecycle.UnloadModel();
                        return true;

                    case WorkerTokenizeRequest tokenize:
                        await HandleTokenizeAsync(pipe, tokenCounter, tokenize, cts.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            try
            {
                await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
                {
                    Message = ex.Message
                }, CancellationToken.None);
            }
            catch { /* pipe may be broken */ }
        }
        finally
        {
            // A throwing cleanup here must never take the whole process down with it - this
            // method's contract (see the doc comment above it) keeps the worker alive after a
            // broken connection or a cancelled-mid-generation model so it can accept the next
            // reconnect. An unhandled exception from a `finally` block propagates straight out of
            // this method (bypassing the catch clauses above, which have already run) and up
            // through RunPipeServerAsync's uncaught `await HandleClientAsync(...)` into Main's
            // top-level catch, which returns 1 and ends the process - silently, since stderr
            // isn't redirected. That would strand the host believing a reconnect is possible when
            // the process is actually gone, forcing it into a full cold respawn instead.
            try { lifecycle.UnloadModel(); }
            catch { /* best effort - see above */ }
        }

        return true;
    }

    private static async Task HandleStartAsync(
        System.IO.Pipes.NamedPipeServerStream pipe,
        ModelLifecycle lifecycle,
        TokenCounterEngine tokenCounter,
        WorkerStartRequest start,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(start.ModelPath))
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = "Model path is required."
            }, ct);
            return;
        }

        if (!File.Exists(start.ModelPath))
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = $"Model file not found: {start.ModelPath}"
            }, ct);
            return;
        }

        if (!IsAllowedModelPath(start.ModelPath))
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = $"Model path not allowed: {start.ModelPath}"
            }, ct);
            return;
        }

        try
        {
            await lifecycle.LoadModelAsync(start.ModelPath, start.ContextSize, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lifecycle.UnloadModel();
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = $"Could not allocate a {start.ContextSize}-token model context: {ex.Message}"
            }, ct);
            return;
        }

        if (lifecycle.Weights is not null)
            tokenCounter.SetWeights(lifecycle.Weights);

        await WorkerWire.WriteResponseAsync(pipe, new WorkerStartedResponse
        {
            Backend = lifecycle.Backend,
            ContextSize = lifecycle.CurrentContextSize
        }, ct);
    }

    private static async Task HandleGenerateAsync(
        System.IO.Pipes.NamedPipeServerStream pipe,
        ModelLifecycle lifecycle,
        WorkerGenerateRequest generate,
        CancellationToken ct)
    {
        if (!lifecycle.IsLoaded)
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                RequestId = generate.RequestId,
                Message = "No model loaded. Send start request first."
            }, ct);
            return;
        }

        // Server-side clamp prevents a malicious or buggy client from pinning CPU/RAM with huge MaxTokens.
        generate = generate with { MaxTokens = Math.Clamp(generate.MaxTokens, 64, 4096) };

        // `ct` is connection-scoped (HandleClientAsync's `cts`), not request-scoped - the host
        // cancelling a request only stops the HOST from waiting on it; it doesn't disconnect
        // this pipe, and this loop isn't reading anything else from it while generation runs, so
        // by itself it would never notice the host gave up and just keeps generating regardless.
        // The disconnectCts below is what actually stops it: WatchForDisconnectAsync issues a
        // phantom read that only ever completes when the client end genuinely goes away (the
        // host never legitimately sends anything mid-generation), and cancels disconnectCts when
        // that happens - which IS the same token threaded into GenerateAsync/InferAsync below, so
        // a real disconnect now stops token generation within about one token, instead of running
        // to MaxTokens regardless of whether anyone is still listening.
        using var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchForDisconnectAsync(pipe, watcherCts.Token, disconnectCts);
        var pendingTokens = new StringBuilder();
        var pendingTokenCount = 0;
        var lastTokenFlush = Stopwatch.StartNew();

        async Task FlushTokensAsync(int generatedChars)
        {
            if (pendingTokens.Length == 0)
                return;

            var text = pendingTokens.ToString();
            pendingTokens.Clear();
            pendingTokenCount = 0;
            lastTokenFlush.Restart();
            await WorkerWire.WriteResponseAsync(pipe, new WorkerTokenResponse
            {
                RequestId = generate.RequestId,
                Text = text,
                GeneratedChars = generatedChars
            }, disconnectCts.Token);
        }

        try
        {
            var (output, generatedChars, elapsedMs) = await lifecycle.GenerateAsync(
                generate.Prompt,
                generate.MaxTokens,
                generate.Temperature,
                generate.TopP,
                generate.TopK,
                generate.PresencePenalty,
                generate.RepeatPenalty,
                generate.Grammar,
                generate.GrammarRoot,
                onToken: async (token, chars) =>
                {
                    pendingTokens.Append(token);
                    pendingTokenCount++;
                    if (pendingTokenCount >= 8 || lastTokenFlush.Elapsed >= TimeSpan.FromMilliseconds(100))
                        await FlushTokensAsync(chars);
                },
                disconnectCts.Token);

            await FlushTokensAsync(generatedChars);
            await WorkerWire.WriteResponseAsync(pipe, new WorkerGenerateCompleteResponse
            {
                RequestId = generate.RequestId,
                Output = output,
                GeneratedChars = generatedChars,
                ElapsedMs = elapsedMs
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Harmless no-op if this is the disconnect case (the pipe is already gone by now,
            // same as any other write attempted after disconnect) - same existing behavior as a
            // host-initiated cancel.
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                RequestId = generate.RequestId,
                Message = "Generation cancelled."
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                RequestId = generate.RequestId,
                Message = ex.Message
            }, CancellationToken.None);
        }
        finally
        {
            watcherCts.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
        }
    }

    /// Named pipes give no event for "the other end disconnected" - a ReadAsync on a byte-mode
    /// pipe only ever completes (with 0 bytes, or an exception) when the pipe actually closes, so
    /// it's the reliable way to detect it. Safe to run concurrently with HandleGenerateAsync's
    /// WRITES above: reads and writes are independent directions on a duplex stream: the risk to
    /// avoid is two concurrent reads, not a read alongside writes. The host never legitimately
    /// sends anything while a generate request is in flight, so this read only ever completes for
    /// one reason - the client is gone - except when `watchToken` cancels it first because
    /// generation finished normally, which is the expected/non-error exit.
    private static async Task WatchForDisconnectAsync(
        System.IO.Pipes.NamedPipeServerStream pipe, CancellationToken watchToken, CancellationTokenSource disconnectCts)
    {
        try
        {
            var buffer = new byte[1];
            // Deliberately not checking the returned byte count (CA2022's "inexact read") - this
            // is a phantom read, not a real one. Any completion at all, 0 bytes or 1, means the
            // pipe surfaced *something* other than "still connected and idle", which is enough.
            _ = await pipe.ReadAsync(buffer, watchToken);
            if (!watchToken.IsCancellationRequested)
                disconnectCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Normal: generation finished first and the finally block cancelled watchToken.
        }
        catch
        {
            disconnectCts.Cancel();
        }
    }

    private static async Task HandleTokenizeAsync(
        System.IO.Pipes.NamedPipeServerStream pipe,
        TokenCounterEngine tokenCounter,
        WorkerTokenizeRequest tokenize,
        CancellationToken ct)
    {
        if (!tokenCounter.IsLoaded)
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = "No model loaded. Cannot tokenize."
            }, ct);
            return;
        }

        try
        {
            var count = tokenCounter.CountTokens(tokenize.Text);
            await WorkerWire.WriteResponseAsync(pipe, new WorkerTokenizeResponse
            {
                TokenCount = count
            }, ct);
        }
        catch (Exception ex)
        {
            await WorkerWire.WriteResponseAsync(pipe, new WorkerErrorResponse
            {
                Message = ex.Message
            }, ct);
        }
    }
}
