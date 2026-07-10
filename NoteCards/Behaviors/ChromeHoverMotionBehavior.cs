using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NoteCards.Animations;
using NoteCards.Controls;

namespace NoteCards.Behaviors;

public enum ChromeHoverMotionProfile
{
    RecentCard,
    Fab,
    FloatingSecondary
}

/// <summary>
/// Hover and press scale/shadow motion for compact chrome surfaces (recent items, FABs).
/// </summary>
public static class ChromeHoverMotionBehavior
{
    private const double BakedShadowOpacityRest = 1.0;
    private const double BakedShadowHoverScale = 1.06;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsPressed;
        public RoutedEventHandler? LoadedHandler;
        public MouseEventHandler? MouseEnterHandler;
        public MouseEventHandler? MouseLeaveHandler;
        public MouseButtonEventHandler? MouseDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
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
            PlayHover(element, element.IsMouseOver);
        };

        element.Loaded += state.LoadedHandler;
        element.Unloaded += (_, _) => Detach(element);
        element.MouseEnter += state.MouseEnterHandler;
        element.MouseLeave += state.MouseLeaveHandler;
        element.PreviewMouseLeftButtonDown += state.MouseDownHandler;
        element.PreviewMouseLeftButtonUp += state.MouseUpHandler;
        state.IsAttached = true;

        if (element.IsLoaded)
            ResetRestState(element);
    }

    private static void Detach(FrameworkElement element)
    {
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

        UiMotion.Cleanup(element);
        state.IsAttached = false;
        state.IsPressed = false;
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
        UiMotion.Stop(host);

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
        {
            UiMotion.Stop(bakedShadow);
            bakedShadow.Opacity = BakedShadowOpacityRest;
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
        var profile = GetProfile(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);

        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
        {
            bakedShadow.Opacity = BakedShadowOpacityRest;
            bakedShadow.RenderTransformOrigin = new Point(0.5, 0.5);
            var (shadowScale, shadowTranslate) = UiMotion.EnsureScaleTranslateGroup(bakedShadow);
            shadowScale.ScaleX = 1;
            shadowScale.ScaleY = 1;
            shadowTranslate.X = 0;
            shadowTranslate.Y = 0;
            return;
        }

        var shadow = ResolveDropShadowTarget(element, host, profile);
        shadow.Opacity = GetRestShadowOpacity(profile);
        shadow.BlurRadius = GetRestShadowBlur(profile);
    }

    private static void PlayHover(FrameworkElement element, bool isEnter)
    {
        if (ShouldSuppressShadowMotion(element))
            return;

        if (GetState(element)?.IsPressed == true && isEnter)
            return;

        var host = ResolveMotionHost(element);
        var profile = GetProfile(element);
        var durationMs = isEnter ? UiMotion.HoverInMs : UiMotion.HoverOutMs;
        var targetScale = isEnter ? GetHoverScale(profile) : 1.0;
        var targetLift = isEnter ? GetHoverLiftY(profile) : 0.0;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(host);
        var scaleFrom = scale.ScaleX;
        var liftFrom = translate.Y;

        if (TryResolveBakedShadowLayer(element, out var bakedShadow))
        {
            bakedShadow.RenderTransformOrigin = new Point(0.5, 0.5);
            var (shadowScale, _) = UiMotion.EnsureScaleTranslateGroup(bakedShadow);
            var targetShadowScale = isEnter ? BakedShadowHoverScale : 1.0;
            var shadowScaleFrom = shadowScale.ScaleX;
            var tracks = new[]
            {
                UiMotion.Track(durationMs, UiMotion.OutCubic, scaleFrom, targetScale, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                }),
                UiMotion.Track(durationMs, UiMotion.OutCubic, liftFrom, targetLift, value => translate.Y = value),
                UiMotion.Track(durationMs, UiMotion.OutCubic, shadowScaleFrom, targetShadowScale, value =>
                {
                    shadowScale.ScaleX = value;
                    shadowScale.ScaleY = value;
                })
            };

            UiMotion.Stop(host);
            UiMotion.Run(host, tracks);
            return;
        }

        var shadow = ResolveDropShadowTarget(element, host, profile);
        var opacityFromEffect = shadow.Opacity;
        var opacityToEffect = isEnter ? GetHoverShadowOpacity(profile) : GetRestShadowOpacity(profile);

        if (isEnter)
            shadow.BlurRadius = GetHoverShadowBlur(profile);

        var effectTracks = new[]
        {
            UiMotion.Track(durationMs, UiMotion.OutCubic, scaleFrom, targetScale, value =>
            {
                scale.ScaleX = value;
                scale.ScaleY = value;
            }),
            UiMotion.Track(durationMs, UiMotion.OutCubic, liftFrom, targetLift, value => translate.Y = value),
            UiMotion.Track(durationMs, UiMotion.OutCubic, opacityFromEffect, opacityToEffect, value => shadow.Opacity = value)
        };

        UiMotion.Stop(host);
        UiMotion.Run(
            host,
            effectTracks,
            onComplete: isEnter
                ? null
                : () => shadow.BlurRadius = GetRestShadowBlur(profile));
    }

    private static void PlayPress(FrameworkElement element, bool isPressed)
    {
        var profile = GetProfile(element);
        var host = ResolveMotionHost(element);
        var targetScale = isPressed ? GetPressScale(profile) : element.IsMouseOver ? GetHoverScale(profile) : 1.0;
        var (scale, _) = UiMotion.EnsureScaleTranslateGroup(host);
        var scaleFrom = scale.ScaleX;
        var tracks = new[]
        {
            UiMotion.Track(UiMotion.QuickMs, UiMotion.OutCubic, scaleFrom, targetScale, value =>
            {
                scale.ScaleX = value;
                scale.ScaleY = value;
            })
        };

        UiMotion.Run(host, tracks);
    }

    private static bool TryResolveBakedShadowLayer(FrameworkElement element, out FrameworkElement bakedShadow)
    {
        bakedShadow = null!;

        if (GetProfile(element) != ChromeHoverMotionProfile.RecentCard
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

    private static DropShadowEffect ResolveDropShadowTarget(
        FrameworkElement element,
        FrameworkElement host,
        ChromeHoverMotionProfile profile)
    {
        if (ShouldSuppressShadowMotion(element))
        {
            if (DashboardScrollRenderGate.ResolveShadowHost(element) is { Effect: DropShadowEffect existing }
                && !existing.IsFrozen)
                return existing;

            if (host.Effect is DropShadowEffect direct && !direct.IsFrozen)
                return direct;

            return ScrollShadowDiscard;
        }

        if (profile != ChromeHoverMotionProfile.RecentCard
            || element is not Control control)
            return UiMotion.EnsureShadow(host);

        control.ApplyTemplate();
        var shadowName = GetMotionHostName(element);
        if (!string.IsNullOrWhiteSpace(shadowName)
            && control.Template.FindName(shadowName, control) is FrameworkElement shadowHost)
            return UiMotion.EnsureShadow(shadowHost);

        return UiMotion.EnsureShadow(host);
    }

    private static FrameworkElement ResolveMotionHost(FrameworkElement element)
    {
        if (GetProfile(element) == ChromeHoverMotionProfile.RecentCard)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            return element;
        }

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

    private static double GetHoverScale(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 1.04,
            ChromeHoverMotionProfile.FloatingSecondary => 1.03,
            _ => UiMotion.HoverScale
        };

    private static double GetPressScale(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 0.96,
            ChromeHoverMotionProfile.FloatingSecondary => 0.97,
            _ => 0.98
        };

    private static double GetHoverLiftY(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => -2.0,
            ChromeHoverMotionProfile.FloatingSecondary => -2.0,
            ChromeHoverMotionProfile.RecentCard => -3.0,
            _ => 0.0
        };

    private static double GetRestShadowOpacity(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 0.20,
            ChromeHoverMotionProfile.FloatingSecondary => 0.20,
            _ => UiMotion.ShadowOpacityRest
        };

    private static double GetHoverShadowOpacity(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 0.30,
            ChromeHoverMotionProfile.FloatingSecondary => 0.28,
            ChromeHoverMotionProfile.RecentCard => UiMotion.ShadowOpacityHover,
            _ => UiMotion.ShadowOpacityHover
        };

    private static double GetRestShadowBlur(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 16,
            ChromeHoverMotionProfile.FloatingSecondary => 16,
            _ => 6
        };

    private static double GetHoverShadowBlur(ChromeHoverMotionProfile profile)
        => profile switch
        {
            ChromeHoverMotionProfile.Fab => 22,
            ChromeHoverMotionProfile.FloatingSecondary => 20,
            _ => UiMotion.ShadowBlurHover
        };

    private static DropShadowEffect ScrollShadowDiscard { get; } = new()
    {
        Opacity = 0,
        BlurRadius = 0,
        ShadowDepth = 0
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