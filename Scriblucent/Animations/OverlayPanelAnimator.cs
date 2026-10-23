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
/// Modal overlay transitions use short fades and a small opening lift. The blurred scrim is composited at
/// capture resolution and scaled up once, keeping dim fades cheap even on large/fullscreen windows.
/// </summary>
public sealed class OverlayPanelAnimator
{
    public const int DimOpenMs = 240;
    public const int DimCloseMs = 140;
    public const int PanelOpenMs = 320;
    public const int PanelCloseMs = 140;
    public const double PanelOffsetY = 18;

    private static readonly IEasingFunction OpenEase = UiMotion.InOutSine;
    private static readonly IEasingFunction CloseEase = UiMotion.InQuad;
    private static readonly IEasingFunction DimOpenEase = UiMotion.OutSine;
    private static readonly IEasingFunction DimCloseEase = UiMotion.InSine;

    private readonly FrameworkElement _host;
    private readonly UIElement? _dimLayer;
    private readonly Image? _backdropImage;
    private readonly FrameworkElement _motionHost;
    private readonly FrameworkElement _scrimHost;
    private bool _isClosing;
    private bool _backgroundSuspended;
    private int _transitionVersion;
    private int _backdropVersion;
    private bool _entryStarted;
    private Action? _pendingEntrance;
    private Action? _finishEntrancePreparation;
    private TaskCompletionSource? _entrySettled;
    private IDisposable? _backdropRequest;
    private Window? _backdropOwner;
    private Effect? _savedPanelEffect;
    private FrameworkElement? _panelCard;

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
        _host.Unloaded += (_, _) =>
        {
            _transitionVersion++;
            _pendingEntrance = null;
            FinishEntrancePreparation();
            StopSurfaceMotion();
            CancelBackdropRequest();
            EndPanelMotionPrep();
            ClearBackdrop();
            ResumeBackground();
            _isClosing = false;
            _entrySettled?.TrySetResult();
        };
    }

    public static OverlayPanelAnimator Bind(
        FrameworkElement host,
        UIElement? dimLayer,
        FrameworkElement motionHost,
        Image? backdropImage,
        FrameworkElement scrimHost)
        => new(host, dimLayer, motionHost, backdropImage, scrimHost);

    public bool IsClosing => _isClosing;
    internal Func<bool>? AdditionalEntranceRunning { get; set; }

    // captureBackdrop is retained for source compatibility; owner leases perform all capture.
    public void Show(Action? afterShown = null, Func<BitmapSource?>? captureBackdrop = null,
        Window? backdropOwner = null, Action? onEntranceStarted = null)
        => ShowCore(afterShown, backdropOwner, onEntranceStarted, null);

    internal void Show(OverlayEntranceBatch entrance, Action? afterShown = null, Window? backdropOwner = null)
        => ShowCore(afterShown, backdropOwner, null, entrance);

    private void ShowCore(Action? afterShown, Window? backdropOwner, Action? onEntranceStarted,
        OverlayEntranceBatch? entrance)
    {
        var version = ++_transitionVersion;
        FinishEntrancePreparation();
        var reversing = _isClosing && _host.Visibility == Visibility.Visible;
        _isClosing = false;
        StopSurfaceMotion();
        _entrySettled?.TrySetResult();
        _entrySettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _entryStarted = false;
        if (!reversing)
            PrepareOpenState();

        _host.Visibility = Visibility.Visible;
        _host.IsHitTestVisible = true;
        _backdropOwner = backdropOwner ?? Window.GetWindow(_host);
        ConfigureScrim(null);
        BeginPanelMotionPrep();
        _motionHost.IsHitTestVisible = reversing;
        BitmapSource? stagedBackdrop = null;
        bool backdropStaged = false;
        void Start()
        {
            if (version != _transitionVersion || _isClosing || _entryStarted || _host.Visibility != Visibility.Visible) return;
            _pendingEntrance = null;
            _entryStarted = true;
            FinishEntrancePreparation();
            _motionHost.IsHitTestVisible = true;
            // Off still prepares the material asynchronously, but presents it on the
            // same tick as the dialog rather than adding blur after the dialog is shown.
            var instant = ReducedMotionPreferences.ShouldUseInstantOverlayTransitions;
            if (instant && backdropStaged) ApplyBackdrop(stagedBackdrop);
            ArrivalGroups.Start(_motionHost, fresh: !reversing);
            onEntranceStarted?.Invoke();
            if (instant) ShowImmediately(afterShown);
            else RunOpenTransition(afterShown);
            if (!instant && backdropStaged) ApplyBackdrop(stagedBackdrop);
            if (reversing && OverlayBackdropCapture.TryCaptureForDialog(_backdropOwner) is { } existing)
                ApplyBackdrop(existing);
        }
        _pendingEntrance = Start;
        var ready = !reversing ? entrance?.Register(Start) : null;
        _finishEntrancePreparation = ready;
        _host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != _transitionVersion || _isClosing) return;
            _host.UpdateLayout();
            if (reversing)
            {
                if (_pendingEntrance is not null) Start();
                if (_backdropRequest is null) BeginBackdropRequest();
            }
            else
            {
                if (ready is null)
                {
                    // Animated entrances can overlap worker blur processing. Off presents
                    // the complete material and foreground together once processing finishes.
                    if (!ReducedMotionPreferences.ShouldUseInstantOverlayTransitions)
                        SetDimOpacity(.12);
                    BeginBackdropRequest(() =>
                    {
                        if (!ReducedMotionPreferences.ShouldUseInstantOverlayTransitions) Start();
                    }, snapshot =>
                    {
                        if (_entryStarted) ApplyBackdrop(snapshot);
                        else
                        {
                            stagedBackdrop = snapshot;
                            backdropStaged = true;
                            Start();
                        }
                    });
                }
                else
                {
                    // A multi-window modal is one transition. Stage its textures without
                    // starting a fade while another window is still being rasterized.
                    BeginBackdropRequest(received: snapshot =>
                    {
                        if (_entryStarted)
                        {
                            ApplyBackdrop(snapshot);
                            FinishEntrancePreparation();
                            ready();
                            return;
                        }
                        stagedBackdrop = snapshot;
                        backdropStaged = true;
                        InstallBackdrop(snapshot);
                        if (_backdropImage is { } image) image.Opacity = 0;
                        _finishEntrancePreparation = null;
                        ready();
                    });
                }
            }
        });
    }

    public void Hide(Action? afterHidden = null)
    {
        if (_isClosing || _host.Visibility != Visibility.Visible)
            return;

        _isClosing = true;
        _transitionVersion++;
        _pendingEntrance = null;
        FinishEntrancePreparation();
        _host.IsHitTestVisible = false;
        StopSurfaceMotion();

        if (!_entryStarted || ReducedMotionPreferences.ShouldUseInstantOverlayTransitions)
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
        if (_isClosing || _host.Visibility != Visibility.Visible) return;
        ResumeBackground();
        if (_backdropOwner is { } owner) OverlayBackdropCapture.Invalidate(owner);
        BeginBackdropRequest(_pendingEntrance);
    }

    private void CancelBackdropRequest()
    {
        _backdropVersion++;
        _backdropRequest?.Dispose(); _backdropRequest = null;
    }

    private void BeginBackdropRequest(Action? prepared = null, Action<BitmapSource?>? received = null)
    {
        CancelBackdropRequest();
        var revision = _backdropVersion;
        _backdropRequest = OverlayBackdropCapture.RequestPrepared(_backdropOwner, snapshot =>
        {
            if (revision != _backdropVersion || _isClosing || _host.Visibility != Visibility.Visible) return;
            if (received is not null) received(snapshot);
            else ApplyBackdrop(snapshot);
        }, prepared ?? (() => { }), WaitForEntrance, deferInitial: prepared is null && received is null);
    }

    private void FinishEntrancePreparation()
    {
        var finish = _finishEntrancePreparation;
        _finishEntrancePreparation = null;
        finish?.Invoke();
    }

    private async Task WaitForEntrance()
    {
        // A resize can restart a staged capture before the batch releases its clocks.
        // That preparation must not wait for the entrance it is responsible for starting.
        while (_entryStarted && _entrySettled is { Task.IsCompleted: false } pending) await pending.Task;
        // A requested refresh may wait for showcase layers too; there is no resting poll/subscription.
        while (_host.IsLoaded && _host.IsVisible && AdditionalEntranceRunning?.Invoke() == true)
            await Task.Delay(16);
    }

    private void ApplyBackdrop(BitmapSource? snapshot)
    {
        if (snapshot is null) { ClearBackdrop(); ResumeBackground(); return; }
        var first = _backdropImage?.Source is null;
        InstallBackdrop(snapshot);
        if (_backdropImage is not { } image) return;
        var version = _transitionVersion;
        void Suspend()
        {
            if (version != _transitionVersion || _isClosing || _backgroundSuspended) return;
            OverlayBackgroundSuspender.TrySuspend(_host);
            _backgroundSuspended = OverlayBackgroundSuspender.IsSuspended(Window.GetWindow(_host));
        }
        if (MotionPolicy.Spatial && (first || image.Opacity < 1))
        {
            if (first) image.Opacity = 0;
            SurfaceMotion.Animate(image, 1, 160, UiMotion.OutSine, false, Suspend);
        }
        else { image.Opacity = 1; Suspend(); }
    }

    private void ShowImmediately(Action? afterShown)
    {
        _motionHost.Opacity = 1;
        EnsureMotionTranslate().Y = 0;
        var restScale = UiMotion.EnsureScale(_motionHost); restScale.ScaleX = restScale.ScaleY = 1;
        SetDimOpacity(1);
        EndPanelMotionPrep();
        _entrySettled?.TrySetResult();
        afterShown?.Invoke();
    }

    private void HideImmediately(Action? afterHidden)
    {
        CancelBackdropRequest();
        EnsureMotionTranslate().Y = PanelOffsetY;
        var startScale = UiMotion.EnsureScale(_motionHost); startScale.ScaleX = startScale.ScaleY = .96;
        SetDimOpacity(0);
        _host.Visibility = Visibility.Collapsed;
        PrepareClosedState();
        ClearBackdrop();
        EndPanelMotionPrep();
        ResumeBackground();
        _isClosing = false;
        _entrySettled?.TrySetResult();
        afterHidden?.Invoke();
    }

    private void RunOpenTransition(Action? afterShown)
    {
        var translate = EnsureMotionTranslate();
        var instant = ReducedMotionPreferences.ShouldUseInstantOverlayTransitions;
        if (_dimLayer is { } dim)
            SurfaceMotion.Animate(dim, 1, DimOpenMs, DimOpenEase, instant);
        var scale = UiMotion.EnsureScale(_motionHost);
        SurfaceMotion.AnimateTracks(_motionHost, MotionChannel.Presence,
            [new(_motionHost, UIElement.OpacityProperty, 1, Profile: MotionProfiles.Readable),
             new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.006, 1, MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.006, 1, MotionProfiles.Arrival)],
            PanelOpenMs, OpenEase, () =>
            { EndPanelMotionPrep(); _entrySettled?.TrySetResult(); afterShown?.Invoke(); }, cache: true, instant: instant);
    }

    private void RunCloseTransition(Action? afterHidden)
    {
        var instant = ReducedMotionPreferences.ShouldUseInstantOverlayTransitions;
        if (_dimLayer is { } dim)
            SurfaceMotion.Animate(dim, 0, DimCloseMs, DimCloseEase, instant);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(_motionHost);
        SurfaceMotion.AnimateTracks(_motionHost, MotionChannel.Presence,
            [new(_motionHost, UIElement.OpacityProperty, 0), new(translate, TranslateTransform.YProperty, 6, true),
             new(scale, ScaleTransform.ScaleXProperty, .98, true), new(scale, ScaleTransform.ScaleYProperty, .98, true)],
            PanelCloseMs, CloseEase, () =>
            {
                CancelBackdropRequest();
                _host.Visibility = Visibility.Collapsed; PrepareClosedState(); ClearBackdrop();
                EndPanelMotionPrep(); ResumeBackground(); _isClosing = false;
                _entrySettled?.TrySetResult(); afterHidden?.Invoke();
            }, cache: true, instant: instant);
    }

    private void PrepareOpenState()
    {
        _motionHost.Opacity = 0;
        EnsureMotionTranslate().Y = PanelOffsetY;
        var startScale = UiMotion.EnsureScale(_motionHost); startScale.ScaleX = startScale.ScaleY = .96;
        SetDimOpacity(0);
    }

    private void PrepareClosedState()
    {
        _motionHost.Opacity = 0;
        EnsureMotionTranslate().Y = PanelOffsetY;
        var startScale = UiMotion.EnsureScale(_motionHost); startScale.ScaleX = startScale.ScaleY = .96;
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
        _backdropImage.CacheMode = null;
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
        // Reversal retains the original effect until the newest transition finishes.
        if (_panelCard is not null)
            return;
        _panelCard = _motionHost.FindName("PanelCard") as FrameworkElement
            ?? _motionHost.FindName("DialogCard") as FrameworkElement;
        if (_panelCard?.Effect != null)
        {
            _savedPanelEffect = _panelCard.Effect;
            _panelCard.Effect = null;
        }

    }

    private void EndPanelMotionPrep()
    {
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
        return UiMotion.EnsureTranslate(_motionHost);
    }

    private void StopSurfaceMotion()
    {
        ArrivalGroups.Stop(_motionHost);
        SurfaceMotion.Stop(_motionHost);
        if (_dimLayer is { } dim)
            SurfaceMotion.Stop(dim);
        if (_backdropImage is { } image) SurfaceMotion.Stop(image);
    }

    private void ResumeBackground()
    {
        if (!_backgroundSuspended)
            return;
        OverlayBackgroundSuspender.TryResume(_host);
        _backgroundSuspended = false;
    }
}
