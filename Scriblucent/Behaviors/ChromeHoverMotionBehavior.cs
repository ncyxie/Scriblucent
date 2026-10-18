using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Behaviors;

public enum ChromeHoverMotionProfile
{
    RecentCard,
    Fab
}

/// <summary>
/// Small lifts and baked shadow feedback for compact chrome surfaces (recent items, FABs).
/// </summary>
public static class ChromeHoverMotionBehavior
{
    private const double BakedShadowOpacityRest = 1.0;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsPressed;
        public RoutedEventHandler? LoadedHandler;
        public MouseEventHandler? MouseEnterHandler;
        public MouseEventHandler? MouseLeaveHandler;
        public MouseButtonEventHandler? MouseDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
        public KeyEventHandler? KeyDownHandler, KeyUpHandler;
        public KeyboardFocusChangedEventHandler? LostFocusHandler;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(ChromeHoverMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ChromeHoverMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.RegisterAttached(
        "Profile",
        typeof(ChromeHoverMotionProfile),
        typeof(ChromeHoverMotionBehavior),
        new PropertyMetadata(ChromeHoverMotionProfile.RecentCard));

    public static readonly DependencyProperty MotionHostNameProperty = DependencyProperty.RegisterAttached(
        "MotionHostName",
        typeof(string),
        typeof(ChromeHoverMotionBehavior),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShadowElementNameProperty = DependencyProperty.RegisterAttached(
        "ShadowElementName",
        typeof(string),
        typeof(ChromeHoverMotionBehavior),
        new PropertyMetadata(string.Empty));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetProfile(DependencyObject element, ChromeHoverMotionProfile value)
        => element.SetValue(ProfileProperty, value);

    public static ChromeHoverMotionProfile GetProfile(DependencyObject element)
        => (ChromeHoverMotionProfile)element.GetValue(ProfileProperty);

    public static void SetMotionHostName(DependencyObject element, string value)
        => element.SetValue(MotionHostNameProperty, value);

    public static string GetMotionHostName(DependencyObject element)
        => (string)element.GetValue(MotionHostNameProperty);

    public static void SetShadowElementName(DependencyObject element, string value)
        => element.SetValue(ShadowElementNameProperty, value);

    public static string GetShadowElementName(DependencyObject element)
        => (string)element.GetValue(ShadowElementNameProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.NewValue is true)
            Attach(element);
        else
            Detach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        if (state.IsAttached)
            return;

        state.LoadedHandler = (_, _) => ResetRestState(element);
        state.MouseEnterHandler = (_, _) => PlayHover(element, isEnter: true);
        state.MouseLeaveHandler = (_, _) =>
        {
            state.IsPressed = false;
            PlayHover(element, isEnter: false);
        };
        state.MouseDownHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            state.IsPressed = true;
            PlayPress(element, isPressed: true);
        };
        state.MouseUpHandler = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            state.IsPressed = false;
            PlayPress(element, isPressed: false);
        };
        state.KeyDownHandler = (_, e) =>
        {
            if (e.Key is not (Key.Space or Key.Enter) || e.IsRepeat || !element.IsEnabled) return;
            state.IsPressed = true;
            PlayPress(element, true);
        };
        state.KeyUpHandler = (_, e) =>
        {
            if (e.Key is not (Key.Space or Key.Enter) || !state.IsPressed) return;
            state.IsPressed = false;
            PlayPress(element, false);
        };
        state.LostFocusHandler = (_, _) =>
        {
            if (!state.IsPressed) return;
            state.IsPressed = false;
            PlayPress(element, false);
        };

        element.Loaded += state.LoadedHandler;
        element.Unloaded += OnUnloaded;
        element.MouseEnter += state.MouseEnterHandler;
        element.MouseLeave += state.MouseLeaveHandler;
        element.PreviewMouseLeftButtonDown += state.MouseDownHandler;
        element.PreviewMouseLeftButtonUp += state.MouseUpHandler;
        element.PreviewKeyDown += state.KeyDownHandler;
        element.PreviewKeyUp += state.KeyUpHandler;
        element.LostKeyboardFocus += state.LostFocusHandler;
        state.IsAttached = true;

