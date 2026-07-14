using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Animations;

namespace Scriblucent.Views;

public partial class AboutPanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;
    private bool _isClosing;

    public AboutPanel()
    {
        InitializeComponent();
        _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
    }

    public void ShowAnimated()
    {
        _isClosing = false;
        _overlayAnimator.Show(
            captureBackdrop: () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
        PlayBrandEnter();
    }

    public void HideAnimated()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        PlayBrandExit();
        _overlayAnimator.Hide(afterHidden: () => _isClosing = false);
    }

    private void PlayBrandEnter()
    {
        if (BrandLogoHost is null)
            return;

        UiMotion.AnimateContentEnter(BrandLogoHost, UiMotion.OpenMs + 80, UiMotion.ContentLiftY);
    }

    private void PlayBrandExit()
    {
        if (BrandLogoHost is null)
            return;

        // Reverse of enter: sink down + fade while the panel closes.
        UiMotion.AnimateContentExit(BrandLogoHost, UiMotion.CloseMs + 40, UiMotion.ContentLiftY);
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
