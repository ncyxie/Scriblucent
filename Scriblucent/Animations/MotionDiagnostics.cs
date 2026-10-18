using System.Diagnostics;

namespace Scriblucent.Animations;

/// <summary>Opt-in bounded diagnostics. Render callbacks are not presented-frame measurements.</summary>
public static class MotionDiagnostics
{
    public readonly record struct Sample(double CallbackGapMs, double ManagedMs, long AllocatedBytes, double CachePixels);
    private static Sample[] _samples = Array.Empty<Sample>();
    private static int _count;
    private static long _previous, _allocation;
    private static bool _enabled;
    internal static bool Enabled => _enabled;
    public static void Start()
    {
        _samples = new Sample[16_384]; _count = 0; _previous = 0; _enabled = true;
        RenderTransitionClock.RefreshHook();
    }
    public static IReadOnlyList<Sample> Stop()
    {
        _enabled = false;
        RenderTransitionClock.RefreshHook();
        return _samples.AsSpan(0, _count).ToArray();
    }
    internal static long BeginFrame()
    {
        if (!_enabled) return 0;
        _allocation = GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }
    internal static void ResetFrameContinuity() => _previous = 0;
    internal static void EndFrame(long start)
    {
        if (!_enabled || start == 0 || _count >= _samples.Length) return;
        var end = Stopwatch.GetTimestamp();
        _samples[_count++] = new Sample(_previous == 0 ? 0 : Stopwatch.GetElapsedTime(_previous, start).TotalMilliseconds,
            Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - _allocation, MotionCacheBudget.ReservedPixels);
        _previous = RenderTransitionClock.IsAnyRunning ? start : 0;
    }
}
