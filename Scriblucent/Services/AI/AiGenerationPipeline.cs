using Scriblucent.Localization;

namespace Scriblucent.Services.AI;

internal static class AiGenerationPipeline
{
    public static async Task<T> RunAsync<T>(
        string noteText,
        AiChunkingPurpose purpose,
        ITextGenerationBackend backend,
        AiGenerationCallbacks callbacks,
        IProgress<AiGenerationProgress>? progress,
        CancellationToken ct)
    {
        // TEMPORARY full-generation trace (see AiGenerationTracer): one log per job capturing
        // chunk plan, every call's prompt/grammar/sampling, live token streams, raw outputs,
        // parse/refusal verdicts, and the outcome - so real-model behaviour can be diagnosed
        // from evidence instead of theory.
        var tracer = AiGenerationTracer.StartJob(
            callbacks.DiagnosticTag?.Invoke(noteText) ?? "ai",
            purpose.ToString(),
            AiModelManifest.Instance.GetSelectedModel().Key,
            noteText);

        try
        {
            return await RunCoreAsync<T>(noteText, purpose, backend, callbacks, progress, ct,
                AiModelManifest.Instance.GetSelectedModel().Key, tracer);
        }
        catch (AiInputRejectedException ex)
        {
            tracer?.LogOutcome($"input-rejected: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            tracer?.LogOutcome($"failed: {ex.GetType().Name}: {ex.Message}");
            if (ex.Data.Contains("DiagnosticPath"))
                tracer?.LogOutcome($"parse-diagnostic-path: {ex.Data["DiagnosticPath"]}");
            throw;
        }
        finally
        {
            tracer?.Dispose();
        }
    }

    private static async Task<T> RunCoreAsync<T>(
        string noteText,
        AiChunkingPurpose purpose,
        ITextGenerationBackend backend,
        AiGenerationCallbacks callbacks,
        IProgress<AiGenerationProgress>? progress,
        CancellationToken ct,
        string modelKey,
        AiGenerationTracer? tracer)
    {
        AiInputGuard.EnsureSuitableStudyText(noteText);

        // DIP: backend itself provides token counts via ITextGenerationBackend.CountTokensAsync
        // (worker uses real tokenizer, Fake uses char estimate). No concrete type check.
        var tokenCounter = new BackendTokenCounter(backend, modelKey);

        var chunks = await AiChunkingPipeline.PrepareChunksAsync(
            noteText, purpose, tokenCounter, modelKey: modelKey, ct: ct);
        tracer?.LogChunkPlan(chunks);

        if (chunks.Count == 0)
        {
            tracer?.LogOutcome("zero chunks after chunking");
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));
        }

        var callCounter = 0;

