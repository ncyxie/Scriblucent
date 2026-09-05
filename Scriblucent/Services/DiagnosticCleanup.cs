using System.IO;

namespace Scriblucent.Services;

internal static class DiagnosticCleanup
{
    private static readonly TimeSpan DiagnosticLogRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CrashLogRetention = TimeSpan.FromDays(30);

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
            "AiCache",
            "diagnostics");

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
            var fileInfo = new FileInfo(crashLogPath);
            if (fileInfo.LastWriteTimeUtc < DateTime.UtcNow - CrashLogRetention)
            {
                // Truncate old entries instead of deleting — keep the file for potential future crashes
                var lines = File.ReadAllLines(crashLogPath);
                var cutoff = DateTime.UtcNow - CrashLogRetention;
                var keptLines = new List<string>();
                var inRelevantBlock = false;

                foreach (var line in lines)
                {
                    // Keep lines that look like timestamps within retention period
                    if (line.StartsWith("=== ", StringComparison.Ordinal))
                    {
                        inRelevantBlock = true;
                        keptLines.Add(line);
                    }
                    else if (inRelevantBlock)
                    {
                        keptLines.Add(line);
                    }
                }

                // If we can't parse timestamps, just keep the last 100 lines
                if (keptLines.Count == 0 && lines.Length > 100)
                    keptLines = lines.Skip(lines.Length - 100).ToList();

                if (keptLines.Count < lines.Length)
                    File.WriteAllLines(crashLogPath, keptLines);
            }
        }
        catch
        {
            // Best-effort
        }
    }
}
