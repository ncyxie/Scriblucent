using System.IO;
using System.Text;
using Scriblucent.Services;

namespace Scriblucent.Services.AI;

/// <summary>Shared parse-failure diagnostic writer for the AI conversion services - keeps the
/// DiagnosticLogging.IsEnabled gate and log format in one place instead of duplicated per
/// service. Returns null when diagnostic logging is disabled, so callers know not to surface a
/// path that was never written.</summary>
internal static class AiDiagnosticLogger
{
    public static string? WriteParseDiagnostic(
        string sourceNote,
        string firstOutput,
        string repairedOutput,
        string tag)
    {
        if (!DiagnosticLogging.IsEnabled)
            return null;

        try
        {
            var diagnosticsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "AiCache",
                "diagnostics");

            Directory.CreateDirectory(diagnosticsDir);
            var path = Path.Combine(diagnosticsDir, $"{tag}-parse-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            sb.AppendLine("--- SOURCE NOTE ---");
            sb.AppendLine(sourceNote);
            sb.AppendLine();
            sb.AppendLine("--- FIRST OUTPUT ---");
            sb.AppendLine(firstOutput);

            if (!string.IsNullOrEmpty(repairedOutput))
            {
                sb.AppendLine();
                sb.AppendLine("--- REPAIR OUTPUT ---");
                sb.AppendLine(repairedOutput);
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(failed to write parse diagnostic log)";
        }
    }
}