        async Task<AiGenerationResponse> GenerateTracedAsync(string kind, AiGenerationRequest request)
        {
            var callId = callCounter++;
            tracer?.LogCallStart(callId, kind, request);
            try
            {
                var response = await backend.GenerateAsync(request);
                tracer?.LogCallEnd(callId, response);
                return response;
            }
            catch (Exception ex)
            {
                tracer?.LogCallAborted(callId, $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        var outputs = new List<string>();
        var repairedOutputs = new List<string>();
        var sawRefusal = false;
        var failedChunkIndexes = new List<int>();
        var successfulChunkCountPerChunk = 0;

        // Per-chunk repair mode (MindMap) handles repair immediately inside the loop so each
        // chunk's failure is isolated; batched mode (Flashcards/Quiz) collects failures and does
        // one combined repair covering all failed chunks together.
        if (callbacks.PerChunkRepair)
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var chunkProgress = CreateChunkProgress(progress, chunk.Index + 1, chunk.TotalCount);
                var prompt = callbacks.BuildChunkPrompt(chunk, chunks);
                var response = await GenerateTracedAsync($"chunk {chunk.Index + 1}/{chunks.Count}", new AiGenerationRequest(
                    Prompt: prompt,
                    MaxTokens: callbacks.MaxTokens,
                    Temperature: callbacks.Temperature,
                    TopP: callbacks.TopP,
                    TopK: callbacks.TopK,
                    PresencePenalty: callbacks.PresencePenalty,
                    RepetitionPenalty: callbacks.RepetitionPenalty,
                    Grammar: callbacks.BuildChunkGrammar(chunk),
                    GrammarRoot: callbacks.GrammarRoot,
                    Progress: chunkProgress,
                    CancellationToken: ct,
                    ModelKey: modelKey,
                    TokenObserver: tracer is null ? null : tracer.AppendStream));
                var output = response.Output;
                outputs.Add(output);
                var outputReason = AiInputGuard.ExplainRefusalOutput(output);

                // Parse FIRST: an output that yields items counts as success even when it also
                // contains refusal-looking text. Traced real-model runs show the model appending a
                // "REFUSE:"-phrased tail after perfectly good records (and even echoing the prompt's
                // own instruction sentence back); classifying before parsing threw those records
                // away as refusals.
                var added = await callbacks.OnChunkGenerated(output, chunks, chunk.Index);
                if (added > 0)
                {
                    successfulChunkCountPerChunk++;
                    tracer?.LogVerdict(chunk.Index + 1, added, false, AiInputGuard.RefusalReasonNone);
                    continue;
                }

                // Nothing parsed - an explicit refusal, refusal-looking text, and plain unparseable
                // output all take the same immediate per-chunk repair path.
                if (outputReason != AiInputGuard.RefusalReasonNone)
                    sawRefusal = true;
                tracer?.LogVerdict(chunk.Index + 1, 0, outputReason != AiInputGuard.RefusalReasonNone, outputReason);

                // Immediate per-chunk repair
                if (callbacks.BuildPerChunkRepairPrompt is not null)
                {
                    var repairPrompt = callbacks.BuildPerChunkRepairPrompt(chunk, output, chunks);
                    var repairGrammar = callbacks.BuildPerChunkRepairGrammar?.Invoke(chunk)
                        ?? callbacks.BuildChunkGrammar(chunk);
                    var repairProgress = CreateRepairProgress(progress, chunk.Index + 1, chunk.TotalCount);
                    var repairResponse = await GenerateTracedAsync($"repair chunk {chunk.Index + 1}/{chunks.Count}", new AiGenerationRequest(
                        Prompt: repairPrompt,
                        MaxTokens: callbacks.RepairMaxTokens > 0 ? callbacks.RepairMaxTokens : callbacks.MaxTokens,
                        Temperature: callbacks.RepairTemperature,
                        TopP: callbacks.RepairTopP,
                        TopK: callbacks.RepairTopK,
                        PresencePenalty: callbacks.RepairPresencePenalty,
                        RepetitionPenalty: callbacks.RepairRepetitionPenalty,
                        Grammar: repairGrammar,
                        GrammarRoot: callbacks.GrammarRoot,
                        Progress: repairProgress,
                        CancellationToken: ct,
                        ModelKey: modelKey,
                        TokenObserver: tracer is null ? null : tracer.AppendStream));
                    var repaired = repairResponse.Output;
                    repairedOutputs.Add(repaired);

                    // Same parse-first rule for the repaired output.
                    var addedRepair = await callbacks.OnChunkGenerated(repaired, chunks, chunk.Index);
                    if (addedRepair > 0)
                    {
                        successfulChunkCountPerChunk++;
                        tracer?.LogVerdict(chunk.Index + 1, addedRepair, false, AiInputGuard.RefusalReasonNone);
                        continue;
                    }

                    var repairedReason = AiInputGuard.ExplainRefusalOutput(repaired);
                    if (repairedReason != AiInputGuard.RefusalReasonNone)
                        sawRefusal = true;
                    tracer?.LogVerdict(chunk.Index + 1, 0, repairedReason != AiInputGuard.RefusalReasonNone, repairedReason);
                    failedChunkIndexes.Add(chunk.Index);
                    continue;
                }

                failedChunkIndexes.Add(chunk.Index);
            }

            var successfulChunkCount = successfulChunkCountPerChunk;

            // Check if we have a result
            var finalize = callbacks.Finalize;
            if (finalize is not null)
            {
                var result = await finalize();
                if (result is T typedResult && !IsDefault(typedResult))
                {
                    tracer?.LogOutcome($"success: coverage={successfulChunkCount}/{chunks.Count}");
                    ReportCoverage(progress, successfulChunkCount, chunks.Count);
                    return typedResult;
                }
            }

            // Every chunk contributed nothing and at least one looked like a refusal - surface the
            // friendlier input-rejected message instead of the generic parse-failed one. Post-B
            // semantics: "refusal-looking but parseable" outputs never get here (they were accepted
            // as successes above). Per-chunk mode has no post-loop repair pass (repairs already ran
            // inline), so there is deliberately no second finalize checkpoint after this.
            if (chunks.Count > 0 && failedChunkIndexes.Count == chunks.Count && sawRefusal)
            {
                tracer?.LogOutcome($"all-refused-or-unparsed: {outputs.Count} chunk outputs contributed nothing");
                throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));
            }

            // Diagnostic log (per-chunk repaired outputs)
            var tag = callbacks.DiagnosticTag?.Invoke(noteText) ?? "ai";
            var diagnosticPath = AiDiagnosticLogger.WriteParseDiagnostic(
                noteText,
                string.Join("\n\n--- CHUNK OUTPUT ---\n\n", outputs),
                string.Join("\n\n--- REPAIR OUTPUT ---\n\n", repairedOutputs),
                tag);

