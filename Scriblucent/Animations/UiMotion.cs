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
/// Single entry point for in-app UI motion. All property animations should run through
/// <see cref="Run"/> so updates are driven by <see cref="RenderTransitionClock"/> and stay
/// aligned with the display refresh. Do not use WPF <c>DoubleAnimation</c> / storyboards.
/// </summary>
public static class UiMotion
{
    public const int OpenMs = 220;
    public const int CloseMs = 200;
    public const int DeleteMs = 130;
    public const int NoteDeleteMs = 260;
    public const int NoteAddReflowMs = 280;
    public const int NoteAddEnterMs = 260;
    public const int NoteAddEnterDelayMs = 55;
    public const int NoteAddMs = NoteAddEnterMs;
    public const double NoteEnterScale = 0.94;
    public const int EnterOpacityMs = 250;
    public const int EnterScaleMs = 320;
    public const int HoverInMs = 180;
    public const int HoverOutMs = 200;
    public const int FastMs = 170;
    public const int FlashcardFlipMinMs = 120;
    public const int QuickMs = 150;
    public const int ReflowMs = 270;
    public const int ReflowStaggerMs = 10;
    public const int ReflowStaggerCapMs = 60;
    public const int ReflowStaggerMaxItems = 10;
    public const int BulkAddLayoutHideThreshold = 2;
    public const int MaxBulkAnimatedEnters = 8;
    public const int MaxBulkAnimatedExits = 8;
    public const int NavFadeOutMs = 120;
    public const int SectionMs = 280;
    public const int SidebarMs = 300;
    public const int SidebarResizeMs = 380;
    public const int SearchMs = 300;
    public const int DashboardMs = 420;
    public const int DashboardCrossFadeOutMs = 300;
    public const int DashboardCrossFadeInMs = 380;
    public const int DashboardCrossFadeInDelayMs = 64;
    public const double SubtleLiftY = 6;
    public const double ContentLiftY = 8;
    public const double PanelLiftY = 12;
    public const double WindowLiftY = 28;
    public const int WindowMotionMs = 280;
    public const int WindowCloseMs = 340;
    public const double CardEnterLiftY = 16;
    public const double NavShiftX = 22;
    public const double StudyNavShiftX = 26;
    public const double CrossFadeShiftX = 18;
    public const double CardHoverLiftY = -5.0;
    public const double ChipHoverLiftY = -2.0;
    public const double PressNudgeY = 1.5;
    public const double EnterScale = 0.78;
    public const double ExitScale = 0.66;
    public const double CollapsedScale = 0.92;
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
    private static readonly ConditionalWeakTable<FrameworkElement, MotionPrepState> MotionPrepStates = new();
    private static readonly ConditionalWeakTable<object, PopupMotionMarker> PopupMotionAttached = new();

    private sealed class PopupMotionMarker;

