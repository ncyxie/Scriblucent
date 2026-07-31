using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Services;

namespace Scriblucent.Animations;

/// <summary>
/// Modal overlay transitions driven by a vsync-aligned render clock. The blurred scrim is composited at
/// capture resolution and scaled up once, keeping dim fades cheap even on large/fullscreen windows.
/// </summary>
public sealed class OverlayPanelAnimator
{
    public const int DimOpenMs = 220;
    public const int DimCloseMs = 200;
    public const int PanelOpenMs = 220;
    public const int PanelCloseMs = 200;
    public const double PanelOffsetY = 12;
    public const int PanelOpenDelayMs = 40;

    private static readonly IEasingFunction OpenEase = UiMotion.OutCubic;
    private static readonly IEasingFunction CloseEase = UiMotion.InCubic;
    private static readonly IEasingFunction DimOpenEase = UiMotion.OutSine;
    private static readonly IEasingFunction DimCloseEase = UiMotion.InSine;

    private readonly FrameworkElement _host;
    private readonly UIElement? _dimLayer;
    private readonly Image? _backdropImage;
    private readonly FrameworkElement _motionHost;
    private readonly FrameworkElement _scrimHost;
    private readonly RenderTransitionClock _clock = new();
    private bool _isClosing;
    private bool _backdropRefreshScheduled;
    private Effect? _savedPanelEffect;
    private FrameworkElement? _panelCard;
    private Border? _frameHairline;
    private Window? _frameHairlineWindow;

    private OverlayPanelAnimator(
        FrameworkElement host,
        UIElement? dimLayer,
        FrameworkElement motionHost,
        Image? backdropImage,
        FrameworkElement scrimHost)
    {
        _host = host;
        _dimLayer = dimLayer;
        _motionHost = motionHost;
        _backdropImage = backdropImage;
        _scrimHost = scrimHost;
    }

    public static OverlayPanelAnimator Bind(
        FrameworkElement host,
        UIElement? dimLayer,
        FrameworkElement motionHost,
        Image? backdropImage,
        FrameworkElement scrimHost)
        => new(host, dimLayer, motionHost, backdropImage, scrimHost);

    public bool IsClosing => _isClosing;

