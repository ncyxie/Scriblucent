using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Scriblucent.Animations;

/// <summary>
/// Drives UI transitions from CompositionTarget.Rendering so updates stay aligned with the display refresh.
/// </summary>
public sealed class RenderTransitionClock
{
    private readonly List<Track> _tracks = new();
    private EventHandler? _handler;
    private Stopwatch? _stopwatch;
    private TimeSpan _lastRenderingTime;
    private Action? _onAllComplete;

    private bool _countsAsRunning;
    private static int _activeCount;

    public static bool IsAnyRunning => _activeCount > 0;

    public void Run(
        IEnumerable<Track> tracks,
        Action? onAllComplete = null,
        bool instantOnlyForSystemReducedMotion = false)
    {
        Stop();

        var trackList = tracks as IList<Track> ?? tracks.ToList();
        if (trackList.Count == 0)
        {
            onAllComplete?.Invoke();
            return;
        }

        var useInstant = instantOnlyForSystemReducedMotion
            ? ReducedMotionPreferences.ShouldUseInstantOverlayTransitions
            : ReducedMotionPreferences.ShouldReduceMotion;

        if (useInstant)
        {
            CompleteImmediately(trackList, onAllComplete);
            return;
        }

        SetRunning(true);
        _tracks.Clear();
        _tracks.AddRange(trackList);

        _onAllComplete = onAllComplete;
        _stopwatch = Stopwatch.StartNew();
        _lastRenderingTime = TimeSpan.MinValue;
        _handler = OnRendering;
        CompositionTarget.Rendering += _handler;
    }

    public void Stop()
    {
        SetRunning(false);
        if (_handler != null)
            CompositionTarget.Rendering -= _handler;

        _handler = null;
        _stopwatch = null;
        _tracks.Clear();
        _onAllComplete = null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_stopwatch == null || _handler == null)
            return;

        if (e is RenderingEventArgs renderingArgs)
        {
            if (renderingArgs.RenderingTime == _lastRenderingTime)
                return;

            _lastRenderingTime = renderingArgs.RenderingTime;
        }

        var elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
        var allComplete = true;

        foreach (var track in _tracks)
        {
            var effectiveElapsedMs = elapsedMs - track.StartDelayMs;
            if (effectiveElapsedMs < 0)
            {
                track.Apply(track.Easing.Ease(0));
                allComplete = false;
                continue;
            }

            var progress = track.DurationMs <= 0
                ? 1.0
                : Math.Min(1.0, effectiveElapsedMs / track.DurationMs);

            var eased = track.Easing.Ease(progress);
            track.Apply(eased);

            if (progress < 1.0)
                allComplete = false;
        }

        if (!allComplete)
            return;

        var onAllComplete = _onAllComplete;
        var tracks = _tracks.ToArray();
        Stop();

        foreach (var track in tracks)
            track.OnComplete?.Invoke();

        onAllComplete?.Invoke();
    }

    private void SetRunning(bool running)
    {
        if (_countsAsRunning == running)
            return;

        _countsAsRunning = running;
        _activeCount = running ? _activeCount + 1 : Math.Max(0, _activeCount - 1);
    }

    private static void CompleteImmediately(IList<Track> tracks, Action? onAllComplete)
    {
        foreach (var track in tracks)
        {
            var eased = track.Easing.Ease(1.0);
            track.Apply(eased);
        }

        foreach (var track in tracks)
            track.OnComplete?.Invoke();

        onAllComplete?.Invoke();
    }

    public readonly record struct Track(
        int DurationMs,
        IEasingFunction Easing,
        Action<double> Apply,
        Action? OnComplete = null,
        int StartDelayMs = 0);
}