using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// Applies the shared overlay transition stack to owner-sized modal windows.
/// </summary>
public sealed class OverlayDialogAnimator
{
    private readonly OverlayPanelAnimator _animator;
    private readonly Window _window;
    private bool _isClosing;
    private IDisposable? _blockedWindows;

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
        _window.Closed += (_, _) => ReleaseBlockedWindows();
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

        var entrance = new OverlayEntranceBatch();
        _blockedWindows ??= BlockedWindowBackdrops.Begin(_window, entrance);
        _animator.Show(entrance, afterShown, backdropOwner: _window.Owner);
        entrance.Seal();
    }

    public void RefreshBackdrop()
        => _animator.RefreshBackdrop();

    public bool BeginClose(Action onClosed)
    {
        if (_isClosing || _animator.IsClosing)
            return false;

        _isClosing = true;
        ReleaseBlockedWindows();
        _animator.Hide(() =>
        {
            _isClosing = false;
            onClosed();
        });
        return true;
    }

    private void ReleaseBlockedWindows()
    {
        _blockedWindows?.Dispose();
        _blockedWindows = null;
    }
}
