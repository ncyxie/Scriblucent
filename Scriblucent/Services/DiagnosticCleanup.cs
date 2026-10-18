using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class DiagnosticCleanup
{
    private static readonly TimeSpan DiagnosticLogRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CrashLogRetention = TimeSpan.FromDays(30);
    private static readonly Regex MetadataCrashEntry = new(
        @"^ META [A-Za-z0-9.]+: [A-Za-z0-9.+`]+ \(0x[0-9A-F]{8}\)$",
        RegexOptions.Compiled);

    public static void CleanupOldLogs()
    {
        try
        {
            CleanupDiagnosticLogs();
            CleanupCrashLog();
        }
        catch
        {
            // Best-effort cleanup — never crash the app
        }
    }

    private static void CleanupDiagnosticLogs()
    {
        var diagnosticsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scriblucent",
            "Logs",
            "AI");

        if (!Directory.Exists(diagnosticsDir))
            return;

        var cutoff = DateTime.UtcNow - DiagnosticLogRetention;

        foreach (var file in Directory.EnumerateFiles(diagnosticsDir, "*.log"))
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < cutoff)
                    fileInfo.Delete();
            }
            catch
            {
                // Skip files that can't be deleted (in use, permissions, etc.)
            }
        }
    }

    private static void CleanupCrashLog()
    {
        var crashLogPath = Path.Combine(AppContext.BaseDirectory, "scriblucent-crash.log");

        if (!File.Exists(crashLogPath))
            return;

        try
        {
            var lines = File.ReadAllLines(crashLogPath);
            var keptLines = RetainCrashMetadataLines(lines, DateTimeOffset.UtcNow - CrashLogRetention);

            if (keptLines.Length == lines.Length)
                return;
            if (keptLines.Length == 0)
                File.Delete(crashLogPath);
            else
                File.WriteAllLines(crashLogPath, keptLines);
        }
        catch
        {
            // Best-effort
        }
    }

    internal static string[] RetainCrashMetadataLines(IEnumerable<string> lines, DateTimeOffset cutoff)
        => lines.Where(line =>
        {
            var closingBracket = line.IndexOf(']');
            return line.StartsWith("[", StringComparison.Ordinal)
                && closingBracket > 1
                && MetadataCrashEntry.IsMatch(line[(closingBracket + 1)..])
                && DateTimeOffset.TryParse(
                    line.AsSpan(1, closingBracket - 1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp)
                && timestamp >= cutoff;
        }).ToArray();
}
