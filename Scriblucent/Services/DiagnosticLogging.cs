using System.IO;

namespace Scriblucent.Services;

/// <summary>Single gate every local diagnostic/crash logger checks before writing anything to
/// disk (CrashLog, the AI parse-failure logger, and the env-var-gated dev tools ScrollPerfDiag/
/// StartupProbe) - lets AppSettings.EnableDiagnosticLogging act as a hard override rather than
/// each logger needing its own on/off wiring.</summary>
internal static class DiagnosticLogging
{
    public static bool IsEnabled => AppSettingsService.Load().EnableDiagnosticLogging;

    public static string GetAiDiagnosticsDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_DIAGNOSTICS_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scriblucent",
            "AiCache",
            "diagnostics");
    }
}