    static UiMotion()
    {
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

    public static bool ShouldSkipDashboardRevealMotion()
    {
        if (PrefersReducedMotion)
            return true;

        return Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel { IsDashboardRevealMotionEnabled: false };
    }

    public static void Stop(object key)
    {
        if (Clocks.TryGetValue(key, out var clock))
            clock.Stop();
    }

    public static void Cleanup(FrameworkElement element)
    {
        Stop(element);
        EndMotionPrep(element);
    }

    public static void Run(object key, IEnumerable<RenderTransitionClock.Track> tracks, Action? onComplete = null)
    {
        var clock = Clocks.GetValue(key, _ => new RenderTransitionClock());
        clock.Run(tracks, onComplete);
    }

    public static void BeginMotionPrep(FrameworkElement element, bool suppressEffect = true)
    {
        var state = MotionPrepStates.GetValue(element, _ => new MotionPrepState());
        if (state.Depth == 0)
        {
            state.PreviousCacheMode = element.CacheMode;
            element.CacheMode = DpiAwareBitmapCache.Create(element, enableClearType: true);

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

        BeginMotionPrep(element, suppressEffect);
        Run(
            element,
            tracks,
            () =>
            {
                EndMotionPrep(element);
                onComplete?.Invoke();
            });
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
        var from = element.Opacity;
        Run(
            element,
            new[]
            {
                Track(
                    durationMs,
                    easing ?? (to >= from ? EaseOutCubic : EaseInCubic),
                    from,
                    to,
                    value => element.Opacity = value,
                    onComplete,
                    startDelayMs)
            });
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
        Border border,
        Thickness from,
        Thickness to,
        int durationMs,
        IEasingFunction? easing = null)
    {
        Clear(border, Border.BorderThicknessProperty);
        Run(
            border,
            new[]
            {
                new RenderTransitionClock.Track(
                    durationMs,
                    easing ?? EaseOutQuad,
                    eased =>
                    {
                        border.BorderThickness = new Thickness(
                            Lerp(from.Left, to.Left, eased),
                            Lerp(from.Top, to.Top, eased),
                            Lerp(from.Right, to.Right, eased),
                            Lerp(from.Bottom, to.Bottom, eased));
                    })
            });
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

    private const string SectionMotionCacheSurfaceName = "CalendarSectionBodySurface";

    public static void AnimateClipReveal(
        FrameworkElement section,
        bool expand,
        int durationMs = SectionMs,
        double slideOffset = 8,
        Action? onComplete = null)
    {
        Stop(section);
        Clear(section, UIElement.OpacityProperty);
        Clear(section, FrameworkElement.MaxHeightProperty);
        section.RenderTransformOrigin = new Point(0.5, 0);
        var translate = EnsureTranslate(section);
        Clear(translate, TranslateTransform.YProperty);

        if (expand)
        {
            if (PrefersReducedMotion)
            {
                section.Visibility = Visibility.Visible;
                section.Opacity = 1;
                translate.Y = 0;
                section.Clip = null;
                section.ClearValue(FrameworkElement.HeightProperty);
                section.MaxHeight = double.PositiveInfinity;
                section.ClipToBounds = false;
                onComplete?.Invoke();
                return;
            }

            section.Visibility = Visibility.Visible;
            section.ClipToBounds = true;
            var (targetHeight, clipWidth) = MeasureExpandedSectionExtent(section);

            section.Height = targetHeight;
            section.MaxHeight = targetHeight;
            var clip = new RectangleGeometry(new Rect(0, 0, clipWidth, 0));
            section.Clip = clip;
            section.Opacity = 0;
            translate.Y = -slideOffset;

            BeginClipMotionCache(section);

            Run(
                section,
                new[]
                {
                    Track(durationMs, OutCubic, 0, targetHeight, value => clip.Rect = new Rect(0, 0, clipWidth, value)),
                    Track(durationMs, OutCubic, -slideOffset, 0, value => translate.Y = value),
                    Track(OpenMs, OutSine, 0, 1, value => section.Opacity = value)
                },
                () =>
                {
                    EndClipMotionCache(section);
                    section.Clip = null;
                    section.ClipToBounds = false;
                    section.Dispatcher.BeginInvoke(() =>
                    {
                        section.ClearValue(FrameworkElement.HeightProperty);
                        section.MaxHeight = double.PositiveInfinity;
                        onComplete?.Invoke();
                    }, DispatcherPriority.Loaded);
                });
            return;
        }

        var startHeight = section.ActualHeight;
        if (startHeight <= 0)
        {
            SetSectionCollapsedState(section, translate, slideOffset);
            onComplete?.Invoke();
            return;
        }

        var collapseWidth = section.ActualWidth > 0 ? section.ActualWidth : 1;
        section.Height = startHeight;
        section.MaxHeight = startHeight;
        section.ClipToBounds = true;
        var collapseClip = new RectangleGeometry(new Rect(0, 0, collapseWidth, startHeight));
        section.Clip = collapseClip;

        var opacityStart = section.Opacity;
        var translateStart = translate.Y;

        BeginClipMotionCache(section);

        Run(
            section,
            new[]
            {
                Track(durationMs, InOutSine, startHeight, 0, value => collapseClip.Rect = new Rect(0, 0, collapseWidth, value)),
                Track(durationMs, InOutSine, translateStart, -slideOffset, value => translate.Y = value),
                Track(HoverInMs, InSine, opacityStart, 0, value => section.Opacity = value)
            },
            () =>
            {
                EndClipMotionCache(section);
                SetSectionCollapsedState(section, translate, slideOffset);
                onComplete?.Invoke();
            });
    }

    private static void BeginClipMotionCache(FrameworkElement section)
    {
        if (section.FindName(SectionMotionCacheSurfaceName) is not FrameworkElement cacheSurface)
            return;

        cacheSurface.UpdateLayout();
        cacheSurface.CacheMode = DpiAwareBitmapCache.Create(cacheSurface, enableClearType: true);
    }

    private static void EndClipMotionCache(FrameworkElement section)
    {
        if (section.FindName(SectionMotionCacheSurfaceName) is FrameworkElement cacheSurface)
            cacheSurface.CacheMode = null;
    }

    public static void ExpandHorizontalPanel(
        FrameworkElement panel,
        bool show,
        double expandedWidth,
        int durationMs = SearchMs,
        Action? onComplete = null)
    {
        Stop(panel);
        Clear(panel, UIElement.OpacityProperty);
        Clear(panel, FrameworkElement.WidthProperty);
        panel.RenderTransformOrigin = new Point(1, 0.5);
        var scale = EnsureScale(panel);

        if (show)
        {
            panel.Visibility = Visibility.Visible;
            panel.Width = expandedWidth;
            panel.Opacity = 0;
            scale.ScaleX = 0.01;
            scale.ScaleY = 1;
        }
        else if (panel.Visibility != Visibility.Visible)
        {
            onComplete?.Invoke();
            return;
        }

        var opacityFrom = panel.Opacity;
        var scaleFrom = scale.ScaleX;
        var easing = show ? EaseOutCubic : EaseInCubic;

        Run(
            panel,
            new[]
            {
                Track(durationMs, easing, opacityFrom, show ? 1.0 : 0.0, value => panel.Opacity = value),
                Track(durationMs, easing, scaleFrom, show ? 1.0 : 0.01, value => scale.ScaleX = value)
            },
            () =>
            {
                if (!show)
                {
                    panel.Visibility = Visibility.Collapsed;
                    panel.IsHitTestVisible = false;
                    panel.Width = 0;
                    panel.Opacity = 0;
                    scale.ScaleX = 0.01;
                }

                onComplete?.Invoke();
            });
    }

    public static void AnimateSidebarChrome(
        FrameworkElement sidebarChrome,
        ColumnDefinition column,
        double fromWidth,
        double targetWidth,
        int durationMs = SidebarResizeMs,
        Action? onComplete = null)
    {
        Stop(SidebarChromeAnimationKey);
        Stop(sidebarChrome);
        Clear(sidebarChrome, UIElement.OpacityProperty);
        sidebarChrome.RenderTransformOrigin = new Point(0, 0.5);
        var translate = EnsureTranslate(sidebarChrome);
        Clear(translate, TranslateTransform.XProperty);
        sidebarChrome.Clip = null;

        if (PrefersReducedMotion || Math.Abs(fromWidth - targetWidth) < 0.5)
        {
            column.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            sidebarChrome.ClearValue(FrameworkElement.WidthProperty);
            sidebarChrome.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
            translate.X = 0;
            sidebarChrome.Opacity = 1;
            SetSidebarAnimationZOrder(sidebarChrome, elevated: false);
            onComplete?.Invoke();
            return;
        }

        var expanding = targetWidth > fromWidth;
        var motionMs = durationMs;
        var translateStart = expanding ? -3.0 : 2.0;
        var opacityStart = expanding ? 0.96 : sidebarChrome.Opacity;

        SetSidebarAnimationZOrder(sidebarChrome, elevated: true);
        translate.X = translateStart;
        sidebarChrome.Opacity = opacityStart;

        column.Width = new GridLength(fromWidth, GridUnitType.Pixel);
        sidebarChrome.ClearValue(FrameworkElement.WidthProperty);
        sidebarChrome.ClearValue(FrameworkElement.HorizontalAlignmentProperty);

        var tracks = new List<RenderTransitionClock.Track>
        {
            Track(motionMs, InOutCubic, fromWidth, targetWidth, value =>
                column.Width = new GridLength(value, GridUnitType.Pixel)),
            Track(motionMs, InOutCubic, translateStart, 0, value => translate.X = value),
            Track(motionMs, OutCubic, opacityStart, 1, value => sidebarChrome.Opacity = value)
        };

        Run(
            SidebarChromeAnimationKey,
            tracks,
            () =>
            {
                column.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                sidebarChrome.Clip = null;
                sidebarChrome.ClearValue(FrameworkElement.WidthProperty);
                sidebarChrome.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                translate.X = 0;
                sidebarChrome.Opacity = 1;
                SetSidebarAnimationZOrder(sidebarChrome, elevated: false);
                onComplete?.Invoke();
            });
    }

    private static void SetSidebarAnimationZOrder(FrameworkElement sidebarChrome, bool elevated)
    {
        if (elevated)
            Panel.SetZIndex(sidebarChrome, 1);
        else
            sidebarChrome.ClearValue(Panel.ZIndexProperty);
    }

    private static readonly object SidebarChromeAnimationKey = new();

    private static (double Height, double Width) MeasureExpandedSectionExtent(FrameworkElement section)
    {
        section.ClearValue(FrameworkElement.HeightProperty);
        section.MaxHeight = double.PositiveInfinity;

        var availableWidth = section.ActualWidth;
        if (availableWidth <= 0 && section.Parent is FrameworkElement parent && parent.ActualWidth > 0)
            availableWidth = parent.ActualWidth;
        if (availableWidth <= 0)
            availableWidth = double.PositiveInfinity;

        section.Measure(new Size(availableWidth, double.PositiveInfinity));
        var desiredHeight = section.DesiredSize.Height;
        var desiredWidth = section.DesiredSize.Width;

        if (!double.IsInfinity(availableWidth) && availableWidth > 0)
        {
            section.Arrange(new Rect(0, 0, availableWidth, Math.Max(desiredHeight, 1)));
            section.UpdateLayout();
        }

        var height = section.ActualHeight > 0 ? section.ActualHeight : desiredHeight;
        var width = section.ActualWidth > 0 ? section.ActualWidth : desiredWidth;
        return (Math.Max(1, height), Math.Max(1, width));
    }

    private static void SetSectionCollapsedState(FrameworkElement section, TranslateTransform translate, double slideOffset)
    {
        section.Visibility = Visibility.Collapsed;
        section.Opacity = 0;
        section.MaxHeight = 0;
        section.ClearValue(FrameworkElement.HeightProperty);
        section.Clip = null;
        translate.Y = -slideOffset;
    }

    public static void SlideFadePanel(
        FrameworkElement element,
        bool show,
        int durationMs = OpenMs,
        double offsetY = PanelLiftY,
        Action? onComplete = null)
    {
        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);

        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 0;
            translate.Y = -offsetY;
        }
        else if (element.Visibility != Visibility.Visible)
        {
            onComplete?.Invoke();
            return;
        }

        var opacityFrom = element.Opacity;
        var opacityTo = show ? 1.0 : 0.0;
        var yFrom = translate.Y;
        var yTo = show ? 0.0 : -offsetY;
        var easing = show ? EaseOutCubic : EaseInCubic;

        Run(
            element,
            new[]
            {
                Track(durationMs, easing, opacityFrom, opacityTo, value => element.Opacity = value),
                Track(durationMs, easing, yFrom, yTo, value => translate.Y = value)
            },
            () =>
            {
                if (!show)
                {
                    element.Visibility = Visibility.Collapsed;
                    element.Opacity = 0;
                    translate.Y = -offsetY;
                }

                onComplete?.Invoke();
            });
    }

    public static void CrossFadePanels(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        int direction,
        int durationMs = EnterScaleMs,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            ResetTranslate(outgoing);
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 1;
            ResetTranslate(incoming);
            onComplete?.Invoke();
            return;
        }

        var offset = CrossFadeShiftX;
        var outShift = direction > 0 ? -offset : direction < 0 ? offset : 0;
        var inStart = direction > 0 ? offset : direction < 0 ? -offset : 0;

        Stop(outgoing);
        Stop(incoming);
        Clear(outgoing, UIElement.OpacityProperty);
        Clear(incoming, UIElement.OpacityProperty);

        outgoing.Visibility = Visibility.Visible;
        incoming.Visibility = Visibility.Visible;
        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        var outTranslate = EnsureTranslate(outgoing);
        var inTranslate = EnsureTranslate(incoming);
        Clear(outTranslate, TranslateTransform.XProperty);
        Clear(inTranslate, TranslateTransform.XProperty);
        outTranslate.X = 0;
        inTranslate.X = inStart;

        Run(
            outgoing,
            new[]
            {
                Track(durationMs, InCubic, 1, 0, value => outgoing.Opacity = value),
                Track(durationMs, InCubic, 0, outShift, value => outTranslate.X = value)
            });

        Run(
            incoming,
            new[]
            {
                Track(durationMs, OutCubic, 0, 1, value => incoming.Opacity = value),
                Track(durationMs, OutCubic, inStart, 0, value => inTranslate.X = value)
            },
            () =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.Opacity = 1;
                ResetTranslate(outgoing);
                onComplete?.Invoke();
            });
    }

    /// <summary>
    /// Cross-fades large dashboard views using bitmap caches so both panels do not re-render every frame.
    /// </summary>
    public static void CrossFadePanelsComposited(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        int direction,
        int durationMs,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            CrossFadePanels(outgoing, incoming, direction, durationMs, onComplete);
            return;
        }

        BeginMotionPrep(outgoing);
        BeginMotionPrep(incoming);

        var offset = CrossFadeShiftX;
        var outShift = direction > 0 ? -offset : direction < 0 ? offset : 0;
        var inStart = direction > 0 ? offset : direction < 0 ? -offset : 0;

        Stop(outgoing);
        Stop(incoming);
        Clear(outgoing, UIElement.OpacityProperty);
        Clear(incoming, UIElement.OpacityProperty);

        outgoing.Visibility = Visibility.Visible;
        incoming.Visibility = Visibility.Visible;
        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        var outTranslate = EnsureTranslate(outgoing);
        var inTranslate = EnsureTranslate(incoming);
        Clear(outTranslate, TranslateTransform.XProperty);
        Clear(inTranslate, TranslateTransform.XProperty);
        outTranslate.X = 0;
        inTranslate.X = inStart;

        void Complete()
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            ResetTranslate(outgoing);
            ResetTranslate(incoming);
            EndMotionPrep(outgoing);
            EndMotionPrep(incoming);
            onComplete?.Invoke();
        }

        Run(
            outgoing,
            new[]
            {
                Track(durationMs, InCubic, 1, 0, value => outgoing.Opacity = value),
                Track(durationMs, InCubic, 0, outShift, value => outTranslate.X = value)
            });

        Run(
            incoming,
            new[]
            {
                Track(durationMs, OutCubic, 0, 1, value => incoming.Opacity = value),
                Track(durationMs, OutCubic, inStart, 0, value => inTranslate.X = value)
            },
            Complete);
    }

    /// <summary>
    /// Opacity cross-dissolve for dashboard tab switches — no horizontal slide (avoids presentation-style motion).
    /// </summary>
    public static void CrossFadeDashboardPanelsComposited(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            outgoing.Visibility = Visibility.Collapsed;
            ResetDashboardPanelMotion(outgoing);
            incoming.Visibility = Visibility.Visible;
            ResetDashboardPanelMotion(incoming);
            onComplete?.Invoke();
            return;
        }

        BeginMotionPrep(outgoing);
        BeginMotionPrep(incoming);

        Stop(outgoing);
        Stop(incoming);
        Clear(outgoing, UIElement.OpacityProperty);
        Clear(incoming, UIElement.OpacityProperty);

        outgoing.Visibility = Visibility.Visible;
        incoming.Visibility = Visibility.Visible;
        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        ResetDashboardPanelMotion(outgoing);
        ResetDashboardPanelMotion(incoming);

        void Complete()
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            ResetDashboardPanelMotion(outgoing);
            ResetDashboardPanelMotion(incoming);

            void FinishCompositor()
            {
                EndMotionPrep(outgoing);
                EndMotionPrep(incoming);
                onComplete?.Invoke();
            }

            incoming.Dispatcher.BeginInvoke(FinishCompositor, DispatcherPriority.Loaded);
        }

        Run(
            outgoing,
            new[]
            {
                Track(DashboardCrossFadeOutMs, InSine, 1, 0, value => outgoing.Opacity = value)
            });

        Run(
            incoming,
            new[]
            {
                Track(
                    DashboardCrossFadeInMs,
                    OutSine,
                    0,
                    1,
                    value => incoming.Opacity = value,
                    onComplete: Complete,
                    startDelayMs: DashboardCrossFadeInDelayMs)
            });
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

    public const int StaggerEnterStepMs = 140;
    public const double StaggerEnterScale = 0.9;
    public const int EmptyDashboardPanelMs = 280;
    public const int EmptyDashboardStaggerStepMs = 70;
    public const double EmptyDashboardEnterLiftY = 10;
    public const double EmptyDashboardEnterScale = 0.96;
    public const int ContentHostEnterMs = 380;
    public const double ContentHostEnterLiftY = 12;
    public const int GroupEnterMs = 380;
    public const int GroupExitMs = 300;
    public const double GroupEnterLiftY = 12;
    public const double GroupEnterScale = 0.94;
    public const double GroupExitScale = 0.9;

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
            var delayMs = index * staggerMs;
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

            Run(
                element,
                new[]
                {
                    Track(durationMs, OutCubic, 0, 1, value => element.Opacity = value, startDelayMs: delayMs),
                    Track(durationMs, OutBack, offsetY, 0, value => translate.Y = value, startDelayMs: delayMs),
                    Track(
                        durationMs,
                        OutBack,
                        enterScale,
                        1,
                        value =>
                        {
                            scale.ScaleX = value;
                            scale.ScaleY = value;
                        },
                        onComplete: MarkOneComplete,
                        startDelayMs: delayMs)
                });
        }
    }

    public static void AnimateEmptyDashboardEnter(FrameworkElement panel, Action? onComplete = null)
    {
        var container = ResolveEmptyDashboardMotionRoot(panel);
        if (container is null)
        {
            AnimateContentEnter(panel, EmptyDashboardPanelMs, EmptyDashboardEnterLiftY, onComplete);
            return;
        }

        var elements = CollectEmptyDashboardMotionChildren(container);
        if (elements.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        Stop(panel);
        panel.Opacity = 1;
        ResetTranslate(panel);

        AnimateStaggeredContentEnter(
            elements,
            durationMs: EmptyDashboardPanelMs,
            offsetY: EmptyDashboardEnterLiftY,
            staggerMs: EmptyDashboardStaggerStepMs,
            onComplete: onComplete,
            enterScale: EmptyDashboardEnterScale);
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
                Track(ContentHostEnterMs, OutCubic, 0, 1, value => element.Opacity = value, startDelayMs: startDelayMs),
                Track(
                    ContentHostEnterMs,
                    OutCubic,
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
        FrameworkElement element,
        int durationMs = SectionMs,
        double offsetY = PanelLiftY,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            element.Opacity = 1;
            ResetTranslate(element);
            onComplete?.Invoke();
            return;
        }

        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);

        element.Opacity = 0;
        translate.Y = offsetY;

        Run(
            element,
            new[]
            {
                Track(durationMs, OutCubic, 0, 1, value => element.Opacity = value),
                Track(durationMs, OutCubic, offsetY, 0, value => translate.Y = value)
            },
            onComplete);
    }

    public static void AnimateContentExit(
        FrameworkElement element,
        int durationMs = CloseMs,
        double offsetY = PanelLiftY,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            element.Opacity = 0;
            ResetTranslate(element);
            onComplete?.Invoke();
            return;
        }

        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);

        var opacityStart = element.Opacity;
        if (opacityStart <= 0)
            opacityStart = 1;

        var offsetStart = translate.Y;

        Run(
            element,
            new[]
            {
                Track(durationMs, InCubic, opacityStart, 0, value => element.Opacity = value),
                Track(durationMs, InCubic, offsetStart, offsetY, value => translate.Y = value)
            },
            onComplete);
    }

    public static int ResolveWindowEnterDurationMs(Window? window)
    {
        if (PrefersReducedMotion)
            return 0;

        if (window?.Owner is { WindowState: WindowState.Maximized })
            return Math.Min(WindowMotionMs, 200);

        return WindowMotionMs;
    }

    public static double ResolveWindowEnterLiftY(Window? window)
    {
        if (window?.Owner is { WindowState: WindowState.Maximized })
            return Math.Min(WindowLiftY, 18);

        return WindowLiftY;
    }

    /// <summary>
    /// Window open motion: live translate (no bitmap cache) so real content stays visible during rise-up.
    /// </summary>
    public static void AnimateWindowContentEnter(
        FrameworkElement element,
        int durationMs = WindowMotionMs,
        double offsetY = WindowLiftY,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            element.Opacity = 1;
            ResetTranslate(element);
            onComplete?.Invoke();
            return;
        }

        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);

        element.Opacity = 1;
        translate.Y = offsetY;

        Run(
            element,
            new[]
            {
                Track(durationMs, OutCubic, offsetY, 0, value => translate.Y = value)
            },
            onComplete);
    }

    /// <summary>
    /// Window close motion: live sink-down (no bitmap cache) so tabs/text stay visible.
    /// </summary>
    public static void AnimateWindowContentExit(
        FrameworkElement element,
        int durationMs = WindowCloseMs,
        double offsetY = WindowLiftY,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            element.Opacity = 1;
            ResetTranslate(element);
            onComplete?.Invoke();
            return;
        }

        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var translate = EnsureTranslate(element);
        Clear(translate, TranslateTransform.YProperty);

        element.Opacity = 1;
        translate.Y = 0;

        Run(
            element,
            new[]
            {
                Track(durationMs, InCubic, 0, offsetY, value => translate.Y = value)
            },
            onComplete);
    }

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

    public static void AnimatePopupOpen(FrameworkElement root, int durationMs = PopupOpenMs)
    {
        try
        {
            var surface = ResolvePopupMotionSurface(root);
            root.Opacity = 1;

            if (PrefersReducedMotion || durationMs <= 0)
            {
                surface.Opacity = 1;
                return;
            }

            StopPopupOpacityAnimation(surface);
            surface.Opacity = 0;
            BeginMotionPrep(surface, suppressEffect: false);

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = OutCubic,
                FillBehavior = FillBehavior.Stop
            };

            EventHandler? completed = null;
            completed = (_, _) =>
            {
                animation.Completed -= completed;
                try
                {
                    surface.BeginAnimation(UIElement.OpacityProperty, null);
                    surface.Opacity = 1;
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    EndMotionPrep(surface);
                }
            };
            animation.Completed += completed;
            surface.BeginAnimation(UIElement.OpacityProperty, animation);
        }
        catch
        {
            try { ResolvePopupMotionSurface(root).Opacity = 1; } catch { /* ignore */ }
        }
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

                    AnimatePopupOpen(root, durationMs);
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

        AttachContextMenuOpenAnimation(menu);
    }

    private static bool TryAttachPopupMotion(object target)
    {
        if (PopupMotionAttached.TryGetValue(target, out _))
            return false;

        PopupMotionAttached.Add(target, new PopupMotionMarker());
        return true;
    }

    public static void AnimateInlineReveal(
        FrameworkElement element,
        bool show,
        Action? onComplete = null)
    {
        if (PrefersReducedMotion)
        {
            if (show)
                element.Visibility = Visibility.Visible;
            else
                element.Visibility = Visibility.Collapsed;

            element.Opacity = 1;
            ResetTranslate(element);
            onComplete?.Invoke();
            return;
        }

        if (show)
        {
            if (element.Visibility == Visibility.Visible && element.Opacity > 0.95)
            {
                onComplete?.Invoke();
                return;
            }

            SlideFadePanel(element, true, QuickMs, SubtleLiftY, onComplete);
            return;
        }

        if (element.Visibility != Visibility.Visible)
        {
            onComplete?.Invoke();
            return;
        }

        SlideFadePanel(element, false, NavFadeOutMs, SubtleLiftY, onComplete);
    }

    /// <summary>
    /// Briefly shows <paramref name="message"/> on a title-adjacent status TextBlock
    /// (same slot as the Edited indicator), then fades out and invokes <paramref name="onFinished"/>.
    /// </summary>
    public static void FlashInlineStatus(
        TextBlock element,
        string message,
        Action? onFinished = null,
        int holdMs = 1150,
        int fadeMs = 800)
    {
        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        element.Text = message;

        if (PrefersReducedMotion)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 1;
            ResetTranslate(element);

            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
            delay.Tick += (_, _) =>
            {
                delay.Stop();
                element.Visibility = Visibility.Collapsed;
                onFinished?.Invoke();
            };
            delay.Start();
            return;
        }

        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        ResetTranslate(element);
        Fade(element, 1, OpenMs, OutQuad);

        Run(
            element,
            new[]
            {
                Track(
                    fadeMs,
                    OutQuad,
                    1,
                    0,
                    value => element.Opacity = value,
                    () =>
                    {
                        element.Visibility = Visibility.Collapsed;
                        element.Opacity = 1;
                        ResetTranslate(element);
                        onFinished?.Invoke();
                    },
                    startDelayMs: holdMs)
            });
    }

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
                Track(durationMs / 2, InOutSine, to, from, value => element.Opacity = value)
            });
    }

    public static void ScaleFadePanel(
        FrameworkElement element,
        bool show,
        int durationMs = FastMs,
        double collapsedScale = CollapsedScale,
        Action? onComplete = null)
    {
        Stop(element);
        Clear(element, UIElement.OpacityProperty);
        var scale = EnsureScale(element, collapsedScale);

        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 0;
            scale.ScaleX = collapsedScale;
            scale.ScaleY = collapsedScale;
        }
        else if (element.Visibility != Visibility.Visible)
        {
            onComplete?.Invoke();
            return;
        }

        var opacityFrom = element.Opacity;
        var scaleFrom = scale.ScaleX;
        var easing = EaseOutCubic;

        Run(
            element,
            new[]
            {
                Track(durationMs, easing, opacityFrom, show ? 1.0 : 0.0, value => element.Opacity = value),
                Track(durationMs, easing, scaleFrom, show ? 1.0 : collapsedScale, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                })
            },
            () =>
            {
                if (!show)
                {
                    element.Visibility = Visibility.Collapsed;
                    element.Opacity = 0;
                    scale.ScaleX = collapsedScale;
                    scale.ScaleY = collapsedScale;
                }

                onComplete?.Invoke();
            });
    }

    public static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate && !translate.IsFrozen)
            return translate;

        var x = 0.0;
        var y = 0.0;
        if (element.RenderTransform is TranslateTransform frozenTranslate)
        {
            x = frozenTranslate.X;
            y = frozenTranslate.Y;
        }

        translate = new TranslateTransform(x, y);
        element.RenderTransform = translate;
        return translate;
    }

    public static ScaleTransform EnsureScale(FrameworkElement element, double defaultScale = 1)
    {
        if (element.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
            return scale;

        var scaleX = defaultScale;
        var scaleY = defaultScale;
        if (element.RenderTransform is ScaleTransform frozenScale)
        {
            scaleX = frozenScale.ScaleX;
            scaleY = frozenScale.ScaleY;
        }

        scale = new ScaleTransform(scaleX, scaleY);
        element.RenderTransform = scale;
        return scale;
    }

    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureScaleTranslateGroup(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            var existingScale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var existingTranslate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existingScale is not null && !existingScale.IsFrozen
                && existingTranslate is not null && !existingTranslate.IsFrozen)
                return (existingScale, existingTranslate);
        }

        var scaleX = 1.0;
        var scaleY = 1.0;
        var translateX = 0.0;
        var translateY = 0.0;

        if (element.RenderTransform is TransformGroup existingGroup)
        {
            if (existingGroup.Children.OfType<ScaleTransform>().FirstOrDefault() is ScaleTransform existingScale)
            {
                scaleX = existingScale.ScaleX;
                scaleY = existingScale.ScaleY;
            }

            if (existingGroup.Children.OfType<TranslateTransform>().FirstOrDefault() is TranslateTransform existingTranslate)
            {
                translateX = existingTranslate.X;
                translateY = existingTranslate.Y;
            }
        }
        else if (element.RenderTransform is ScaleTransform loneScale)
        {
            scaleX = loneScale.ScaleX;
            scaleY = loneScale.ScaleY;
        }

        var scale = new ScaleTransform(scaleX, scaleY);
        var translate = new TranslateTransform(translateX, translateY);
        var created = new TransformGroup();
        created.Children.Add(scale);
        created.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = created;
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