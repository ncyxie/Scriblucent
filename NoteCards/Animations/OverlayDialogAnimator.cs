using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace NoteCards.Animations;

/// <summary>
/// Applies the shared overlay transition stack to owner-sized modal windows.
/// </summary>
public sealed class OverlayDialogAnimator
{
    private readonly OverlayPanelAnimator _animator;
    private readonly Window _window;
    private bool _isClosing;

    private OverlayDialogAnimator(
        Window window,
        FrameworkElement overlayRoot,
        UIElement dimLayer,
        FrameworkElement panelMotionHost,
        Image backdropImage,
        FrameworkElement scrimHost)
    {
        _window = window;
        _animator = OverlayPanelAnimator.Bind(overlayRoot, dimLayer, panelMotionHost, backdropImage, scrimHost);
    }

    public static OverlayDialogAnimator Bind(
        Window window,
        FrameworkElement overlayRoot,
        UIElement dimLayer,
        FrameworkElement panelMotionHost,
        Image backdropImage,
        FrameworkElement scrimHost)
        => new(window, overlayRoot, dimLayer, panelMotionHost, backdropImage, scrimHost);

    public void BeginOpen(Action? afterShown = null)
    {
        _isClosing = false;
        _window.Opacity = 1;

        _animator.Show(
            afterShown,
            () => OverlayBackdropCapture.TryCaptureForDimAnimation(_window.Owner));
    }

    public void RefreshBackdrop()
        => _animator.RefreshBackdrop(() => OverlayBackdropCapture.TryCaptureBehindOverlays(_window.Owner));

    public bool BeginClose(Action onClosed)
    {
        if (_isClosing || _animator.IsClosing)
            return false;

        _isClosing = true;
        _animator.Hide(() =>
        {
            _isClosing = false;
            onClosed();
        });
        return true;
    }
}