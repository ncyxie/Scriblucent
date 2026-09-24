using System.IO;

namespace Scriblucent.Services;

internal static class CrashLog
{
    public static void Append(string context, Exception ex)
    {
        if (!DiagnosticLogging.IsEnabled)
            return;

        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scriblucent-crash.log");
            // Exception messages and stack traces can contain note text and machine paths.
            File.AppendAllText(path,
                $"[{DateTimeOffset.UtcNow:O}] META {context}: {ex.GetType().FullName} (0x{ex.HResult:X8}){Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
