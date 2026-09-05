using System.Diagnostics;
using System.IO;

namespace Scriblucent.Services;

/// <summary>
/// Cold-start timeline (SCRIBLUCENT_STARTUP_PROBE=1). Records named phases against a stopwatch
/// started at the earliest possible moment and writes the timeline to
/// %TEMP%\scriblucent-startup.log on the first Mark. The static initializer of this type runs
/// when the runtime first touches it - the earliest hook a managed assembly can grab.
/// </summary>
internal static class StartupProbe
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SCRIBLUCENT_STARTUP_PROBE") == "1"
        && DiagnosticLogging.IsEnabled;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly HashSet<string> Phases = new();
    private static bool _started;

    public static void Mark(string phase)
    {
        if (!Enabled)
            return;

        var elapsed = Clock.Elapsed.TotalMilliseconds;
        lock (Gate)
        {
            Phases.Add(phase);
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "scriblucent-startup.log");
                if (!_started)
                {
                    _started = true;
                    File.WriteAllText(path, $"scriblucent-startup\tpid={Environment.ProcessId}\n");
                }

                File.AppendAllText(path, $"{elapsed:F1}\t{phase}\n");
            }
            catch
            {
                // The probe must never break startup.
            }
        }
    }

    /// <summary>Records a phase only the first time it happens - for events that fire repeatedly
    /// (recent-strip refreshes, realization passes) where only the startup occurrence matters.</summary>
    public static void MarkOnce(string phase)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            if (Phases.Contains(phase))
                return;

            Mark(phase);
        }
    }

    public static bool HasMarked(string phase)
    {
        if (!Enabled)
            return true;

        lock (Gate)
            return Phases.Contains(phase);
    }
}