        if (element.IsLoaded)
            ResetRestState(element);
    }

    private static void Detach(FrameworkElement element)
    {
        element.Unloaded -= OnUnloaded;
        if (element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            element.Loaded -= state.LoadedHandler;
        if (state.MouseEnterHandler != null)
            element.MouseEnter -= state.MouseEnterHandler;
        if (state.MouseLeaveHandler != null)
            element.MouseLeave -= state.MouseLeaveHandler;
        if (state.MouseDownHandler != null)
            element.PreviewMouseLeftButtonDown -= state.MouseDownHandler;
        if (state.MouseUpHandler != null)
            element.PreviewMouseLeftButtonUp -= state.MouseUpHandler;
        element.PreviewKeyDown -= state.KeyDownHandler;
        element.PreviewKeyUp -= state.KeyUpHandler;
        element.LostKeyboardFocus -= state.LostFocusHandler;

        UiMotion.Cleanup(element);
        state.IsAttached = false;
        state.IsPressed = false;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        UiMotion.Cleanup(ResolveMotionHost(element));
        if (GetState(element) is { } state) state.IsPressed = false;
    }

    /// <summary>
    /// Re-applies hover or rest after scroll ends when the pointer is still over the surface.
    /// </summary>
    public static void SyncPointerState(FrameworkElement element)
    {
        if (!GetIsEnabled(element) || ShouldSuppressShadowMotion(element))
            return;

        PlayHover(element, element.IsMouseOver);
    }

    /// <summary>
    /// Immediately returns hover scale/lift/shadow to rest during scroll hit-test suppression.
    /// </summary>
    public static void ForceRestHoverState(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;

        var host = ResolveMotionHost(element);
        var profile = GetProfile(element);
        UiMotion.Stop(UiMotion.Channel(host, MotionChannel.Interaction));
        SurfaceLight.Reset(host);
        // Shared button faces also own a hover-fill track. Cancelling their motion
        // must clear that paint, rather than freeze a partially faded tint into the scroll cache.
        if (host is InteractionSurface interactive) interactive.AnimateState(false, false, false);

        var translate = UiMotion.ChannelTranslate(host, MotionChannel.Interaction);

        translate.X = 0;
        translate.Y = 0;

        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
        {
            UiMotion.Stop(bakedShadow);
            bakedShadow.Opacity = profile == ChromeHoverMotionProfile.RecentCard ? .72 : RestBakedOpacity(profile);
            if (bakedShadow is CardShadowLayer { FollowsFace: true }) return;
            bakedShadow.RenderTransformOrigin = new Point(0.5, 0.5);
            var (shadowScale, shadowTranslate) = UiMotion.EnsureScaleTranslateGroup(bakedShadow);
            shadowScale.ScaleX = 1;
            shadowScale.ScaleY = 1;
            shadowTranslate.X = 0;
            shadowTranslate.Y = 0;
            return;
        }

        if (host.Effect is DropShadowEffect { IsFrozen: false })
            host.Effect = null;
    }

    private static void ResetRestState(FrameworkElement element)
    {
        if (ShouldSuppressShadowMotion(element))
        {
            ForceRestHoverState(element);
            return;
        }

        var host = ResolveMotionHost(element);
        SurfaceLight.Reset(host);
        var profile = GetProfile(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);

        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
        {
            bakedShadow.Opacity = profile == ChromeHoverMotionProfile.RecentCard ? .72 : RestBakedOpacity(profile);
            if (bakedShadow is CardShadowLayer { FollowsFace: true }) return;
            bakedShadow.RenderTransformOrigin = new Point(0.5, 0.5);
            var (shadowScale, shadowTranslate) = UiMotion.EnsureScaleTranslateGroup(bakedShadow);
            shadowScale.ScaleX = 1;
            shadowScale.ScaleY = 1;
            shadowTranslate.X = 0;
            shadowTranslate.Y = 0;
            return;
        }

        // Existing effects stay static; templates with baked shadow layers own feedback.
    }

    private static void PlayHover(FrameworkElement element, bool isEnter)
    {
        if (ShouldSuppressShadowMotion(element) || !MotionPolicy.Spatial)
            return;

        if (GetState(element)?.IsPressed == true && isEnter)
            return;

        var host = ResolveMotionHost(element);
        var profile = GetProfile(element);
        var durationMs = isEnter ? UiMotion.HoverInMs : UiMotion.HoverOutMs;
        var targetLift = isEnter ? GetHoverLiftY(profile) : 0.0;
        var translate = UiMotion.ChannelTranslate(host, MotionChannel.Interaction);

        var tracks = new List<NativeMotionTrack> { new(translate, TranslateTransform.YProperty, targetLift, true) };
        // Recent items are content cards: use exactly the same wash, edge and
        // timing as CardSurfaceMotionBehavior, rather than a command-button light.
        if (profile == ChromeHoverMotionProfile.RecentCard || CommandLightBehavior.LightFor(element) is null)
            SurfaceLight.AddTracks(host, tracks, isEnter);
        else SurfaceLight.Reset(host);
        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
            tracks.Add(new(bakedShadow, UIElement.OpacityProperty,
                profile == ChromeHoverMotionProfile.RecentCard ? isEnter ? 1 : .72 :
                isEnter ? GetHoverShadowOpacity(profile) : GetRestShadowOpacity(profile)));
        UiMotion.Stop(UiMotion.Channel(host, MotionChannel.Interaction));
        SurfaceMotion.AnimateTracks(host, MotionChannel.Interaction, tracks, durationMs, UiMotion.OutCubic);
    }

    private static void PlayPress(FrameworkElement element, bool isPressed)
    {
        if (!MotionPolicy.Spatial) return;
        var host = ResolveMotionHost(element);
        var target = isPressed ? UiMotion.PressNudgeY : element.IsMouseOver ? GetHoverLiftY(GetProfile(element)) : 0;
        var translate = UiMotion.ChannelTranslate(host, MotionChannel.Interaction);
        UiMotion.Stop(UiMotion.Channel(host, MotionChannel.Interaction));
        SurfaceMotion.AnimateTracks(host, MotionChannel.Interaction,
            [new(translate, TranslateTransform.YProperty, target, true)], isPressed ? 80 : 120, UiMotion.OutCubic);
    }

    private static bool TryResolveBakedShadowLayer(FrameworkElement element, out FrameworkElement bakedShadow)
    {
        bakedShadow = null!;

        var profile = GetProfile(element);
        if (profile is not (ChromeHoverMotionProfile.RecentCard
                or ChromeHoverMotionProfile.Fab)
            || element is not Control control)
            return false;

        control.ApplyTemplate();
        var shadowName = GetShadowElementName(control);
        if (!string.IsNullOrWhiteSpace(shadowName)
            && control.Template.FindName(shadowName, control) is FrameworkElement namedLayer)
        {
            bakedShadow = namedLayer;
            return true;
        }

        return false;
    }



    private static FrameworkElement ResolveMotionHost(FrameworkElement element)
    {
        if (element is not Control control)
            return element;

        control.ApplyTemplate();
        var hostName = GetMotionHostName(element);
        if (!string.IsNullOrWhiteSpace(hostName)
            && control.Template.FindName(hostName, control) is FrameworkElement namedHost)
        {
            namedHost.RenderTransformOrigin = new Point(0.5, 0.5);
            return namedHost;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return element;
    }

    private static double RestBakedOpacity(ChromeHoverMotionProfile profile)
        => profile == ChromeHoverMotionProfile.Fab
            ? GetRestShadowOpacity(profile)
            : BakedShadowOpacityRest;

    private static double GetHoverLiftY(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => -3.0,
            ChromeHoverMotionProfile.RecentCard => UiMotion.CardHoverLiftY,
            _ => 0.0
        };

    private static double GetRestShadowOpacity(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 0.20,
            _ => UiMotion.ShadowOpacityRest
        };

    private static double GetHoverShadowOpacity(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 0.30,
            ChromeHoverMotionProfile.RecentCard => UiMotion.ShadowOpacityHover,
            _ => UiMotion.ShadowOpacityHover
        };

    private static bool ShouldSuppressShadowMotion(FrameworkElement element)
        => DashboardScrollRenderGate.IsSmoothScrollRendering(element);

    private static State? GetState(FrameworkElement element)
        => element.GetValue(StateProperty) as State;

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State existing)
            return existing;

        var state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }
}
