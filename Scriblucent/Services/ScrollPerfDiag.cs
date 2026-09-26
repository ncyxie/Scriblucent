using System.Diagnostics;
using System.IO;

namespace Scriblucent.Services;

/// <summary>
/// Opt-in scroll performance tracing (set SCRIBLUCENT_SCROLL_DIAG=1).
/// Hot-path events are recorded into a preallocated ring buffer of raw fields - no strings,
/// no allocations, no I/O inside the hot path (a synchronous flushed write per event from
/// inside MeasureOverride was itself the source of multi-ms frame spikes). The buffer is
/// flushed in batches to %TEMP%\scriblucent-scroll-diag.log by a background timer.
/// </summary>
internal static class ScrollPerfDiag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SCRIBLUCENT_SCROLL_DIAG") == "1"
        && DiagnosticLogging.IsEnabled;

    private const int Capacity = 8192;
    private const int Fields = 12; // ticks, eventId, a..j
    private const int BufferLength = Capacity * Fields;

    private static readonly long[] Buffer = new long[BufferLength];
    private static int _count;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static readonly Timer? FlushTimer;

    // Event ids.
    public const int EvFrame = 1;
    public const int EvPosY = 2;
    public const int EvHostMeasureState = 3;
    public const int EvHostMeasure = 4;
    public const int EvMeasureSlice = 5;
    public const int EvMeasureSliceStatic = 6;
    public const int EvCacheApply = 7;
    public const int EvCacheRelease = 8;
    public const int EvContainerPrepare = 9;
    public const int EvCommit = 10;
    public const int EvEditorSelectionPaint = 11;
    public const int EvEditorFindPaint = 12;
    public const int EvEditorSelectionClear = 13;

    private static readonly string[] Names =
    {
        "", "frame", "posY", "hostMeasureState", "hostMeasure", "measureSlice",
        "measureSliceStatic", "cacheApply", "cacheRelease", "containerPrepare", "commit",
        "editorSelectionPaint", "editorFindPaint", "editorSelectionClear"
    };

    static ScrollPerfDiag()
    {
        if (Enabled)
            FlushTimer = new Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>String-based logging for rare, non-hot-path events. Callers must build the
    /// string lazily (guard with <see cref="Enabled"/>) - this method is never in the hot path.</summary>
    public static void Log(string message)
    {
        if (!Enabled)
            return;

        lock (Gate)
            EnsureWriter().WriteLine($"{Clock.Elapsed.TotalMilliseconds:F2}\t{message}");
    }

    public static void LogFrame(double frameMs, double gc0, double gc1, double gc2, double cacheApplied, double cachePending)
        => LogEvent(EvFrame, frameMs, gc0, gc1, gc2, cacheApplied, cachePending);

    public static void LogPosY(double posY)
        => LogEvent(EvPosY, posY);

    public static void LogHostMeasureState(double items, double extent, double viewportHeight, double minimized, double viable, double preserve, double motionFrozen)
        => LogEvent(EvHostMeasureState, items, extent, viewportHeight, minimized, viable, preserve, motionFrozen);

    public static void LogHostMeasure(double elapsedMs, double items, double hadRealized)
        => LogEvent(EvHostMeasure, elapsedMs, items, hadRealized);

    public static void LogMeasureSlice(double sliceOffset, double height, double extent, double contentExtent, double childrenPrev, double childrenCur, double sectionTop, double viewportTop)
        => LogEvent(EvMeasureSlice, sliceOffset, height, extent, contentExtent, childrenPrev, childrenCur, sectionTop, viewportTop);

    public static void LogMeasureSliceStatic(double sliceOffset, double height, double contentExtent, double sectionTop, double viewportTop, double items)
        => LogEvent(EvMeasureSliceStatic, sliceOffset, height, contentExtent, sectionTop, viewportTop, items);

    public static void LogCacheApply(double applied)
        => LogEvent(EvCacheApply, applied);

    public static void LogCacheRelease(double released)
        => LogEvent(EvCacheRelease, released);

    public static void LogContainerPrepare(double elapsedMs)
        => LogEvent(EvContainerPrepare, elapsedMs);

    public static void LogCommit(double clampedY)
        => LogEvent(EvCommit, clampedY);

    public static void LogEditorSelectionPaint(double elapsedMs, double lines, double tiles)
        => LogEvent(EvEditorSelectionPaint, elapsedMs, lines, tiles);

    public static void LogEditorFindPaint(double elapsedMs, double matches, double tiles)
        => LogEvent(EvEditorFindPaint, elapsedMs, matches, tiles);

    public static void LogEditorSelectionClear(double elapsedMs)
        => LogEvent(EvEditorSelectionClear, elapsedMs);

    private static void LogEvent(
        int eventId,
        double a = 0, double b = 0, double c = 0, double d = 0,
        double e = 0, double f = 0, double g = 0, double h = 0,
        double i = 0, double j = 0)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            if (_count >= Capacity)
                FlushLocked();

            var o = _count * Fields;
            Buffer[o] = Clock.Elapsed.Ticks;
            Buffer[o + 1] = eventId;
            Buffer[o + 2] = BitConverter.DoubleToInt64Bits(a);
            Buffer[o + 3] = BitConverter.DoubleToInt64Bits(b);
            Buffer[o + 4] = BitConverter.DoubleToInt64Bits(c);
            Buffer[o + 5] = BitConverter.DoubleToInt64Bits(d);
            Buffer[o + 6] = BitConverter.DoubleToInt64Bits(e);
            Buffer[o + 7] = BitConverter.DoubleToInt64Bits(f);
            Buffer[o + 8] = BitConverter.DoubleToInt64Bits(g);
            Buffer[o + 9] = BitConverter.DoubleToInt64Bits(h);
            Buffer[o + 10] = BitConverter.DoubleToInt64Bits(i);
            Buffer[o + 11] = BitConverter.DoubleToInt64Bits(j);
            _count++;
        }
    }

    public static void Flush()
    {
        if (!Enabled)
            return;

        lock (Gate)
            FlushLocked();
    }

    private static void FlushLocked()
    {
        if (_count > 0)
        {
            var writer = EnsureWriter();
            for (var n = 0; n < _count; n++)
            {
                var o = n * Fields;
                var id = (int)Buffer[o + 1];
                writer.Write($"{Buffer[o] / (double)TimeSpan.TicksPerMillisecond:F2}\t{Names[id]}");
                for (var field = 2; field < Fields; field++)
                    writer.Write($"\t{BitConverter.Int64BitsToDouble(Buffer[o + field]):F3}");
                writer.WriteLine();
            }

            _count = 0;
        }

        _writer?.Flush();
    }

    private static StreamWriter EnsureWriter()
    {
        return _writer ??= new StreamWriter(
            Path.Combine(Path.GetTempPath(), "scriblucent-scroll-diag.log"),
            append: false);
    }
}
