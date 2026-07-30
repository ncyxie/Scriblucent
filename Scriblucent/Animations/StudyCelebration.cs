using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Services;

namespace Scriblucent.Animations;

/// <summary>
/// Tier 3 study celebrations: pre-placed confetti pieces driven by one UiMotion batch per overlay.
/// </summary>
public static class StudyCelebration
{
    public enum Intensity
    {
        Full,
        Subtle
    }

    private enum PieceRole
    {
        Rain,
        LeftCannon,
        RightCannon,
        LateRain
    }

    private static readonly object ConfettiBatchKey = new();

    private const int FadeInMs = 60;
    private const int FadeOutMs = 140;
    private const int FullFallMs = 2100;
    private const int SubtleFallMs = 1750;
    private const double SpawnCanvasTop = -48;

    public static void PlayConfetti(Panel? overlay, Intensity intensity = Intensity.Full)
    {
        if (overlay is null || ReducedMotionPreferences.ShouldReduceMotion)
            return;

        try
        {
            if (!overlay.Dispatcher.CheckAccess())
            {
                overlay.Dispatcher.BeginInvoke(() => PlayConfetti(overlay, intensity));
                return;
            }

            Stop(overlay);
            overlay.Visibility = Visibility.Visible;

            var pieces = overlay.Children.OfType<FrameworkElement>().ToList();
            if (pieces.Count == 0)
                return;

            var overlayHeight = ResolveOverlayExtent(overlay, vertical: true);
            var overlayWidth = ResolveOverlayExtent(overlay, vertical: false);
            if (overlayHeight <= 1 || overlayWidth <= 1)
            {
                void OnOverlaySized(object? sender, SizeChangedEventArgs _)
                {
                    overlay.SizeChanged -= OnOverlaySized;
                    PlayConfetti(overlay, intensity);
                }

                overlay.SizeChanged += OnOverlaySized;
                return;
            }

            var fallBase = overlayHeight * 0.96 + Math.Abs(SpawnCanvasTop);
            var fallMs = intensity == Intensity.Full ? FullFallMs : SubtleFallMs;
            var rainCount = (int)Math.Round(pieces.Count * 0.5);
            var cannonCount = (int)Math.Round(pieces.Count * 0.34);
            var leftCannonCount = cannonCount / 2;
            var tracks = new List<RenderTransitionClock.Track>(pieces.Count * 4);

            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                var role = ResolveRole(index, pieces.Count, rainCount, leftCannonCount);
                var motion = EnsureMotionTransform(piece);
                var translate = motion.Translate;
                var rotate = motion.Rotate;

                UiMotion.Clear(piece, UIElement.OpacityProperty);
                UiMotion.Clear(translate, TranslateTransform.XProperty);
                UiMotion.Clear(translate, TranslateTransform.YProperty);
                UiMotion.Clear(rotate, RotateTransform.AngleProperty);

                var startY = ResolveStartY(index);
                var endY = fallBase + (index % 5) * (overlayHeight * 0.018);
                var drift = ResolveDrift(role, index, overlayWidth);
                var peakOpacity = 0.78 + (index % 4) * 0.055;
                var spin = ResolveSpin(index);
                var delay = ResolveDelay(role, index, intensity, rainCount, leftCannonCount, cannonCount);
                var pieceFallMs = fallMs + (index % 5) * 110 + (role == PieceRole.LateRain ? 180 : 0);
                var fadeOutStart = delay + pieceFallMs - FadeOutMs;

                translate.Y = startY;
                translate.X = 0;
                rotate.Angle = 0;
                piece.Opacity = 0;
                PositionPiece(piece, overlay, role, index, overlayWidth, rainCount, leftCannonCount);

                tracks.Add(
                    UiMotion.Track(
                        FadeInMs,
                        UiMotion.OutQuad,
                        0,
                        peakOpacity,
                        value => piece.Opacity = value,
                        startDelayMs: delay));
                tracks.Add(
                    UiMotion.Track(
                        FadeOutMs,
                        UiMotion.InQuad,
                        peakOpacity,
                        0,
                        value => piece.Opacity = value,
                        startDelayMs: fadeOutStart));
                tracks.Add(
                    UiMotion.Track(
                        pieceFallMs,
                        UiMotion.InCubic,
                        startY,
                        endY,
                        value => translate.Y = value,
                        startDelayMs: delay));
                tracks.Add(
                    UiMotion.Track(
                        pieceFallMs,
                        UiMotion.InOutSine,
                        0,
                        drift,
                        value => translate.X = value,
                        startDelayMs: delay));
                tracks.Add(
                    UiMotion.Track(
                        pieceFallMs,
                        UiMotion.OutSine,
                        0,
                        spin,
                        value => rotate.Angle = value,
                        startDelayMs: delay));
            }

            UiMotion.Run(ConfettiBatchKey, tracks);
        }
        catch (Exception ex)
        {
            CrashLog.Append("StudyCelebration", ex);
        }
    }

    public static void Stop(Panel? overlay)
    {
        UiMotion.Stop(ConfettiBatchKey);

        if (overlay is null)
            return;

        overlay.Visibility = Visibility.Collapsed;

        foreach (var piece in overlay.Children.OfType<FrameworkElement>())
        {
            UiMotion.Stop(piece);
            piece.Opacity = 0;
            var motion = EnsureMotionTransform(piece);
            motion.Translate.X = 0;
            motion.Translate.Y = 0;
            motion.Rotate.Angle = 0;
            if (overlay is Canvas)
                Canvas.SetTop(piece, SpawnCanvasTop);
        }
    }

    public static void RevealScore(params FrameworkElement?[] elements)
        => RevealScore(60, elements);

    public static void RevealScore(int startDelayMs, params FrameworkElement?[] elements)
    {
        if (ReducedMotionPreferences.ShouldReduceMotion)
            return;

        var delay = startDelayMs;
        foreach (var element in elements)
        {
            if (element is null)
                continue;

            UiMotion.Stop(element);
            element.Opacity = 0;
            var scale = UiMotion.EnsureScale(element, 0.9);
            scale.ScaleX = 0.9;
            scale.ScaleY = 0.9;

            var revealDelay = delay;
            UiMotion.Run(
                element,
                new[]
                {
                    UiMotion.Track(
                        UiMotion.OpenMs,
                        UiMotion.OutBack,
                        0,
                        1,
                        value => element.Opacity = value,
                        startDelayMs: revealDelay),
                    UiMotion.Track(
                        UiMotion.OpenMs,
                        UiMotion.OutBack,
                        0.9,
                        1,
                        value =>
                        {
                            scale.ScaleX = value;
                            scale.ScaleY = value;
                        },
                        startDelayMs: revealDelay)
                });

            delay += 90;
        }
    }

    private static PieceRole ResolveRole(int index, int total, int rainCount, int leftCannonCount)
    {
        if (index < rainCount)
            return PieceRole.Rain;

        var cannonStart = rainCount;
        var cannonEnd = cannonStart + leftCannonCount * 2;
        if (index < cannonStart + leftCannonCount)
            return PieceRole.LeftCannon;
        if (index < cannonEnd)
            return PieceRole.RightCannon;

        return PieceRole.LateRain;
    }

    private static void PositionPiece(
        FrameworkElement piece,
        Panel overlay,
        PieceRole role,
        int index,
        double overlayWidth,
        int rainCount,
        int leftCannonCount)
    {
        if (overlay is not Canvas)
            return;

        Canvas.SetTop(piece, SpawnCanvasTop);

        var lane = GoldenRatioFraction(index);
        switch (role)
        {
            case PieceRole.Rain:
                Canvas.SetLeft(piece, overlayWidth * (0.01 + 0.98 * lane));
                break;
            case PieceRole.LeftCannon:
                var leftSlot = index - rainCount;
                Canvas.SetLeft(piece, overlayWidth * (0.01 + leftSlot % 6 * 0.018));
                break;
            case PieceRole.RightCannon:
                var rightSlot = index - rainCount - leftCannonCount;
                Canvas.SetLeft(piece, overlayWidth * (0.82 + rightSlot % 6 * 0.03));
                break;
            case PieceRole.LateRain:
                Canvas.SetLeft(piece, overlayWidth * (0.03 + 0.94 * GoldenRatioFraction(index + 17)));
                break;
        }
    }

    private static double ResolveStartY(int index)
        => -20 - (index % 10) * 24;

    private static double ResolveDrift(PieceRole role, int index, double overlayWidth)
    {
        var magnitude = overlayWidth * 0.05 * (0.45 + (index % 6) * 0.11);
        return role switch
        {
            PieceRole.LeftCannon => magnitude,
            PieceRole.RightCannon => -magnitude,
            _ => (index % 2 == 0 ? 1 : -1) * magnitude * 0.65
        };
    }

    private static double ResolveSpin(int index)
        => (index % 2 == 0 ? 1 : -1) * (300 + (index % 7) * 85);

    private static int ResolveDelay(
        PieceRole role,
        int index,
        Intensity intensity,
        int rainCount,
        int leftCannonCount,
        int cannonCount)
    {
        var burst = intensity == Intensity.Full ? 3 : 5;
        var wave = intensity == Intensity.Full ? 14 : 22;
        return role switch
        {
            PieceRole.Rain => (index % 12) * burst + (index / 12) * wave,
            PieceRole.LeftCannon => 6 + (index - rainCount) * (burst + 1),
            PieceRole.RightCannon => 10 + (index - rainCount - leftCannonCount) * (burst + 1),
            PieceRole.LateRain => 280 + (index - rainCount - cannonCount) * (burst + 3),
            _ => 0
        };
    }

    private static (TranslateTransform Translate, RotateTransform Rotate) EnsureMotionTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            var rotate = group.Children.OfType<RotateTransform>().FirstOrDefault();
            if (translate is not null && rotate is not null && !translate.IsFrozen && !rotate.IsFrozen)
                return (translate, rotate);
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var createdTranslate = new TranslateTransform();
        var createdRotate = new RotateTransform();
        element.RenderTransform = new TransformGroup
        {
            Children = { createdRotate, createdTranslate }
        };
        return (createdTranslate, createdRotate);
    }

    private static double ResolveOverlayExtent(FrameworkElement overlay, bool vertical)
    {
        overlay.UpdateLayout();
        var size = vertical ? overlay.ActualHeight : overlay.ActualWidth;
        if (size > 1)
            return size;

        var window = Window.GetWindow(overlay);
        if (window is not null)
        {
            window.UpdateLayout();
            size = vertical ? window.ActualHeight : window.ActualWidth;
            if (size > 1)
                return size;
        }

        return vertical ? 720 : 1100;
    }

    private static double GoldenRatioFraction(int index)
        => (index * 0.618033988749895) % 1.0;
}