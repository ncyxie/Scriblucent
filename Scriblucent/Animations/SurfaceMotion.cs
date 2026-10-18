using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Scriblucent.Animations;

/// <summary>Compatibility entry point for native surface motion.</summary>
internal static class SurfaceMotion
{
    internal static void Stop(UIElement element) => NativeSurfaceMotion.Stop(element);
    internal static void Stop(UIElement element, MotionChannel channel) => NativeSurfaceMotion.Stop(element, channel);
    internal static void AnimateTracks(UIElement element, MotionChannel channel,
        IReadOnlyList<NativeMotionTrack> tracks, int durationMs, IEasingFunction easing,
        Action? onComplete = null, bool cache = false, int startDelayMs = 0, bool instant = false)
        => NativeSurfaceMotion.AnimateTracks(element, channel, tracks, durationMs, easing, onComplete, cache, startDelayMs, instant);
    internal static void Animate(UIElement element, double toOpacity, int durationMs, IEasingFunction easing,
        bool instant, Action? onComplete = null, TranslateTransform? translate = null, double toY = 0,
        bool cache = false, int startDelayMs = 0)
    {
        var tracks = new List<NativeMotionTrack> { new(element, UIElement.OpacityProperty, Math.Clamp(toOpacity, 0, 1)) };
        if (translate is not null) tracks.Add(new(translate, TranslateTransform.YProperty, toY, true));
        AnimateTracks(element, MotionChannel.Presence, tracks, durationMs, easing, onComplete, cache, startDelayMs, instant);
    }

    internal static void Arrive(UIElement element, int durationMs, Action? onComplete = null,
        TranslateTransform? translate = null, bool cache = false, int startDelayMs = 0)
    {
        var tracks = new List<NativeMotionTrack>
        { new(element, UIElement.OpacityProperty, 1, Profile: new(UiMotion.OutSine, Math.Min(180, durationMs))) };
        if (translate is not null)
            tracks.Add(new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival));
        AnimateTracks(element, MotionChannel.Presence, tracks, durationMs, UiMotion.InOutSine,
            onComplete, cache, startDelayMs);
    }
}
