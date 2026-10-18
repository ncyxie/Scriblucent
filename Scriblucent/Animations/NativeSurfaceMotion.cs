using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Scriblucent.Animations;

internal readonly record struct NativeMotionTrack(DependencyObject Target, DependencyProperty Property,
    double To, bool Spatial = false, double? Overshoot = null, double? Correction = null,
    NativeMotionProfile? Profile = null);

/// <summary>Track-local timing keeps readable fades independent of a surface's physical arrival.</summary>
internal readonly record struct NativeMotionProfile(IEasingFunction Easing, int? DurationMs = null,
    double PeakAt = .75, double? CorrectionAt = null);

internal static class MotionProfiles
{
    internal static readonly NativeMotionProfile Arrival = new(UiMotion.InOutSine);
    internal static readonly NativeMotionProfile Showcase = new(UiMotion.InOutSine, PeakAt: .65, CorrectionAt: .85);
    internal static readonly NativeMotionProfile Readable = new(UiMotion.OutSine, 180);
    internal static readonly NativeMotionProfile PopupFade = new(UiMotion.OutSine, 120);
}

/// <summary>Coordinated native clocks. Channels own disjoint properties and cancellation holds visible values.</summary>
internal static class NativeSurfaceMotion
{
    private static readonly ConditionalWeakTable<UIElement, Dictionary<MotionChannel, State>> States = new();
    private sealed class State
    {
        internal readonly List<(NativeMotionTrack Track, AnimationClock Clock)> Tracks = new();
        internal CacheMode? OriginalCache;
        internal BitmapCache? TemporaryCache;
        internal double ReservedPixels;
        internal EventHandler? Completed;
        internal AnimationClock? CompletionClock;
        internal RoutedEventHandler? Unloaded;
        internal Action? PreferenceChanged;
        internal Window? Window;
        internal EventHandler? WindowClosed;
    }
    internal static bool IsRunning(UIElement element, MotionChannel channel)
        => States.TryGetValue(element, out var states) && states.ContainsKey(channel);
    internal static void Stop(UIElement element)
    {
        if (!States.TryGetValue(element, out var states)) return;
        foreach (var channel in states.Keys.ToArray()) Stop(element, channel);
    }
    internal static void Stop(UIElement element, MotionChannel channel)
    {
        if (!States.TryGetValue(element, out var states) || !states.TryGetValue(channel, out var state)) return;
        var values = state.Tracks.Select(t => (double)t.Track.Target.GetValue(t.Track.Property)).ToArray();
        Release(element, channel, state);
        for (var i = 0; i < values.Length; i++)
            state.Tracks[i].Track.Target.SetCurrentValue(state.Tracks[i].Track.Property, values[i]);
    }
    internal static void AnimateTracks(UIElement element, MotionChannel channel,
        IReadOnlyList<NativeMotionTrack> tracks, int durationMs, IEasingFunction easing,
        Action? onComplete = null, bool cache = false, int startDelayMs = 0, bool instant = false)
    {
        Stop(element, channel);
        var from = tracks.Select(t => (double)t.Target.GetValue(t.Property)).ToArray();
        foreach (var track in tracks) track.Target.SetCurrentValue(track.Property, track.To);
        if (!MotionPolicy.Spatial)
        {
            // A constant spatial clock can expose its pre-animation base before the first tick.
            // Reduced commits these properties directly and animates only meaningful fades.
            var animated = tracks.Select((track, index) => (track, initial: from[index]))
                .Where(item => !item.track.Spatial).ToArray();
            tracks = animated.Select(item => item.track).ToArray();
            from = animated.Select(item => item.initial).ToArray();
        }
        durationMs = MotionPolicy.FadeDuration(durationMs);
        if (tracks.Count == 0 || durationMs == 0 || (instant && MotionPolicy.Quality != MotionQuality.Reduced)
            || element is FrameworkElement { IsLoaded: false } || !element.IsVisible)
        { onComplete?.Invoke(); return; }
        var states = States.GetValue(element, _ => new());
        var state = new State { OriginalCache = element.CacheMode };
        if (cache && element.CacheMode is null && element is FrameworkElement surface)
        {
            state.ReservedPixels = MotionCacheBudget.TryReserve(surface);
            if (state.ReservedPixels > 0)
            {
                state.TemporaryCache = DpiAwareBitmapCache.Create(surface, enableClearType: false);
                element.SetCurrentValue(UIElement.CacheModeProperty, state.TemporaryCache);
            }
        }
        var longestMs = -1;
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var initial = !MotionPolicy.Spatial && track.Spatial ? track.To : from[i];
            var delay = MotionPolicy.Spatial ? startDelayMs : 0;
            var trackMs = MotionPolicy.FadeDuration(Math.Max(0, track.Profile?.DurationMs ?? durationMs));
            var trackEase = track.Profile?.Easing ?? easing;
            // Short opacity tracks retain their finish until the group's longest clock releases all tracks.
            var timeline = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
            timeline.KeyFrames.Add(new DiscreteDoubleKeyFrame(initial, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > 0) timeline.KeyFrames.Add(new DiscreteDoubleKeyFrame(initial, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            void Frame(double value, double fraction, IEasingFunction ease) => timeline.KeyFrames.Add(
                new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + trackMs * fraction)), ease));
            if (MotionPolicy.Spatial && track.Overshoot is { } peak)
            {
                Frame(peak, track.Profile?.PeakAt ?? .65, track.Profile?.Easing ?? UiMotion.OutCubic);
                // Routine arrivals turn once and settle. Showcase artwork keeps its extra correction.
                if (track.Profile is null || track.Profile.Value.CorrectionAt is not null)
                    Frame(track.Correction ?? track.To, track.Profile?.CorrectionAt ?? .85, UiMotion.InOutSine);
            }
            Frame(track.To, 1, track.Overshoot is not null && track.Profile is not null ? UiMotion.InOutSine : trackEase);
            var clock = timeline.CreateClock();
            state.Tracks.Add((track, clock));
            if (delay + trackMs > longestMs)
            {
                longestMs = delay + trackMs;
                state.CompletionClock = clock;
            }
        }
        state.Completed = (_, _) =>
        {
            if (!states.TryGetValue(channel, out var current) || !ReferenceEquals(current, state)) return;
            Release(element, channel, state);
            onComplete?.Invoke();
        };
        state.CompletionClock!.Completed += state.Completed;
        if (element is FrameworkElement frameworkElement)
        {
            state.Unloaded = (_, _) => Release(element, channel, state);
            frameworkElement.Unloaded += state.Unloaded;
        }
        states.Add(channel, state);
        state.Window = Window.GetWindow(element);
        if (state.Window is { } window)
            window.Closed += state.WindowClosed = (_, _) => Release(element, channel, state);
        state.PreferenceChanged = () =>
        {
            if (!states.TryGetValue(channel, out var current) || !ReferenceEquals(current, state)) return;
            Release(element, channel, state);
            onComplete?.Invoke();
        };
        MotionPolicy.Changed += state.PreferenceChanged;
        RenderTransitionClock.NativeStarted();
        for (var i = 0; i < state.Tracks.Count; i++)
        {
            var (track, clock) = state.Tracks[i];
            // A new clock has no current time until its first tick. Keep the visible starting
            // value underneath it, so entry/release and immediate reversal cannot expose the end pose.
            track.Target.SetCurrentValue(track.Property, from[i]);
            Apply(track, clock);
            // SnapshotAndReplace can otherwise expose the replaced clock's base/snapshot
            // until the next composition tick, even after restoring the starting base value.
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            // The last composition tick may precede a long idle interval. Start against the
            // next tick so priming the visible pose cannot backdate the real transition.
            clock.Controller.Begin();
        }
    }
    private static void Apply(NativeMotionTrack track, AnimationClock? clock)
    {
        if (track.Target is UIElement visual) visual.ApplyAnimationClock(track.Property, clock);
        else if (track.Target is Animatable animatable) animatable.ApplyAnimationClock(track.Property, clock);
        else throw new ArgumentException("Native motion requires UIElement or Animatable targets.");
    }
    private static void Release(UIElement element, MotionChannel channel, State state)
    {
        if (!States.TryGetValue(element, out var states) || !states.TryGetValue(channel, out var current)
            || !ReferenceEquals(current, state)) return;
        states.Remove(channel);
        state.CompletionClock!.Completed -= state.Completed;
        if (element is FrameworkElement frameworkElement) frameworkElement.Unloaded -= state.Unloaded;
        if (state.Window is { } window) window.Closed -= state.WindowClosed;
        foreach (var (track, _) in state.Tracks)
        {
            Apply(track, null);
            var final = channel == MotionChannel.Interaction && !MotionPolicy.Spatial
                && track.Target is TranslateTransform ? 0 : track.To;
            track.Target.SetCurrentValue(track.Property, final);
        }
        if (state.TemporaryCache is not null && ReferenceEquals(element.CacheMode, state.TemporaryCache))
            element.SetCurrentValue(UIElement.CacheModeProperty, state.OriginalCache);
        MotionPolicy.Changed -= state.PreferenceChanged;
        MotionCacheBudget.Release(state.ReservedPixels);
        RenderTransitionClock.NativeStopped();
    }
}