            var ex = new InvalidOperationException(LocalizationService.GetString("AiGenerationParseFailed"));
            if (diagnosticPath is not null)
                ex.Data["DiagnosticPath"] = diagnosticPath;
            throw ex;
        }
        else
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var chunkProgress = CreateChunkProgress(progress, chunk.Index + 1, chunk.TotalCount);
                var prompt = callbacks.BuildChunkPrompt(chunk, chunks);
                var response = await GenerateTracedAsync($"chunk {chunk.Index + 1}/{chunks.Count}", new AiGenerationRequest(
                    Prompt: prompt,
                    MaxTokens: callbacks.MaxTokens,
                    Temperature: callbacks.Temperature,
                    TopP: callbacks.TopP,
                    TopK: callbacks.TopK,
                    PresencePenalty: callbacks.PresencePenalty,
                    RepetitionPenalty: callbacks.RepetitionPenalty,
                    Grammar: callbacks.BuildChunkGrammar(chunk),
                    GrammarRoot: callbacks.GrammarRoot,
                    Progress: chunkProgress,
                    CancellationToken: ct,
                    ModelKey: modelKey,
                    TokenObserver: tracer is null ? null : tracer.AppendStream));
                var output = response.Output;
                outputs.Add(output);
                var outputReason = AiInputGuard.ExplainRefusalOutput(output);

                // Parse FIRST (see the matching comment in the per-chunk branch): refusal-looking
                // text must not discard records that actually parsed.
                var added = await callbacks.OnChunkGenerated(output, chunks, chunk.Index);
                var refused = added <= 0 && outputReason != AiInputGuard.RefusalReasonNone;
                if (refused)
                    sawRefusal = true;
                tracer?.LogVerdict(chunk.Index + 1, added, refused, refused ? outputReason : AiInputGuard.RefusalReasonNone);
                if (added <= 0)
                    failedChunkIndexes.Add(chunk.Index);
            }

            var successfulChunkCount = chunks.Count - failedChunkIndexes.Count;

            // Repair pass — resubmits only the chunks that failed to contribute anything, not the
            // whole note, so repair cost scales with how much actually went wrong. Runs whenever
            // any chunk failed, even when other chunks already produced usable results - finalize
            // used to short-circuit before this pass, silently shielding failed chunks from
            // recovery whenever at least one chunk succeeded (traced real run: a 1/3 coverage was
            // returned as success with no recovery attempt). Best-effort on purpose: a throwing
            // or stalling repair must never kill an otherwise-good job, so every failure mode is
            // logged and swallowed - cancellation excepted, which always propagates.
            var repairedOutput = "";
            if (failedChunkIndexes.Count > 0 && callbacks.BuildRepairPrompt is not null
                && callbacks.BuildRepairGrammar is not null && callbacks.OnRepairGenerated is not null)
            {
                try
                {
                    var failedChunkText = string.Join(
                        "\n\n",
                        chunks.Where(c => failedChunkIndexes.Contains(c.Index)).Select(c => c.Text));
                    var repairPrompt = callbacks.BuildRepairPrompt(failedChunkText, chunks, chunks.Count);
                    var repairProgress = CreateRepairProgress(progress);
                    var repairResponse = await GenerateTracedAsync("batched-repair", new AiGenerationRequest(
                        Prompt: repairPrompt,
                        MaxTokens: callbacks.RepairMaxTokens,
                        Temperature: callbacks.RepairTemperature,
                        TopP: callbacks.RepairTopP,
                        TopK: callbacks.RepairTopK,
                        PresencePenalty: callbacks.RepairPresencePenalty,
                        RepetitionPenalty: callbacks.RepairRepetitionPenalty,
                        Grammar: callbacks.BuildRepairGrammar(failedChunkText),
                        GrammarRoot: callbacks.GrammarRoot,
                        Progress: repairProgress,
                        CancellationToken: ct,
                        ModelKey: modelKey,
                        TokenObserver: tracer is null ? null : tracer.AppendStream));
                    var repaired = repairResponse.Output;
                    repairedOutput = repaired;

                    if (!AiInputGuard.IsRefusalOutput(repaired))
                    {
                        var repairedResult = await callbacks.OnRepairGenerated(repaired, chunks.Count, ct);
                        tracer?.LogOutcome(string.IsNullOrEmpty(repairedResult)
                            ? "batched-repair produced no parseable items"
                            : "batched-repair accepted");

                        // Repair covers all previously-failed chunks in one combined prompt/response,
                        // with no per-chunk attribution in the result - we genuinely don't know how
                        // many of the failed chunks it actually recovered. successfulChunkCount
                        // deliberately keeps its pre-repair value rather than optimistically claiming
                        // full recovery: understating coverage is a minor annoyance, but overstating
                        // it would silently present a still-incomplete result as complete.
                    }
                    else
                    {
                        tracer?.LogOutcome($"batched-repair refused: {AiInputGuard.ExplainRefusalOutput(repaired)}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception repairEx)
                {
                    tracer?.LogOutcome($"batched-repair failed: {repairEx.GetType().Name}: {repairEx.Message}");
                }
            }

            // Every chunk contributed nothing and at least one looked like a refusal - after giving
            // repair its one shot above, surface the friendlier input-rejected message instead of
            // the generic parse-failed one. (Post-B semantics: "refusal-looking but parseable"
            // outputs were already accepted as successes in the loop, so this can't misfire.)
            if (chunks.Count > 0 && failedChunkIndexes.Count == chunks.Count && sawRefusal)
            {
                tracer?.LogOutcome($"all-refused-or-unparsed: {outputs.Count} chunk outputs contributed nothing");
                throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));
            }

            // Check if we have a result - first finalize checkpoint after repair had its chance to
            // add recovered items.
            var finalize = callbacks.Finalize;
            if (finalize is not null)
            {
                var result = await finalize();
                if (result is T typedResult && !IsDefault(typedResult))
                {
                    tracer?.LogOutcome($"success: coverage={successfulChunkCount}/{chunks.Count}");
                    ReportCoverage(progress, successfulChunkCount, chunks.Count);
                    return typedResult;
                }
            }

            // Diagnostic log
            var tag = callbacks.DiagnosticTag?.Invoke(noteText) ?? "ai";
            var diagnosticPath = AiDiagnosticLogger.WriteParseDiagnostic(
                noteText,
                string.Join("\n\n--- CHUNK OUTPUT ---\n\n", outputs),
                repairedOutput,
                tag);

            var ex = new InvalidOperationException(LocalizationService.GetString("AiGenerationParseFailed"));
            if (diagnosticPath is not null)
                ex.Data["DiagnosticPath"] = diagnosticPath;
            throw ex;
        }
    }

    private static bool IsDefault<T>(T value)
    {
        return EqualityComparer<T>.Default.Equals(value, default);
    }

    /// <summary>Fired once, right before a successful RunAsync return, so callers can tell a
    /// full-coverage result from one where some chunks contributed nothing (e.g. show "used 3 of
    /// 5 sections") instead of presenting a partial result as if it were complete.</summary>
    private static void ReportCoverage(IProgress<AiGenerationProgress>? progress, int successfulChunkCount, int totalChunkCount)
    {
        progress?.Report(new AiGenerationProgress(
            AiGenerationStatus.Finalizing,
            SuccessfulChunkCount: successfulChunkCount,
            TotalChunkCount: totalChunkCount));
    }

    internal static IProgress<AiGenerationProgress>? CreateChunkProgress(
        IProgress<AiGenerationProgress>? progress,
        int chunkIndex,
        int chunkCount)
    {
        if (progress is null)
            return progress;

        return new Progress<AiGenerationProgress>(status =>
            progress.Report(status with
            {
                ChunkIndex = Math.Max(1, chunkIndex),
                ChunkCount = Math.Max(1, chunkCount)
            }));
    }

    internal static IProgress<AiGenerationProgress>? CreateRepairProgress(
        IProgress<AiGenerationProgress>? progress,
        int? chunkIndex = null,
        int? chunkCount = null)
    {
        if (progress is null)
            return progress;

        if (chunkIndex.HasValue && chunkCount.HasValue)
        {
            var safeIndex = Math.Max(1, chunkIndex.Value);
            var safeCount = Math.Max(1, chunkCount.Value);
            progress.Report(new AiGenerationProgress(
                AiGenerationStatus.Repairing,
                ChunkIndex: safeIndex,
                ChunkCount: safeCount));

            return new Progress<AiGenerationProgress>(status =>
                progress.Report(status with
                {
                    Status = AiGenerationStatus.Repairing,
                    ChunkIndex = safeIndex,
                    ChunkCount = safeCount
                }));
        }

        progress.Report(new AiGenerationProgress(AiGenerationStatus.Repairing));

        return new Progress<AiGenerationProgress>(status =>
            progress.Report(status with
            {
                Status = AiGenerationStatus.Repairing
            }));
    }

    private sealed class BackendTokenCounter : ITokenCounter
    {
        private readonly ITextGenerationBackend _backend;
        private readonly string _modelKey;

        public BackendTokenCounter(ITextGenerationBackend backend, string modelKey)
        {
            _backend = backend;
            _modelKey = modelKey;
        }

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default)
            => _backend.CountTokensAsync(text, _modelKey, ct);
    }

}
