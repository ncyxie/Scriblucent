using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Views;

public partial class AboutPanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;
    private bool _isClosing;
    private int _brandGeneration;

    public AboutPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { MotionPolicy.DecorationBudgetChanged += DecorationChanged; MotionPolicy.Changed += DecorationChanged; DecorationChanged(); };
        Unloaded += (_, _) => { MotionPolicy.DecorationBudgetChanged -= DecorationChanged; MotionPolicy.Changed -= DecorationChanged; };
        _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
        _overlayAnimator.AdditionalEntranceRunning = () =>
            NativeSurfaceMotion.IsRunning(BrandLogoHost, MotionChannel.Showcase)
            || NativeSurfaceMotion.IsRunning(MeshNear, MotionChannel.Showcase)
            || NativeSurfaceMotion.IsRunning(MeshFar, MotionChannel.Showcase)
            || NativeSurfaceMotion.IsRunning(BrandLight, MotionChannel.Showcase);
    }

    private void DecorationChanged()
    {
        MeshNear.Visibility = MeshFar.Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden;
        if (MotionPolicy.DecorativeLayers) return;
        SurfaceMotion.Stop(MeshNear); SurfaceMotion.Stop(MeshFar); SurfaceMotion.Stop(BrandLight);
        UiMotion.EnsureTranslate(MeshNear).X = UiMotion.EnsureTranslate(MeshFar).X = 0;
        BrandLight.Opacity = 0;
    }

    public void ShowAnimated()
    {
        if (IsVisible && !_isClosing) return;
        var reversing = _isClosing;
        _isClosing = false;
        _overlayAnimator.Show(
            backdropOwner: Window.GetWindow(this), onEntranceStarted: () => PlayBrandEnter(reversing));
    }

    public void HideAnimated()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        PlayBrandExit();
        _overlayAnimator.Hide(afterHidden: () => _isClosing = false);
    }

    private void PlayBrandEnter(bool reversing = false)
    {
        if (BrandLogoHost is null)
            return;

        var generation = ++_brandGeneration;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(BrandLogoHost);
        UiMotion.Stop(BrandLogoHost);
        if (!reversing)
        {
            BrandLogoHost.Opacity = 0;
            translate.Y = MotionPolicy.Spatial ? 22 : 0;
            scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .90 : 1;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (generation != _brandGeneration || _isClosing || !IsVisible) return;
            SurfaceMotion.AnimateTracks(BrandLogoHost, MotionChannel.Showcase,
                [new(BrandLogoHost, UIElement.OpacityProperty, 1, Profile: MotionProfiles.Readable),
                 new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.025, .997, MotionProfiles.Showcase),
                 new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.025, .997, MotionProfiles.Showcase)],
                420, UiMotion.InOutSine, cache: true, startDelayMs: reversing ? 0 : 70);
            PlayStageLight();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PlayStageLight()
    {
        foreach (var mesh in new[] { MeshNear, MeshFar })
        {
            SurfaceMotion.Stop(mesh);
            var move = UiMotion.EnsureTranslate(mesh);
            move.X = MotionPolicy.DecorativeLayers ? ReferenceEquals(mesh, MeshNear) ? -18 : 18 : 0;
            SurfaceMotion.AnimateTracks(mesh, MotionChannel.Showcase,
                [new(move, TranslateTransform.XProperty, 0, true, Profile: MotionProfiles.Arrival)], 650, UiMotion.InOutSine);
        }
        SurfaceMotion.Stop(BrandLight);
        BrandLight.Opacity = MotionPolicy.DecorativeLayers ? 1 : 0;
        var sweep = UiMotion.EnsureTranslate(BrandLight); sweep.X = -160;
        if (MotionPolicy.DecorativeLayers)
            SurfaceMotion.AnimateTracks(BrandLight, MotionChannel.Showcase,
                [new(sweep, TranslateTransform.XProperty, 500, true), new(BrandLight, UIElement.OpacityProperty, 0)],
                650, UiMotion.InOutSine);
    }

    private void PlayBrandExit()
    {
        if (BrandLogoHost is null)
            return;

        // Reverse of enter: sink down + fade while the panel closes.
        _brandGeneration++;
        SurfaceMotion.Stop(BrandLogoHost);
        SurfaceMotion.Stop(MeshNear); SurfaceMotion.Stop(MeshFar); SurfaceMotion.Stop(BrandLight);
        BrandLight.Opacity = 0;
        var (_, translate) = UiMotion.EnsureScaleTranslateGroup(BrandLogoHost);
        SurfaceMotion.Animate(BrandLogoHost, 0, 120, UiMotion.InQuad, false,
            translate: translate, toY: MotionPolicy.Spatial ? 8 : 0);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
    }

    private void OverlayDim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HideAnimated();
    }

    private void GitHubProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore launch failures (no browser / restricted environment).
        }
    }
}
