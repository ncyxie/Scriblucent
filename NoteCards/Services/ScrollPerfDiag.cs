using System.Diagnostics;
using System.IO;

namespace NoteCards.Services;

/// <summary>
/// Opt-in scroll performance tracing (set NOTECARDS_SCROLL_DIAG=1).
/// Writes tab-separated events to %TEMP%\notecards-scroll-diag.log.
/// </summary>
internal static class ScrollPerfDiag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("NOTECARDS_SCROLL_DIAG") == "1";

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
                Path.Combine(Path.GetTempPath(), "notecards-scroll-diag.log"),
                append: false)
            {
                AutoFlush = true
            };

            _writer.WriteLine($"{Clock.Elapsed.TotalMilliseconds:F2}\t{message}");
        }
    }
}