    public void Show(Action? afterShown = null, Func<BitmapSource?>? captureBackdrop = null)
    {
        _isClosing = false;
        _clock.Stop();
        PrepareOpenState();

        BitmapSource? snapshot = null;
        try
        {
            snapshot = captureBackdrop?.Invoke();
        }
        catch
        {
            snapshot = null;
        }

        ConfigureScrim(snapshot);
        InstallBackdrop(snapshot);

        if (snapshot != null)
            OverlayBackgroundSuspender.TrySuspend(_host);

        EnsureOverlayFrameHairline();
        _host.Visibility = Visibility.Visible;
        _host.IsHitTestVisible = true;

        if (ReducedMotionPreferences.ShouldUseInstantOverlayTransitions)
        {
            _host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _host.UpdateLayout();
                ShowImmediately(afterShown);
            });
            return;
        }

        BeginPanelMotionPrep();

        _host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _host.UpdateLayout();
            RunOpenTransition(afterShown);
        });
    }

    public void Hide(Action? afterHidden = null)
    {
        if (_isClosing || _host.Visibility != Visibility.Visible)
            return;

        _isClosing = true;
        _host.IsHitTestVisible = false;
        _clock.Stop();

        if (ReducedMotionPreferences.ShouldUseInstantOverlayTransitions)
        {
            HideImmediately(afterHidden);
            return;
        }

        BeginPanelMotionPrep();
        RunCloseTransition(afterHidden);
    }

    public void RunAfterFirstRender(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        _host.Dispatcher.BeginInvoke(priority, action);
    }

    /// <summary>
    /// Re-captures the blurred scrim after the window content changes (e.g. theme switch while open).
    /// </summary>
    public void RefreshBackdrop(Func<BitmapSource?>? captureBackdrop = null)
    {
        if (_isClosing || _host.Visibility != Visibility.Visible || _backdropImage?.Source == null)
            return;

        if (_backdropRefreshScheduled)
            return;

        _backdropRefreshScheduled = true;
        captureBackdrop ??= () => OverlayBackdropCapture.TryCaptureBehindOverlays(Window.GetWindow(_host));

        _host.Dispatcher.BeginInvoke(
            () =>
            {
                if (Window.GetWindow(_host) is { } window)
                    window.UpdateLayout();

                _host.Dispatcher.BeginInvoke(
                    () =>
                    {
                        _backdropRefreshScheduled = false;
                        RefreshBackdropCore(captureBackdrop);
                    },
                    DispatcherPriority.Render);
            },
            DispatcherPriority.Loaded);
    }

    private void RefreshBackdropCore(Func<BitmapSource?> captureBackdrop)
    {
        if (_isClosing || _host.Visibility != Visibility.Visible || _backdropImage?.Source == null)
            return;

        OverlayBackgroundSuspender.TryResume();

        BitmapSource? snapshot = null;

        try
        {
            if (Window.GetWindow(_host) is { } window)
            {
                window.UpdateLayout();
                snapshot = captureBackdrop();
            }
        }
        catch
        {
            snapshot = null;
        }

        if (snapshot != null)
        {
            ConfigureScrim(snapshot);
            InstallBackdrop(snapshot);
        }

        OverlayBackgroundSuspender.TrySuspend(_host);
        EnsureOverlayFrameHairline();
    }

    private void ShowImmediately(Action? afterShown)
    {
        EnsureMotionTranslate().Y = 0;
        SetDimOpacity(1);
        afterShown?.Invoke();
    }

    private void HideImmediately(Action? afterHidden)
    {
        EnsureMotionTranslate().Y = PanelOffsetY;
        SetDimOpacity(0);
        _host.Visibility = Visibility.Collapsed;
        PrepareClosedState();
        ClearBackdrop();
        HideOverlayFrameHairline();
        OverlayBackgroundSuspender.TryResume();
        _isClosing = false;
        afterHidden?.Invoke();
    }

    private void RunOpenTransition(Action? afterShown)
    {
        var translate = EnsureMotionTranslate();
        translate.Y = PanelOffsetY;
        SetDimOpacity(0);

        _clock.Run(
            new[]
            {
                new RenderTransitionClock.Track(
                    DimOpenMs,
                    DimOpenEase,
                    eased => SetDimOpacity(eased)),
                new RenderTransitionClock.Track(
                    PanelOpenMs,
                    OpenEase,
                    eased => translate.Y = PanelOffsetY * (1.0 - eased),
                    () => translate.Y = 0,
                    PanelOpenDelayMs)
            },
            () =>
            {
                EndPanelMotionPrep();
                afterShown?.Invoke();
            },
            instantOnlyForSystemReducedMotion: true);
    }

    private void RunCloseTransition(Action? afterHidden)
    {
        var translate = EnsureMotionTranslate();
        var dimStart = _dimLayer is UIElement dim && dim.Opacity > 0 ? dim.Opacity : 1;

        _clock.Run(
            new[]
            {
                new RenderTransitionClock.Track(
                    DimCloseMs,
                    DimCloseEase,
                    eased => SetDimOpacity(dimStart * (1.0 - eased)),
                    () => SetDimOpacity(0)),
                new RenderTransitionClock.Track(
                    PanelCloseMs,
                    CloseEase,
                    eased => translate.Y = PanelOffsetY * eased,
                    () => translate.Y = PanelOffsetY)
            },
            () =>
            {
                _host.Visibility = Visibility.Collapsed;
                PrepareClosedState();
                ClearBackdrop();
                HideOverlayFrameHairline();
                EndPanelMotionPrep();
                OverlayBackgroundSuspender.TryResume();
                _isClosing = false;
                afterHidden?.Invoke();
            },
            instantOnlyForSystemReducedMotion: true);
    }

    private void EnsureOverlayFrameHairline()
    {
        var panel = ResolveHairlinePanel();
        if (panel is null)
            return;

        if (_frameHairline is null)
        {
            _frameHairline = new Border();
            Panel.SetZIndex(_frameHairline, 10_000);
            panel.Children.Add(_frameHairline);
        }
        else if (!ReferenceEquals(_frameHairline.Parent, panel))
        {
            if (_frameHairline.Parent is Panel oldParent)
                oldParent.Children.Remove(_frameHairline);
            Panel.SetZIndex(_frameHairline, 10_000);
            panel.Children.Add(_frameHairline);
        }

        AttachFrameHairlineWindow();
        RefreshOverlayFrameHairline();
        _frameHairline.Visibility = Visibility.Visible;
    }

    private void RefreshOverlayFrameHairline()
    {
        if (_frameHairline is null)
            return;

        var isDarkMode = string.Equals(ThemeManager.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase);
        // Overlay dialogs are separate HWNDs over an inactive owner — L/R/B are the owner's frame.
        var frameWindow = _frameHairlineWindow?.Owner ?? _frameHairlineWindow;
        var frameActive = frameWindow?.IsActive ?? true;
        ShellWindowChrome.ApplyOverlayFrameHairline(_frameHairline, isDarkMode, frameActive, frameWindow);
    }

    private void AttachFrameHairlineWindow()
    {
        var window = Window.GetWindow(_host);
        if (ReferenceEquals(_frameHairlineWindow, window))
            return;

        DetachFrameHairlineWindow();
        _frameHairlineWindow = window;
        if (window is null)
            return;

        window.Activated += OnFrameHairlineActivationChanged;
        window.Deactivated += OnFrameHairlineActivationChanged;
        window.StateChanged += OnFrameHairlineActivationChanged;
        if (window.Owner is { } owner)
        {
            owner.Activated += OnFrameHairlineActivationChanged;
            owner.Deactivated += OnFrameHairlineActivationChanged;
            owner.StateChanged += OnFrameHairlineActivationChanged;
        }
    }

    private void DetachFrameHairlineWindow()
    {
        if (_frameHairlineWindow is null)
            return;

        _frameHairlineWindow.Activated -= OnFrameHairlineActivationChanged;
        _frameHairlineWindow.Deactivated -= OnFrameHairlineActivationChanged;
        _frameHairlineWindow.StateChanged -= OnFrameHairlineActivationChanged;
        if (_frameHairlineWindow.Owner is { } owner)
        {
            owner.Activated -= OnFrameHairlineActivationChanged;
            owner.Deactivated -= OnFrameHairlineActivationChanged;
            owner.StateChanged -= OnFrameHairlineActivationChanged;
        }

        _frameHairlineWindow = null;
    }

    private void OnFrameHairlineActivationChanged(object? sender, EventArgs e)
        => RefreshOverlayFrameHairline();

    private Panel? ResolveHairlinePanel()
    {
        if (_host is Panel hostPanel)
            return hostPanel;

        // Settings/About/Schedule bind the UserControl as host; the named OverlayRoot is Content.
        if (_host is ContentControl { Content: Panel contentPanel })
            return contentPanel;

        return _scrimHost.Parent as Panel;
    }

    private void HideOverlayFrameHairline()
    {
        DetachFrameHairlineWindow();
        if (_frameHairline is not null)
            _frameHairline.Visibility = Visibility.Collapsed;
    }

    private void PrepareOpenState()
    {
        _motionHost.Opacity = 1;
        EnsureMotionTranslate().Y = PanelOffsetY;
        SetDimOpacity(0);
    }

    private void PrepareClosedState()
    {
        _motionHost.Opacity = 1;
        EnsureMotionTranslate().Y = PanelOffsetY;
        SetDimOpacity(0);
    }

    private void ConfigureScrim(BitmapSource? snapshot)
    {
        // Scrim fills the overlay host 1:1 — never pin Width/Height to bitmap pixels
        // (that forced Viewbox Stretch=Fill to zoom/shift the backdrop).
        _scrimHost.ClearValue(FrameworkElement.WidthProperty);
        _scrimHost.ClearValue(FrameworkElement.HeightProperty);
        _scrimHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scrimHost.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void InstallBackdrop(BitmapSource? snapshot)
    {
        if (_backdropImage == null)
            return;

        if (snapshot == null)
        {
            ClearBackdrop();
            return;
        }

        _backdropImage.Source = snapshot;
        _backdropImage.Visibility = Visibility.Visible;
        _backdropImage.Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(_backdropImage, BitmapScalingMode.Linear);
        _backdropImage.CacheMode = DpiAwareBitmapCache.Create(_backdropImage, enableClearType: false);
    }

    private void ClearBackdrop()
    {
        if (_backdropImage == null)
            return;

        _backdropImage.CacheMode = null;
        _backdropImage.Source = null;
        _backdropImage.Visibility = Visibility.Collapsed;
    }

    private void BeginPanelMotionPrep()
    {
        _panelCard = _motionHost.FindName("PanelCard") as FrameworkElement
            ?? _motionHost.FindName("DialogCard") as FrameworkElement;
        if (_panelCard?.Effect != null)
        {
            _savedPanelEffect = _panelCard.Effect;
            _panelCard.Effect = null;
        }

        _motionHost.CacheMode = DpiAwareBitmapCache.Create(_motionHost, enableClearType: true);
    }

    private void EndPanelMotionPrep()
    {
        _motionHost.CacheMode = null;

        if (_panelCard != null)
            _panelCard.Effect = _savedPanelEffect;

        _savedPanelEffect = null;
        _panelCard = null;
    }

    private void SetDimOpacity(double opacity)
    {
        if (_dimLayer != null)
            _dimLayer.Opacity = opacity;
    }

    private TranslateTransform EnsureMotionTranslate()
    {
        _motionHost.RenderTransformOrigin = new Point(0.5, 1);
        var translate = UiMotion.EnsureTranslate(_motionHost);
        if (Math.Abs(translate.Y) < 0.001)
            translate.Y = PanelOffsetY;

        return translate;
    }
}