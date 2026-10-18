using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Scriblucent.Rendering;

// Opt-in, in-memory measurements. No strings, tree walks or file writes in timed paths.
internal static class FlashcardScrollTrace
{
    internal enum Kind { Frame, Offset, Prepare, Measure, Arrange, Capture, Publish, Lighting, Begin, End, Gc }
    internal readonly record struct Entry(long Timestamp, Kind Event, double Duration, double Value, int Identity);
    private static Entry[]? _entries;
    private static int _count;
    internal static bool Enabled => _entries is not null;
    internal static void Start() { _count = 0; _entries = new Entry[65536]; }
    internal static Entry[] Stop()
    {
        var entries = _entries; _entries = null;
        return entries is null ? [] : entries.Take(Math.Min(_count, entries.Length)).ToArray();
    }
    internal static void Record(Kind kind, double duration = 0, double value = 0, object? identity = null)
    {
        if (_entries is not { } entries) return;
        var index = _count++;
        if (index < entries.Length) entries[index] = new(Stopwatch.GetTimestamp(), kind, duration, value,
            identity is null ? 0 : RuntimeHelpers.GetHashCode(identity));
    }
    internal static Timing Measure(Kind kind, object? identity = null) => new(kind, identity);
    internal readonly struct Timing : IDisposable
    {
        private readonly long _start;
        private readonly Kind _kind;
        private readonly object? _identity;
        internal Timing(Kind kind, object? identity) { _start = Enabled ? Stopwatch.GetTimestamp() : 0; _kind = kind; _identity = identity; }
        public void Dispose() { if (_start != 0) Record(_kind, Stopwatch.GetElapsedTime(_start).TotalMilliseconds, identity: _identity); }
    }
}
