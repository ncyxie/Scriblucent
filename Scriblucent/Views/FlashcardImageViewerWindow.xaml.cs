using Scriblucent.Animations;
using Scriblucent.Services;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Scriblucent.Views;

/// <summary>Click-to-zoom viewer for a flashcard's attached image - cards render images small
/// (see FlashcardsPreviewWindow's card templates), so this shows the same image at its full
/// stored resolution, over a dimmed/blurred backdrop covering the owner window.
///
/// Backdrop and foreground animate independently rather than as one rigid block: the backdrop
/// only fades (a scrim shouldn't slide - that reads as the whole scene being a single flat
/// screenshot), while the photo scales in from center with its own fade, which is what actually
/// sells "a photo lifting into view" instead of "a panel sliding up" (the generic
/// WindowContentEnterHelper motion every settings/dialog panel in this app uses, wrong for a
/// lightbox). Backdrop blur reuses OverlayBackdropCapture's existing pre-blurred-snapshot
/// approach (cheap - rendered once, not live per-frame). Dismissed by clicking anywhere, pressing
/// Escape, or the close button - all three play the exit animation before actually closing.</summary>
public partial class FlashcardImageViewerWindow : Window
{
    private const int EnterDurationMs = 240;
    private const int ExitDurationMs = 180;
    private const double EnterScale = 0.92;
    private const double ExitScale = 0.96;

    private bool _isClosing;

    public FlashcardImageViewerWindow(BitmapImage image)
    {
        InitializeComponent();
        WindowThemeService.Register(this);
        ViewerImage.Source = image;

        BackdropLayer.Opacity = 0;
        ForegroundLayer.Opacity = 0;
        var scale = UiMotion.EnsureScale(ForegroundLayer, UiMotion.PrefersReducedMotion ? 1 : EnterScale);
        scale.ScaleX = UiMotion.PrefersReducedMotion ? 1 : EnterScale;
        scale.ScaleY = UiMotion.PrefersReducedMotion ? 1 : EnterScale;

        Loaded += (_, _) => BeginEnterAnimation();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
        BackdropImage.Source = OverlayBackdropCapture.TryCaptureBehindOverlays(Owner);
    }

    private void BeginEnterAnimation()
    {
        if (UiMotion.PrefersReducedMotion)
        {
            BackdropLayer.Opacity = 1;
            ForegroundLayer.Opacity = 1;
            return;
        }

        var scale = UiMotion.EnsureScale(ForegroundLayer);

        UiMotion.Run(BackdropLayer, new[]
        {
            UiMotion.Track(EnterDurationMs, UiMotion.OutCubic, 0, 1, value => BackdropLayer.Opacity = value)
        });

        UiMotion.RunComposited(ForegroundLayer, new[]
        {
            UiMotion.Track(EnterDurationMs, UiMotion.OutCubic, 0, 1, value => ForegroundLayer.Opacity = value),
            UiMotion.Track(EnterDurationMs, UiMotion.OutCubic, EnterScale, 1.0, value =>
            {
                scale.ScaleX = value;
                scale.ScaleY = value;
            })
        });
    }

    private void RequestClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;

        if (UiMotion.PrefersReducedMotion)
        {
            Close();
            return;
        }

        var scale = UiMotion.EnsureScale(ForegroundLayer);

        UiMotion.Run(BackdropLayer, new[]
        {
            UiMotion.Track(ExitDurationMs, UiMotion.InCubic, BackdropLayer.Opacity, 0, value => BackdropLayer.Opacity = value)
        });

        UiMotion.RunComposited(ForegroundLayer, new[]
        {
            UiMotion.Track(ExitDurationMs, UiMotion.InCubic, ForegroundLayer.Opacity, 0, value => ForegroundLayer.Opacity = value),
            UiMotion.Track(ExitDurationMs, UiMotion.InCubic, scale.ScaleX, ExitScale, value =>
            {
                scale.ScaleX = value;
                scale.ScaleY = value;
            })
        },
        Close);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RequestClose();

    private void CloseBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RequestClose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            RequestClose();
    }
}
