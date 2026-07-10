using System.Diagnostics;
using System.IO;

namespace Scriblucent.Services;

/// <summary>
/// Opt-in scroll performance tracing (set SCRIBLUCENT_SCROLL_DIAG=1).
/// Writes tab-separated events to %TEMP%\scriblucent-scroll-diag.log.
/// </summary>
internal static class ScrollPerfDiag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SCRIBLUCENT_SCROLL_DIAG") == "1";

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _writer;

    public static void Log(string message)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            _writer ??= new StreamWriter(
                Path.Combine(Path.GetTempPath(), "scriblucent-scroll-diag.log"),
                append: false)
            {
                AutoFlush = true
            };

            _writer.WriteLine($"{Clock.Elapsed.TotalMilliseconds:F2}\t{message}");
        }
    }
}
