using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Scriblucent.Animations;

/// <summary>
/// Single entry point for in-app UI motion. Layout and interaction transitions use
/// <see cref="Run"/> and the display-aligned <see cref="RenderTransitionClock"/>.
/// Floating surfaces and popups use WPF clocks to avoid per-frame managed writes.
/// </summary>
public static class UiMotion
{
    public const int OpenMs = 220;
    public const int CloseMs = 140;
    public const int DeleteMs = 120;
    public const int NoteDeleteMs = 120;
    public const int NoteAddReflowMs = 220;
    public const int NoteAddEnterMs = 280;
    public const int NoteAddEnterDelayMs = 24;
    public const int NoteAddMs = NoteAddEnterMs;
    public const double NoteEnterScale = 0.94;
    public const int EnterOpacityMs = 280;
    public const int EnterScaleMs = 280;
    public const int HoverInMs = 160;
    public const int HoverOutMs = 180;
    public const int FastMs = 120;
    public const int FlashcardFlipMinMs = 120;
    public const int QuickMs = 150;
    public const int ReflowMs = 220;
    public const int ReflowStaggerMs = 24;
    public const int ReflowStaggerCapMs = 96;
    public const int ReflowStaggerMaxItems = 8;
    public const int BulkAddLayoutHideThreshold = 2;
    public const int MaxBulkAnimatedEnters = 8;
    public const int MaxBulkAnimatedExits = 8;
    public const int NavFadeOutMs = 70;
    public const int SectionMs = 220;
    public const int SidebarMs = 220;
    public const int SidebarResizeMs = 260;
    public const int SearchMs = 120;
    public const int DashboardMs = 180;
    public const int DashboardCrossFadeOutMs = 180;
    public const int DashboardCrossFadeInMs = 180;
    public const int DashboardCrossFadeInDelayMs = 0;
    public const double SubtleLiftY = 6;
    public const double ContentLiftY = 8;
    public const double PanelLiftY = 14;
    public const double WindowInitialOpacity = 0.45;
    public const int WindowMotionMs = 300;
    public const int WindowCloseMs = 140;
    public const double CardEnterLiftY = 14;
    public const double NavShiftX = 18;
    public const double StudyNavShiftX = 18;
    public const double CrossFadeShiftX = 0;
    public const double CardHoverLiftY = -5.0;
    public const double ChipHoverLiftY = -2.0;
    public const double PressNudgeY = 1.0;
    public const double EnterScale = 0.94;
    public const double ExitScale = 0.96;
    public const double CollapsedScale = 0.98;
    public const double ShadowBlurRest = 8;
    public const double ShadowBlurHover = 14;
    public const double ShadowOpacityRest = 0.08;
    public const double ShadowOpacityHover = 0.18;

    private static readonly IEasingFunction EaseOutCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseInOutCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction EaseOutSine = Freeze(new SineEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInSine = Freeze(new SineEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseInOutSine = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction EaseOutQuad = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInQuad = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseOutQuint = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseOutBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });

    private static readonly ConditionalWeakTable<object, RenderTransitionClock> Clocks = new();
    private static readonly ConditionalWeakTable<object, Dictionary<MotionChannel, object>> ChannelKeys = new();
    private sealed record ChannelOwner(WeakReference<UIElement> Element, MotionChannel Channel);
    private static readonly ConditionalWeakTable<object, ChannelOwner> ChannelOwners = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Dictionary<MotionChannel, TranslateTransform>> ChannelTransforms = new();
    private static readonly ConditionalWeakTable<FrameworkElement, MotionPrepState> MotionPrepStates = new();
    private static readonly ConditionalWeakTable<object, PopupMotionMarker> PopupMotionAttached = new();
    private static readonly ConditionalWeakTable<FrameworkElement, InlineStatusTimer> InlineStatusTimers = new();

    private sealed class InlineStatusTimer
    {
        internal readonly DispatcherTimer Timer = new();
        internal RoutedEventHandler? Unloaded;
    }

    private sealed class PopupMotionMarker;

