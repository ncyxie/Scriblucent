namespace Scriblucent.Services;

/// <summary>Single gate every local diagnostic/crash logger checks before writing anything to
/// disk (CrashLog, the AI parse-failure logger, and the env-var-gated dev tools ScrollPerfDiag/
/// StartupProbe) - lets AppSettings.EnableDiagnosticLogging act as a hard override rather than
/// each logger needing its own on/off wiring.</summary>
internal static class DiagnosticLogging
{
    public static bool IsEnabled => AppSettingsService.Load().EnableDiagnosticLogging;
}
