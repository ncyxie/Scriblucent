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
            File.AppendAllText(path, $"[{DateTime.Now:O}] {context}: {ex}\n\n");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
