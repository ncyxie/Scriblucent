using System.Globalization;
using System.IO;
using System.Text;
using Scriblucent.Services;

namespace Scriblucent.Services.AI;

// Opt-in diagnostics record the source note, grounded planner calls, token stream, raw output,
// timing, and outcome. Logs contain note content, so all three gates are required and traces expire.

internal sealed class AiGenerationTracer : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly StringBuilder _streamBuffer = new();
    private bool _streamOpen;
    private const int MaximumStreamCharacters = 256 * 1024;

    private AiGenerationTracer(string path)
    {
        _path = path;
    }

    public static AiGenerationTracer? StartJob(
        string tag,
        string purpose,
        string modelKey,
        string noteText)
    {
        var settings = AppSettingsService.Load();
        if (!DiagnosticLogging.IsEnabled
            || !settings.EnableAiContentDiagnostics
            || !string.Equals(
                Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_TRACE"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var dir = DiagnosticLogging.GetAiDiagnosticsDirectory();
            Directory.CreateDirectory(dir);
            DeleteExpiredTraces(dir);
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
            _streamBuffer.Clear();
        }
        Append("--- STREAM ---" + Environment.NewLine);
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
                var remaining = MaximumStreamCharacters - _streamBuffer.Length;
                if (remaining > 0)
                    _streamBuffer.Append(delta.AsSpan(0, Math.Min(delta.Length, remaining)));
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
                sb.Append(_streamBuffer);
                if (_streamBuffer.Length >= MaximumStreamCharacters)
                    sb.AppendLine("\n<<STREAM TRUNCATED>>");
                _streamBuffer.Clear();
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

    private static void DeleteExpiredTraces(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var path in Directory.EnumerateFiles(directory, "gen-*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
        }
        catch
        {
            // Diagnostics retention is best effort.
        }
    }
}
