using System.Globalization;
using System.IO;
using System.Text;
using Scriblucent.Services;

namespace Scriblucent.Services.AI;

// TEMPORARY diagnostics scaffolding for the generation-quality investigation: one log file per
// generation job recording the full source note, chunk plan, every backend call (prompt, grammar,
// sampling params), the live token stream as it arrives from the worker, raw outputs with char
// counts and timing, per-chunk parse/refusal verdicts, and the job outcome. Writes are append-only
// and best-effort - tracing must never break or slow down generation. Remove once the
// "unbalanced chunks / spurious refusals" issue is diagnosed and fixed.

internal sealed class AiGenerationTracer : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private bool _streamOpen;

    private AiGenerationTracer(string path)
    {
        _path = path;
    }

    public static AiGenerationTracer? StartJob(
        string tag,
        string purpose,
        string modelKey,
        string noteText,
        bool forceEnable = false)
    {
        if (!forceEnable && !DiagnosticLogging.IsEnabled)
            return null;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "AiCache",
                "diagnostics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(
                dir,
                $"gen-{tag}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}.log");

            var sb = new StringBuilder();
            sb.AppendLine("=== JOB START ===");
            sb.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            sb.AppendLine($"Purpose: {purpose}");
            sb.AppendLine($"ModelKey: {modelKey}");
            sb.AppendLine("--- SOURCE NOTE ---");
            sb.AppendLine(noteText);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return new AiGenerationTracer(path);
        }
        catch
        {
            return null;
        }
    }

    public void LogChunkPlan(IReadOnlyList<TextChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CHUNK PLAN ===");
        if (chunks.Count == 0)
            sb.AppendLine("(no chunks produced)");
        foreach (var chunk in chunks)
        {
            sb.AppendLine(
                $"Chunk[{chunk.Index}] of {chunk.TotalCount}: chars={chunk.Text.Length} tokens={chunk.TokenCount} offsets=[{chunk.StartCharOffset}..{chunk.EndCharOffset}]");
            sb.AppendLine(chunk.Text);
            sb.AppendLine();
        }
        Append(sb.ToString());
    }

    public void LogCallStart(int callId, string kind, AiGenerationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== CALL #{callId} [{kind}] START ===");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"MaxTokens={request.MaxTokens} Temp={request.Temperature} TopP={request.TopP} TopK={request.TopK} PresencePenalty={request.PresencePenalty} RepetitionPenalty={request.RepetitionPenalty}"));
        sb.AppendLine($"GrammarRoot={request.GrammarRoot ?? "(none)"}");
        if (!string.IsNullOrEmpty(request.Grammar))
        {
            sb.AppendLine("--- GRAMMAR ---");
            sb.AppendLine(request.Grammar);
        }
        sb.AppendLine("--- PROMPT ---");
        sb.AppendLine(request.Prompt);
        Append(sb.ToString());

        lock (_gate)
        {
            _streamOpen = true;
            try { File.AppendAllText(_path, "--- STREAM ---" + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    public void AppendStream(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        try
        {
            lock (_gate)
            {
                if (!_streamOpen)
                    return;
                File.AppendAllText(_path, delta, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public void LogCallEnd(int callId, AiGenerationResponse response)
    {
        var sb = new StringBuilder();
        CloseStream(sb);
        sb.AppendLine($"=== CALL #{callId} END ===");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"GeneratedChars={response.GeneratedChars} ElapsedMs={response.Elapsed.TotalMilliseconds:F0}"));
        sb.AppendLine("--- RAW OUTPUT ---");
        sb.AppendLine(response.Output);
        Append(sb.ToString());
    }

    public void LogCallAborted(int callId, string reason)
    {
        var sb = new StringBuilder();
        CloseStream(sb);
        sb.AppendLine($"=== CALL #{callId} ABORTED === {reason}");
        Append(sb.ToString());
    }

    public void LogVerdict(int chunkNumber, int addedItems, bool refusal, string refusalReason)
    {
        Append(
            $"=== VERDICT chunk {chunkNumber} === AddedItems={addedItems} Refusal={refusal} Reason={refusalReason}{Environment.NewLine}");
    }

    public void LogOutcome(string text)
    {
        Append($"=== JOB OUTCOME === {text}{Environment.NewLine}");
    }

    private void CloseStream(StringBuilder sb)
    {
        lock (_gate)
        {
            if (_streamOpen)
            {
                _streamOpen = false;
                sb.AppendLine();
                sb.AppendLine("<<STREAM END>>");
            }
        }
    }

    private void Append(string text)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(_path, text, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
    }
}