    static UiMotion()
    {
        // Settle clocks before clearing held hover/press offsets on live accessibility changes.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RenderTransitionClock).TypeHandle);
        MotionPolicy.Changed += () =>
        {
            if (MotionPolicy.Spatial) return;
            foreach (var entry in ChannelTransforms)
                if (entry.Value.TryGetValue(MotionChannel.Interaction, out var translate))
                {
                    if (entry.Key.Dispatcher.CheckAccess()) translate.X = translate.Y = 0;
                    else if (entry.Key.Dispatcher.Thread.IsAlive && !entry.Key.Dispatcher.HasShutdownStarted)
                        entry.Key.Dispatcher.BeginInvoke(() => translate.X = translate.Y = 0);
                }
        };
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnContextMenuOpening));
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnComboBoxLoadedForPopupMotion));
    }

    private static void OnComboBoxLoadedForPopupMotion(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
            AttachComboBoxDropdownAnimation(comboBox);
    }

    private sealed class MotionPrepState
    {
        public CacheMode? PreviousCacheMode;
        public Effect? SavedEffect;
        public int Depth;
        public double ReservedPixels;
        public RoutedEventHandler? Unloaded;
    }

    public static IEasingFunction OutCubic => EaseOutCubic;
    public static IEasingFunction InCubic => EaseInCubic;
    public static IEasingFunction InOutCubic => EaseInOutCubic;
    public static IEasingFunction OutSine => EaseOutSine;
    public static IEasingFunction InSine => EaseInSine;
    public static IEasingFunction InOutSine => EaseInOutSine;
    public static IEasingFunction OutQuad => EaseOutQuad;
    public static IEasingFunction InQuad => EaseInQuad;
    public static IEasingFunction OutQuint => EaseOutQuint;
    public static IEasingFunction OutBack => EaseOutBack;

    public static double Lerp(double from, double to, double eased) => from + (to - from) * eased;

    /// <summary>
    /// True when Windows accessibility / performance settings request reduced UI motion.
    /// </summary>
    public static bool PrefersReducedMotion => ReducedMotionPreferences.ShouldReduceMotion;

    public static bool IsAnyMotionRunning => RenderTransitionClock.IsAnyRunning;
    public static bool SuppressChildEntrances { get; set; }

    public static bool ShouldSkipDashboardRevealMotion()
    {
        if (PrefersReducedMotion || SuppressChildEntrances)
            return true;

        return Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel { IsDashboardRevealMotionEnabled: false };
    }

    public static void Stop(object key)
    {
        if (key is UIElement element) SurfaceMotion.Stop(element);
        else if (ChannelOwners.TryGetValue(key, out var owner) && owner.Element.TryGetTarget(out var target))
            SurfaceMotion.Stop(target, owner.Channel);
        if (Clocks.TryGetValue(key, out var clock))
            clock.Stop();
    }

    public static object Channel(object key, MotionChannel channel)
    {
        var channels = ChannelKeys.GetValue(key, _ => new());
        if (!channels.TryGetValue(channel, out var value))
        {
            channels[channel] = value = new object();
            if (key is UIElement element) ChannelOwners.Add(value, new(new(element), channel));
        }
        return value;
    }

    public static TranslateTransform ChannelTranslate(FrameworkElement element, MotionChannel channel)
    {
        var transforms = ChannelTransforms.GetValue(element, _ => new());
        if (transforms.TryGetValue(channel, out var existing)) return existing;
        var group = element.RenderTransform as TransformGroup;
        if (group is null || group.IsFrozen)
        {
            var original = element.RenderTransform;
            group = new TransformGroup();
            if (original is not null && original != Transform.Identity)
                group.Children.Add(original.IsFrozen ? original.Clone() : original);
            element.RenderTransform = group;
        }
        var translate = new TranslateTransform();
        group.Children.Add(translate);
        transforms[channel] = translate;
        return translate;
    }

    public static void Run(object key, MotionChannel channel, IEnumerable<RenderTransitionClock.Track> tracks, Action? onComplete = null)
        => Run(Channel(key, channel), tracks, onComplete);

    public static void Cleanup(FrameworkElement element)
    {
        WindowMotion.Stop(element);
        Stop(element);
        SurfaceMotion.Stop(element);
        if (ChannelKeys.TryGetValue(element, out var channels))
            foreach (var key in channels.Values) Stop(key);
        if (ChannelTransforms.TryGetValue(element, out var transforms))
            foreach (var translate in transforms.Values) translate.X = translate.Y = 0;
        EndMotionPrep(element);
    }

    public static void Run(object key, IEnumerable<RenderTransitionClock.Track> tracks, Action? onComplete = null)
    {
        var clock = Clocks.GetValue(key, _ => new RenderTransitionClock());
        MotionPolicy.ObserveActiveWindow();
        clock.Run(tracks, onComplete);
    }

    public static void BeginMotionPrep(FrameworkElement element, bool suppressEffect = true)
    {
        var state = MotionPrepStates.GetValue(element, _ => new MotionPrepState());
        if (state.Depth == 0)
        {
            state.PreviousCacheMode = element.CacheMode;
            state.ReservedPixels = MotionCacheBudget.TryReserve(element);
            if (state.ReservedPixels > 0)
                element.CacheMode = DpiAwareBitmapCache.Create(element, enableClearType: false);
            state.Unloaded = (_, _) => { state.Depth = 1; EndMotionPrep(element); };
            element.Unloaded += state.Unloaded;

            if (suppressEffect && element.Effect is not null)
            {
                state.SavedEffect = element.Effect;
                element.Effect = null;
            }
        }

        state.Depth++;
    }

    public static void EndMotionPrep(FrameworkElement element)
    {
        if (!MotionPrepStates.TryGetValue(element, out var state) || state.Depth <= 0)
            return;

        state.Depth--;
        if (state.Depth > 0)
            return;

        element.CacheMode = state.PreviousCacheMode;
        element.Unloaded -= state.Unloaded;
        state.Unloaded = null;
        MotionCacheBudget.Release(state.ReservedPixels);
        state.ReservedPixels = 0;
        if (state.SavedEffect is Effect savedEffect)
        {
            // If a scroll gesture is still under way, restoring here would stomp the scroll
            // shadow suspension (this element's shadow would go live again mid-scroll even
            // though DashboardShadowSuspender still considers it suspended). Hand the saved
            // effect to that system instead so it stays off until the scroll actually ends.
            if (savedEffect is DropShadowEffect dropShadow
                && Scriblucent.Controls.DashboardScrollRenderGate.IsSmoothScrollRendering(element))
            {
                DashboardShadowSuspender.AdoptSuspendedEffect(
                    DashboardShadowSuspender.AnimatedScrollShadowScope,
                    element,
                    dropShadow);
            }
            else
            {
                element.Effect = savedEffect;
            }
        }

        state.PreviousCacheMode = null;
        state.SavedEffect = null;
    }

    public static void RunComposited(
        FrameworkElement element,
        IEnumerable<RenderTransitionClock.Track> tracks,
        Action? onComplete = null,
        bool suppressEffect = true)
    {
        if (PrefersReducedMotion)
        {
            Run(element, tracks, onComplete);
            return;
        }

        Stop(element);
        BeginMotionPrep(element, suppressEffect);
        Clocks.GetValue(element, _ => new RenderTransitionClock()).Run(
            tracks, onComplete, onStopped: () => EndMotionPrep(element));
    }

    public static RenderTransitionClock.Track Track(
        int durationMs,
        IEasingFunction easing,
        double from,
        double to,
        Action<double> apply,
        Action? onComplete = null,
        int startDelayMs = 0)
        => new(
            durationMs,
            easing,
            eased => apply(Lerp(from, to, eased)),
            onComplete,
            startDelayMs);

    public static void Clear(DependencyObject target, DependencyProperty property)
    {
        switch (target)
        {
            case Animatable animatable:
                animatable.BeginAnimation(property, null);
                break;
            case UIElement uiElement:
                uiElement.BeginAnimation(property, null);
                break;
        }
    }

    public static void Fade(
        UIElement element,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null,
        int startDelayMs = 0)
    {
        SurfaceMotion.Animate(element, Math.Clamp(to, 0, 1), durationMs,
            easing ?? (to >= element.Opacity ? EaseOutCubic : EaseInQuad),
            false, onComplete, startDelayMs: startDelayMs);
    }

    public static void AnimateGridLength(
        object key,
        ColumnDefinition column,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        Clear(column, ColumnDefinition.WidthProperty);
        Run(
            key,
            new[]
            {
                Track(
                    durationMs,
                    easing ?? EaseInOutCubic,
                    from,
                    to,
                    value => column.Width = new GridLength(value, GridUnitType.Pixel),
                    onComplete)
            });
    }

    public static void AnimateRowHeight(
        object key,
        RowDefinition row,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        Clear(row, RowDefinition.HeightProperty);
        Run(
            key,
            new[]
            {
                Track(
                    durationMs,
                    easing ?? EaseOutQuad,
                    from,
                    to,
                    value => row.Height = new GridLength(value, GridUnitType.Pixel),
                    onComplete)
            });
    }

    public static void AnimateMaxHeight(
        FrameworkElement element,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        Clear(element, FrameworkElement.MaxHeightProperty);
        Run(
            element,
            new[]
            {
                Track(
                    durationMs,
                    easing ?? EaseInOutSine,
                    from,
                    to,
                    value => element.MaxHeight = value,
                    onComplete)
            });
    }

    public static void AnimateThickness(
        Border border, Thickness from, Thickness to, int durationMs, IEasingFunction? easing = null)
    {
        var thickness = Math.Max(to.Left, Math.Max(to.Right, Math.Max(to.Top, to.Bottom)));
        var normal = Math.Max(border.BorderThickness.Left, Math.Max(border.BorderThickness.Right,
            Math.Max(border.BorderThickness.Top, border.BorderThickness.Bottom)));
        AccentMotion.Set(border, thickness > normal, border.BorderBrush, border.CornerRadius.TopLeft, thickness);
    }

    public static void AnimateTranslateY(
        FrameworkElement element,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);
        Run(
            element,
            new[]
            {
                Track(durationMs, easing ?? EaseOutCubic, from, to, value => translate.Y = value, onComplete)
            });
    }

    public static void AnimateScale(
        FrameworkElement element,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        var scale = EnsureScale(element);
        Clear(scale, ScaleTransform.ScaleXProperty);
        Clear(scale, ScaleTransform.ScaleYProperty);
        Run(
            element,
            new[]
            {
                Track(durationMs, easing ?? EaseOutCubic, from, to, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                }, onComplete)
            });
    }

    public static void AnimateScaleXY(
        ScaleTransform scale,
        object key,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        Clear(scale, ScaleTransform.ScaleXProperty);
        Clear(scale, ScaleTransform.ScaleYProperty);
        Run(
            key,
            new[]
            {
                Track(durationMs, easing ?? EaseOutCubic, from, to, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                }, onComplete)
            });
    }

    public static void AnimateProgress(FrameworkElement fill, double trackWidth, double ratio, bool animate = true)
    {
        fill.Width = Math.Max(0, trackWidth);
        fill.RenderTransformOrigin = new Point(0, .5);
        var scale = EnsureScale(fill);
        scale.ScaleY = 1;
        var target = Math.Clamp(ratio, 0, 1);
        if (!animate || !MotionPolicy.Spatial) { Stop(Channel(fill, MotionChannel.Layout)); scale.ScaleX = target; return; }
        Run(fill, MotionChannel.Layout, new[]
        {
            Track(180, OutCubic, scale.ScaleX, target, v => scale.ScaleX = v)
        });
    }

    public static void AnimateShadowOpacity(
        DropShadowEffect shadow,
        object key,
        double from,
        double to,
        int durationMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        Run(
            key,
            new[]
            {
                Track(durationMs, easing ?? EaseOutCubic, from, to, value => shadow.Opacity = value, onComplete)
            });
    }

    /// <summary>Reveals stationary content by expanding its clipped layout slot.</summary>
    public static void AnimateLayoutReveal(FrameworkElement content, bool show,
        Action? onComplete = null, Orientation axis = Orientation.Vertical,
        bool animate = true, RowDefinition? row = null, bool soften = false, int? durationMs = null)
        => LayoutRevealMotion.SetVisible(content, show, axis, animate, onComplete, row, soften, durationMs);

    public static void AnimateSidebarChrome(
        FrameworkElement sidebarChrome, ColumnDefinition column, double fromWidth,
        double targetWidth, int durationMs = SidebarResizeMs, Action? onComplete = null)
    {
        SidebarResizeMotion.Animate(sidebarChrome, column, fromWidth, targetWidth, durationMs, onComplete);
    }

    private static void SetSidebarAnimationZOrder(FrameworkElement sidebarChrome, bool elevated)
    {
        if (elevated)
            Panel.SetZIndex(sidebarChrome, 1);
        else
            sidebarChrome.ClearValue(Panel.ZIndexProperty);
    }

    private static readonly object SidebarChromeAnimationKey = new();

    public static void CrossFadePanels(
        FrameworkElement outgoing, FrameworkElement incoming, int direction,
        int durationMs = MotionTokens.Navigation, Action? onComplete = null)
    {
        SurfaceMotion.Stop(outgoing);
        SurfaceMotion.Stop(incoming);
        ResetDashboardPanelMotion(outgoing);
        ResetDashboardPanelMotion(incoming);
        outgoing.Visibility = incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        if (MotionPolicy.Quality == MotionQuality.Off)
        {
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Opacity = 1;
            onComplete?.Invoke();
            return;
        }
        SurfaceMotion.Animate(outgoing, 0, durationMs, InQuad, false, cache: true);
        SurfaceMotion.Animate(incoming, 1, durationMs, OutSine, false, () =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            onComplete?.Invoke();
        }, cache: true);
    }

    /// <summary>
    /// Cross-fades large dashboard views using bitmap caches so both panels do not re-render every frame.
    /// </summary>
    public static void CrossFadePanelsComposited(
        FrameworkElement outgoing, FrameworkElement incoming, int direction,
        int durationMs, Action? onComplete = null)
    {
        CrossFadePanels(outgoing, incoming, direction, durationMs, onComplete);
    }

    /// <summary>
    /// Opacity cross-dissolve for dashboard tab switches — no horizontal slide (avoids presentation-style motion).
    /// </summary>
    public static void CrossFadeDashboardPanelsComposited(
        FrameworkElement outgoing, FrameworkElement incoming, Action? onComplete = null)
    {
        CrossFadePanels(outgoing, incoming, 0, MotionTokens.Navigation, onComplete);
    }

    private static void ResetDashboardPanelMotion(FrameworkElement element)
    {
        Stop(element);
        element.ClearValue(UIElement.OpacityProperty);
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            foreach (var child in group.Children)
            {
                if (child is ScaleTransform scale)
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
                else if (child is TranslateTransform translate)
                {
                    translate.X = 0;
                    translate.Y = 0;
                }
            }

            return;
        }

        ResetTranslate(element);
        if (element.RenderTransform is ScaleTransform loneScale && !loneScale.IsFrozen)
        {
            loneScale.ScaleX = 1;
            loneScale.ScaleY = 1;
        }
    }

    public const int StaggerEnterStepMs = 24;
    public const double StaggerEnterScale = 0.94;
    public const int EmptyDashboardPanelMs = 480;
    public const int EmptyDashboardStaggerStepMs = 40;
    public const double EmptyDashboardEnterLiftY = 20;
    public const double EmptyDashboardEnterScale = 0.88;
    public const int ContentHostEnterMs = 220;
    public const double ContentHostEnterLiftY = 8;
    public const int GroupEnterMs = 220;
    public const int GroupExitMs = 120;
    public const double GroupEnterLiftY = 6;

    public static void AnimateStaggeredContentEnter(
        IReadOnlyList<FrameworkElement> elements,
        int durationMs = SectionMs,
        double offsetY = PanelLiftY,
        int staggerMs = StaggerEnterStepMs,
        Action? onComplete = null,
        double enterScale = StaggerEnterScale)
    {
        if (elements.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        if (PrefersReducedMotion)
        {
            foreach (var element in elements)
                SnapStaggeredEnterRest(element);

            onComplete?.Invoke();
            return;
        }

        var remaining = elements.Count;
        void MarkOneComplete()
        {
            remaining--;
            if (remaining <= 0)
                onComplete?.Invoke();
        }

        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (index >= MotionTokens.MaxItems) { SnapStaggeredEnterRest(elements[index]); MarkOneComplete(); continue; }
            var delayMs = Math.Min(index * staggerMs, MotionTokens.MaxStagger);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            Stop(element);
            Clear(element, UIElement.OpacityProperty);
            var (scale, translate) = EnsureScaleTranslateGroup(element);
            Clear(translate, TranslateTransform.YProperty);
            Clear(scale, ScaleTransform.ScaleXProperty);
            Clear(scale, ScaleTransform.ScaleYProperty);
            element.Opacity = 0;
            translate.Y = offsetY;
            scale.ScaleX = enterScale;
            scale.ScaleY = enterScale;

            SurfaceMotion.AnimateTracks(element, MotionChannel.Presence,
                [new(element, UIElement.OpacityProperty, 1, Profile: MotionProfiles.Readable),
                 new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.008, 1, MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.008, 1, MotionProfiles.Arrival)],
                durationMs, InOutSine, MarkOneComplete, cache: true, startDelayMs: delayMs);
        }
    }

    public static void AnimateEmptyDashboardEnter(FrameworkElement panel, Action? onComplete = null)
    {
        var container = ResolveEmptyDashboardMotionRoot(panel);
        if (container is null) { AnimateContentEnter(panel, 240, 14, onComplete); return; }
        var elements = CollectEmptyDashboardMotionChildren(container);
        if (elements.Count == 0) { onComplete?.Invoke(); return; }
        Stop(panel); panel.Opacity = 1; ResetTranslate(panel);
        var remaining = elements.Count;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i]; Stop(element);
            element.RenderTransformOrigin = new Point(.5, .5);
            var (scale, translate) = EnsureScaleTranslateGroup(element);
            var graphic = i == 0;
            scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial && graphic ? .88 : 1;
            element.Opacity = 0;
            translate.Y = MotionPolicy.Spatial ? graphic ? 20 : 8 : 0;
            var tracks = new List<NativeMotionTrack>
            {
                new(element, UIElement.OpacityProperty, 1, Profile: MotionProfiles.Readable),
                new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival)
            };
            if (graphic)
            {
                tracks.Add(new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.025, .997, MotionProfiles.Showcase));
                tracks.Add(new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.025, .997, MotionProfiles.Showcase));
            }
            SurfaceMotion.AnimateTracks(element, MotionChannel.Showcase, tracks,
                graphic ? 480 : 240, InOutSine, () => { if (--remaining == 0) onComplete?.Invoke(); },
                cache: graphic, startDelayMs: Math.Min(80, i * 40));
        }
        if (panel is Scriblucent.Controls.Dashboard.EmptyDashboardPanel empty) empty.PlayIllustration();
    }

    private static Panel? ResolveEmptyDashboardMotionRoot(FrameworkElement panel)
    {
        if (panel is Panel direct)
            return direct;

        return panel.FindName("MotionRoot") is Panel named ? named : null;
    }

    private static List<FrameworkElement> CollectEmptyDashboardMotionChildren(Panel container)
        => container.Children
            .OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible)
            .ToList();

    private static void SnapStaggeredEnterRest(FrameworkElement element)
    {
        element.Opacity = 1;
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            if (group.Children.OfType<TranslateTransform>().FirstOrDefault() is { IsFrozen: false } translate)
                translate.X = translate.Y = 0;
            if (group.Children.OfType<ScaleTransform>().FirstOrDefault() is { IsFrozen: false } scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            return;
        }

        ResetTranslate(element);
        var loneScale = EnsureScale(element);
        loneScale.ScaleX = 1;
        loneScale.ScaleY = 1;
    }

    public static void PrepDashboardContentRevealStart(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);
        element.Opacity = 0;
        translate.Y = ContentHostEnterLiftY;
    }

    public static void AnimateDashboardContentHostEnter(
        FrameworkElement element,
        int startDelayMs = 0,
        Action? onComplete = null)
    {
        void Complete()
        {
            EnsureContentRevealRestState(element);
            onComplete?.Invoke();
        }

        if (PrefersReducedMotion)
        {
            Complete();
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        Stop(element);
        PrepDashboardContentRevealStart(element);
        var translate = EnsureTranslate(element);

        Run(
            element,
            new[]
            {
                Track(ContentHostEnterMs, OutSine, 0, 1, value => element.Opacity = value, startDelayMs: startDelayMs),
                Track(
                    ContentHostEnterMs,
                    InOutSine,
                    ContentHostEnterLiftY,
                    0,
                    value => translate.Y = value,
                    onComplete: Complete,
                    startDelayMs: startDelayMs)
            });
    }

    public static void EnsureContentRevealRestState(FrameworkElement element)
    {
        Stop(element);
        element.ClearValue(UIElement.OpacityProperty);
        ResetTranslate(element);
    }

    public static void AnimateContentEnter(
        FrameworkElement element, int durationMs = SectionMs, double offsetY = PanelLiftY,
        Action? onComplete = null)
    {
        Stop(element);
        var (_, translate) = EnsureScaleTranslateGroup(element);
        element.Opacity = 0;
        translate.Y = MotionPolicy.Spatial ? offsetY : 0;
        SurfaceMotion.Arrive(element, durationMs, onComplete, translate);
    }

    public static void AnimateContentExit(
        FrameworkElement element, int durationMs = CloseMs, double offsetY = PanelLiftY,
        Action? onComplete = null)
    {
        var (_, translate) = EnsureScaleTranslateGroup(element);
        SurfaceMotion.Animate(element, 0, durationMs, InQuad, false, onComplete,
            translate, MotionPolicy.Spatial ? offsetY : 0);
    }

    /// <summary>
    /// Move the complete client stage and its baked shadow as one object, with short
    /// local reveals for real chrome and actions. Large editors avoid group opacity.
    /// </summary>
    public static void AnimateWindowContentEnter(
        FrameworkElement element, int durationMs = 300, Action? onComplete = null)
        => WindowMotion.Enter(element, onComplete);

    public static void AnimateWindowContentExit(
        FrameworkElement element, int durationMs = 140, Action? onComplete = null)
        => WindowMotion.Exit(element, onComplete);

    private const int PopupOpenMs = 180;

    /// <summary>
    /// Prefer the solid card (fill / shadow host) over the transparent padding shell.
    /// </summary>
    private static FrameworkElement ResolvePopupMotionSurface(FrameworkElement root)
    {
        try
        {
            if (root is Border { Child: Border card }
                && (card.Effect is not null || IsPaintingBackground(card.Background)))
            {
                return card;
            }

            if (root is Border { Child: FrameworkElement mid })
            {
                if (mid is Border { Child: Border nested }
                    && (nested.Effect is not null || IsPaintingBackground(nested.Background)))
                {
                    return nested;
                }

                if (mid is Border midBorder)
                    return midBorder;
            }
        }
        catch
        {
            // fall through
        }

        return root;
    }

    private static bool IsPaintingBackground(Brush? brush)
    {
        if (brush is null)
            return false;
        if (ReferenceEquals(brush, Brushes.Transparent))
            return false;
        if (brush is SolidColorBrush solid)
            return solid.Color.A > 0;
        return true;
    }

    /// <summary>
    /// Layered popup HWNDs often ignore manual Opacity writes from CompositionTarget.Rendering.
    /// WPF DoubleAnimation goes through the animation system and repaints the popup.
    /// </summary>
    private static void StopPopupOpacityAnimation(FrameworkElement surface)
    {
        try
        {
            Stop(surface);
            surface.BeginAnimation(UIElement.OpacityProperty, null);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Hide the motion surface before paint so open never flashes solid for a frame.
    /// Close is always instant — no fade-out.
    /// </summary>
    public static void PreparePopupOpenVisual(FrameworkElement root)
    {
        try
        {
            var surface = ResolvePopupMotionSurface(root);
            StopPopupOpacityAnimation(surface);
            EndMotionPrep(surface);
            StopPopupOpacityAnimation(root);
            root.Opacity = 1;
            surface.Opacity = 0;
        }
        catch
        {
            // Element may already be disconnecting.
        }
    }

    public static void AnimatePopupOpen(FrameworkElement root, int durationMs = PopupOpenMs, FrameworkElement? anchor = null)
    {
        try
        {
            var surface = ResolvePopupMotionSurface(root);
            root.Opacity = 1;
            StopPopupOpacityAnimation(surface);
            surface.Opacity = 0;
            surface.RenderTransformOrigin = new Point(.5, 0);
            if (anchor is not null && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            {
                var anchorPoint = anchor.PointToScreen(new Point(anchor.ActualWidth * .5, anchor.ActualHeight * .5));
                var top = surface.PointToScreen(new Point()); var dpi = VisualTreeHelper.GetDpi(surface);
                surface.RenderTransformOrigin = new Point(Math.Clamp((anchorPoint.X - top.X) / (surface.ActualWidth * dpi.DpiScaleX), 0, 1),
                    Math.Clamp((anchorPoint.Y - top.Y) / (surface.ActualHeight * dpi.DpiScaleY), 0, 1));
            }
            var (scale, translate) = EnsureScaleTranslateGroup(surface);
            scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .96 : 1;
            translate.Y = MotionPolicy.Spatial ? 6 : 0;
            SurfaceMotion.AnimateTracks(surface, MotionChannel.Presence,
                [new(surface, UIElement.OpacityProperty, 1, Profile: MotionProfiles.PopupFade),
                 new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleXProperty, 1, true, Profile: MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleYProperty, 1, true, Profile: MotionProfiles.Arrival)],
                durationMs, InOutSine, cache: true);
        }
        catch { try { ResolvePopupMotionSurface(root).Opacity = 1; } catch { } }
    }

    /// <summary>
    /// Hide immediately on open, then fade in on the next render tick.
    /// </summary>
    public static void AnimatePopupOpenDeferred(
        FrameworkElement root,
        Popup? hostPopup = null,
        int durationMs = PopupOpenMs)
    {
        PreparePopupOpenVisual(root);
        root.Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    if (hostPopup is { IsOpen: false })
                        return;

                    AnimatePopupOpen(root, durationMs, hostPopup?.PlacementTarget as FrameworkElement ?? (root as ContextMenu)?.PlacementTarget as FrameworkElement);
                }
                catch
                {
                    try { ResolvePopupMotionSurface(root).Opacity = 1; } catch { /* ignore */ }
                }
            },
            DispatcherPriority.Render);
    }

    public static void OpenPopup(Popup popup, bool open = true)
    {
        if (popup is null)
            return;

        AttachPopupOpenAnimation(popup);
        popup.IsOpen = open;
        if (!open && popup.Child is FrameworkElement root)
            PreparePopupOpenVisual(root);
    }

    public static void TogglePopup(Popup? popup)
    {
        if (popup is null)
            return;

        if (popup.IsOpen)
            ClosePopup(popup);
        else
            OpenPopup(popup, open: true);
    }

    public static void ClosePopup(Popup? popup)
    {
        if (popup is null || !popup.IsOpen)
            return;

        // Instant close — no fade-out.
        popup.IsOpen = false;
        if (popup.Child is FrameworkElement root)
            PreparePopupOpenVisual(root);
    }

    public static void OpenContextMenu(ContextMenu menu, UIElement placementTarget, PlacementMode placement = PlacementMode.Bottom)
    {
        if (menu is null || placementTarget is null)
            return;

        Scriblucent.Behaviors.ItemReorderBehavior.PrepareContextMenu(menu, placementTarget);
        AttachContextMenuOpenAnimation(menu);
        menu.PlacementTarget = placementTarget;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    public static void AnimateWidth(
        FrameworkElement element,
        double from,
        double to,
        int durationMs = FastMs,
        IEasingFunction? easing = null,
        Action? onComplete = null)
    {
        if (Math.Abs(from - to) < 0.5)
        {
            element.Width = to;
            onComplete?.Invoke();
            return;
        }

        if (PrefersReducedMotion)
        {
            element.Width = to;
            onComplete?.Invoke();
            return;
        }

        Run(
            element,
            new[]
            {
                Track(durationMs, easing ?? OutCubic, from, to, value => element.Width = value, onComplete)
            });
    }

    public static void AttachPopupOpenAnimation(Popup? popup)
    {
        if (popup is null || !TryAttachPopupMotion(popup))
            return;

        // Skip ComboBox PART_Popup here — DropDownOpened owns that path so we never double-animate.
        if (popup.TemplatedParent is ComboBox)
            return;
        popup.PopupAnimation = PopupAnimation.None;

        popup.Opened += OnStandalonePopupOpened;
        popup.Closed += OnStandalonePopupClosed;
    }

    private static void OnStandalonePopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: FrameworkElement child } popup)
            return;

        AnimatePopupOpenDeferred(child, popup);
    }

    private static void OnStandalonePopupClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: FrameworkElement child })
            return;

        PreparePopupOpenVisual(child);
    }

    public static void AttachComboBoxDropdownAnimation(ComboBox comboBox)
    {
        if (comboBox is null || !TryAttachPopupMotion(comboBox))
            return;

        comboBox.ApplyTemplate();
        if (comboBox.Template?.FindName("PART_Popup", comboBox) is Popup popup) popup.PopupAnimation = PopupAnimation.None;
        comboBox.DropDownOpened += OnComboBoxDropDownOpened;
        comboBox.DropDownClosed += OnComboBoxDropDownClosed;
    }

    private static void OnComboBoxDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        if (FindComboBoxPopup(comboBox) is not { Child: FrameworkElement child })
            return;

        if (PrefersReducedMotion)
        {
            PreparePopupOpenVisual(child);
            ResolvePopupMotionSurface(child).Opacity = 1;
            return;
        }

        AnimatePopupOpenDeferred(child, hostPopup: null);
    }

    private static void OnComboBoxDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        if (FindComboBoxPopup(comboBox) is { Child: FrameworkElement child })
            PreparePopupOpenVisual(child);
    }

    private static Popup? FindComboBoxPopup(ComboBox comboBox)
    {
        comboBox.ApplyTemplate();
        if (comboBox.Template is null)
            return null;

        return comboBox.Template.FindName("PART_Popup", comboBox) as Popup
            ?? comboBox.Template.FindName("Popup", comboBox) as Popup;
    }

    public static void AttachContextMenuOpenAnimation(ContextMenu? contextMenu)
    {
        if (contextMenu is null || !TryAttachPopupMotion(contextMenu))
            return;

        contextMenu.Opened += (_, _) => AnimateContextMenuRoot(menu: contextMenu);
        contextMenu.Closed += (_, _) => ResetContextMenuRoot(menu: contextMenu);
    }

    private static void AnimateContextMenuRoot(ContextMenu menu)
    {
        if (VisualTreeHelper.GetChildrenCount(menu) > 0
            && VisualTreeHelper.GetChild(menu, 0) is FrameworkElement root)
        {
            AnimatePopupOpenDeferred(root);
            return;
        }

        menu.Dispatcher.BeginInvoke(
            () =>
            {
                if (VisualTreeHelper.GetChildrenCount(menu) > 0
                    && VisualTreeHelper.GetChild(menu, 0) is FrameworkElement deferredRoot)
                    AnimatePopupOpenDeferred(deferredRoot);
            },
            DispatcherPriority.Render);
    }

    private static void ResetContextMenuRoot(ContextMenu menu)
    {
        if (VisualTreeHelper.GetChildrenCount(menu) > 0
            && VisualTreeHelper.GetChild(menu, 0) is FrameworkElement root)
            PreparePopupOpenVisual(root);
    }

    public static void ConfigurePopupsForWindow(Window window)
        => ConfigurePopupsForElement(window);

    public static void ConfigurePopupsForElement(DependencyObject root)
    {
        ConfigurePopupsInLogicalTree(root);
        if (root is FrameworkElement { IsLoaded: true } element)
            ConfigurePopupsInVisualTree(element);
        else if (root is FrameworkElement framework)
            framework.Loaded += OnPopupDiscoveryLoaded;
    }

    public static void ConfigurePopupsInLogicalTree(DependencyObject root)
    {
        // ComboBox before nested PART_Popup so TryAttach marks the combo; popup skip uses TemplatedParent.
        if (root is ComboBox comboBox)
            AttachComboBoxDropdownAnimation(comboBox);
        else if (root is Popup popup)
            AttachPopupOpenAnimation(popup);
        else if (root is ContextMenu contextMenu)
            AttachContextMenuOpenAnimation(contextMenu);
        else if (root is FrameworkElement framework && framework.ContextMenu is ContextMenu attachedMenu)
            AttachContextMenuOpenAnimation(attachedMenu);

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependency)
                ConfigurePopupsInLogicalTree(dependency);
        }
    }

    public static void ConfigurePopupsInVisualTree(DependencyObject root)
    {
        if (root is ComboBox comboBox)
            AttachComboBoxDropdownAnimation(comboBox);
        else if (root is Popup popup)
            AttachPopupOpenAnimation(popup);
        else if (root is ContextMenu contextMenu)
            AttachContextMenuOpenAnimation(contextMenu);
        else if (root is FrameworkElement framework && framework.ContextMenu is ContextMenu attachedMenu)
            AttachContextMenuOpenAnimation(attachedMenu);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ConfigurePopupsInVisualTree(VisualTreeHelper.GetChild(root, i));
    }

    private static void OnPopupDiscoveryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loaded -= OnPopupDiscoveryLoaded;
        ConfigurePopupsInVisualTree(element);
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not ContextMenu menu)
            return;

        Scriblucent.Behaviors.ItemReorderBehavior.PrepareContextMenu(menu, element);
        AttachContextMenuOpenAnimation(menu);
    }

    private static bool TryAttachPopupMotion(object target)
    {
        if (PopupMotionAttached.TryGetValue(target, out _))
            return false;

        PopupMotionAttached.Add(target, new PopupMotionMarker());
        return true;
    }

    /// <summary>
    /// Briefly shows <paramref name="message"/> on a title-adjacent status TextBlock
    /// (same slot as the Edited indicator), then collapses it and invokes <paramref name="onFinished"/>.
    /// </summary>

    public static void FlashInlineStatus(TextBlock element, string message,
        Action? onFinished = null, int holdMs = 1150)
    {
        element.Text = message;
        FlashInlineStatusCore(element, message, onFinished, holdMs);
    }

    internal static void FlashInlineStatus(Scriblucent.Controls.EditorTitleStatus element, string message,
        Action? onFinished = null, int holdMs = 1150)
    {
        element.Text = message;
        FlashInlineStatusCore(element, message, onFinished, holdMs);
    }

    private static void FlashInlineStatusCore(FrameworkElement element, string message, Action? onFinished, int holdMs)
    {
        CancelInlineStatusFlash(element);
        InlineStatusMotion.SetVisible(element, true, null, true, message);
        var state = new InlineStatusTimer();
        void Complete(bool animate)
        {
            state.Timer.Stop();
            element.Unloaded -= state.Unloaded;
            InlineStatusTimers.Remove(element);
            InlineStatusMotion.SetVisible(element, false, onFinished, animate, message);
        }
        state.Unloaded = (_, _) => Complete(false);
        element.Unloaded += state.Unloaded;
        state.Timer.Interval = TimeSpan.FromMilliseconds(holdMs);
        state.Timer.Tick += (_, _) => Complete(true);
        InlineStatusTimers.Add(element, state);
        state.Timer.Start();
    }

    internal static void CancelInlineStatusFlash(FrameworkElement element)
    {
        if (InlineStatusTimers.TryGetValue(element, out var previous))
        {
            previous.Timer.Stop();
            element.Unloaded -= previous.Unloaded;
            InlineStatusTimers.Remove(element);
        }
    }

    /// <summary>Title status fits its message and releases its space after fading out.</summary>
    public static void SetInlineStatusVisible(TextBlock element, bool visible, Action? onComplete = null, bool animate = true)
        => InlineStatusMotion.SetVisible(element, visible, onComplete, animate, element.Text);

    internal static void SetInlineStatusVisible(Scriblucent.Controls.EditorTitleStatus element, bool visible,
        Action? onComplete = null, bool animate = true)
        => InlineStatusMotion.SetVisible(element, visible, onComplete, animate, element.Text);

    public static void PulseOpacity(
        UIElement element,
        object key,
        double from,
        double to,
        int durationMs = 900)
    {
        if (PrefersReducedMotion)
            return;

        Stop(key);
        Run(
            key,
            new[]
            {
                Track(durationMs / 2, InOutSine, from, to, value => element.Opacity = value),
                Track(durationMs / 2, InOutSine, to, from, value => element.Opacity = value, startDelayMs: durationMs / 2)
            });
    }

    public static TranslateTransform EnsureTranslate(FrameworkElement element)
        => EnsureTransform(element, () => new TranslateTransform());

    public static ScaleTransform EnsureScale(FrameworkElement element, double defaultScale = 1)
        => EnsureTransform(element, () => new ScaleTransform(defaultScale, defaultScale));

    private static T EnsureTransform<T>(FrameworkElement element, Func<T> create) where T : Transform
    {
        if (element.RenderTransform is T standalone && !standalone.IsFrozen) return standalone;
        if (element.RenderTransform is TransformGroup { IsFrozen: false } existing)
        {
            if (existing.Children.OfType<T>().FirstOrDefault(t => !t.IsFrozen) is { } child) return child;
            var added = create(); existing.Children.Add(added); return added;
        }
        var previous = element.RenderTransform;
        var group = new TransformGroup();
        if (previous is not null && !previous.Value.IsIdentity)
            group.Children.Add(previous.IsFrozen ? previous.CloneCurrentValue() : previous);
        var result = create(); group.Children.Add(result); element.RenderTransform = group;
        return result;
    }

    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureScaleTranslateGroup(FrameworkElement element)
    {
        if (element.ReadLocalValue(UIElement.RenderTransformOriginProperty) == DependencyProperty.UnsetValue)
            element.RenderTransformOrigin = new Point(.5, .5);
        var scale = EnsureScale(element);
        var translate = EnsureTranslate(element);
        return (scale, translate);
    }

    public static DropShadowEffect EnsureShadow(FrameworkElement element)
    {
        if (element.Effect is DropShadowEffect existing
            && element.ReadLocalValue(UIElement.EffectProperty) != DependencyProperty.UnsetValue
            && !existing.IsFrozen)
            return existing;

        if (element.Effect is DropShadowEffect frozen)
        {
            var mutable = CloneShadow(frozen);
            element.Effect = mutable;
            return mutable;
        }

        var shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = ShadowOpacityRest,
            BlurRadius = ShadowBlurRest,
            ShadowDepth = 1,
            Direction = 270
        };
        element.Effect = shadow;
        return shadow;
    }

    private static void ResetTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate && !translate.IsFrozen)
            translate.X = translate.Y = 0;
    }

    private static DropShadowEffect CloneShadow(DropShadowEffect source)
        => new()
        {
            Color = source.Color,
            Opacity = source.Opacity,
            BlurRadius = source.BlurRadius,
            ShadowDepth = source.ShadowDepth,
            Direction = source.Direction,
            RenderingBias = source.RenderingBias
        };

    private static IEasingFunction Freeze(IEasingFunction easing)
    {
        if (easing is Freezable freezable)
            freezable.Freeze();

        return easing;
    }
}
