using NoteCards.Animations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NoteCards.Views;

public partial class AboutPanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;

    public AboutPanel()
    {
        InitializeComponent();
        _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
    }

    public void ShowAnimated()
    {
        _overlayAnimator.Show(
            captureBackdrop: () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
    }

    public void HideAnimated()
    {
        _overlayAnimator.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
    }

    private void OverlayDim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HideAnimated();
    }
}