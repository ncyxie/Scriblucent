using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Scriblucent.Animations;

/// <summary>Shared display-aligned scheduling for procedural motion.</summary>
public sealed class RenderTransitionClock
{
    private readonly List<Track> _tracks = new();
    private bool[] _finished = Array.Empty<bool>();
    private EventHandler? _handler;
    private Stopwatch? _stopwatch;
    private TimeSpan _lastRenderingTime;
    private Action? _onAllComplete;
    private Action? _onStopped;
    private int _generation;
    private bool _startOnNextFrame;
    private static readonly List<RenderTransitionClock> Active = new();
    private static readonly List<RenderTransitionClock> Frame = new();
    private static int _nativeCount;
    private static bool _hooked;
    private static TimeSpan _lastFrame;
    public static bool IsAnyRunning => Active.Count > 0 || _nativeCount > 0;
    public static event Action? ActivityChanged;
    internal static event EventHandler? Rendered;
    internal static void AddFrameObserver(EventHandler observer) { MotionPolicy.ObserveActiveWindow(); Rendered += observer; NativeStarted(); }
    internal static void RemoveFrameObserver(EventHandler observer) { Rendered -= observer; NativeStopped(); }

    static RenderTransitionClock() => MotionPolicy.Changed += SettleAll;
    public void Run(IEnumerable<Track> tracks, Action? onAllComplete = null,
        bool instantOnlyForSystemReducedMotion = false, Action? onStopped = null, bool startOnNextFrame = false)
    {
        Stop();
        _startOnNextFrame = startOnNextFrame;
        _tracks.AddRange(tracks);
        _onAllComplete = onAllComplete;
        _onStopped = onStopped;
        _finished = new bool[_tracks.Count];
        if (_tracks.Count == 0 || !MotionPolicy.Spatial) { Complete(); return; }
        _stopwatch = Stopwatch.StartNew();
        _lastRenderingTime = TimeSpan.MinValue;
        _handler = OnRendering;
        Active.Add(this);
        UpdateHook();
    }
    public void Stop()
    {
        _generation++;
        _startOnNextFrame = false;
        Active.Remove(this);
        _handler = null;
        _stopwatch = null;
        _tracks.Clear();
        _onAllComplete = null;
        var stopped = _onStopped;
        _onStopped = null;
        UpdateHook();
        stopped?.Invoke();
    }
    internal static void NativeStarted() { _nativeCount++; UpdateHook(); }
    internal static void NativeStopped() { _nativeCount = Math.Max(0, _nativeCount - 1); UpdateHook(); }
    internal static void RefreshHook() => UpdateHook();
    private static void UpdateHook()
    {
        var needed = IsAnyRunning;
        if (_hooked == needed) return;
        _hooked = needed;
        if (needed) CompositionTarget.Rendering += RenderFrame;
        else
        {
            CompositionTarget.Rendering -= RenderFrame;
            MotionDiagnostics.ResetFrameContinuity();
            MotionPolicy.ResetSampling(); _lastFrame = default;
        }
        ActivityChanged?.Invoke();
    }
    private static void RenderFrame(object? sender, EventArgs e)
    {
        if (!IsAnyRunning) return;
        if (e is RenderingEventArgs args && _lastFrame == args.RenderingTime) return;
        var sample = MotionDiagnostics.BeginFrame();
        try
        {
            if (e is RenderingEventArgs rendering)
            {
                _lastFrame = rendering.RenderingTime;
                MotionPolicy.Sample(rendering.RenderingTime);
            }
            Frame.Clear();
            Rendered?.Invoke(sender, e);
            Frame.AddRange(Active);
            for (var i = 0; i < Frame.Count; i++) Frame[i].OnRendering(sender, e);
            Frame.Clear();
        }
        finally { MotionDiagnostics.EndFrame(sample); }
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_stopwatch is null || _handler is null) return;
        if (e is RenderingEventArgs args)
        {
            if (_lastRenderingTime == args.RenderingTime) return;
            _lastRenderingTime = args.RenderingTime;
        }
        if (_startOnNextFrame) { _stopwatch.Restart(); _startOnNextFrame = false; }
        var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
        Advance(elapsed);
    }
    private void Advance(double elapsed)
    {
        if (_handler is null) return;
        var generation = _generation;
        var complete = true;
        for (var i = 0; i < _tracks.Count; i++)
        {
            if (_finished[i]) continue;
            var track = _tracks[i];
            var time = elapsed - track.StartDelayMs;
            if (time < 0) { complete = false; continue; }
            var progress = track.DurationMs <= 0 ? 1 : Math.Min(1, time / track.DurationMs);
            if (progress >= 1) _finished[i] = true;
            else complete = false;
            track.Apply(track.Easing.Ease(progress));
            if (_generation != generation) return;
        }
        if (complete) Complete();
    }
    private void Complete()
    {
        var tracks = _tracks.ToArray();
        var finished = _finished;
        var completion = _onAllComplete;
        var generation = _generation;
        for (var i = 0; i < tracks.Length; i++)
        {
            if (i >= finished.Length || !finished[i]) tracks[i].Apply(tracks[i].Easing.Ease(1));
            if (_generation != generation) return;
        }
        Stop();
        foreach (var track in tracks) track.OnComplete?.Invoke();
        completion?.Invoke();
    }
    private static void SettleAll()
    {
        foreach (var clock in Active.ToArray()) clock.Complete();
    }
    public readonly record struct Track(int DurationMs, IEasingFunction Easing, Action<double> Apply,
        Action? OnComplete = null, int StartDelayMs = 0);
}
