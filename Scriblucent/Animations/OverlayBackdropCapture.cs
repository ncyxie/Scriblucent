using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// Captures a pre-blurred snapshot for overlay scrims at true 1:1 DIP size
/// (pixel size = DIPs × DPI scale), so the backdrop never looks zoomed.
/// </summary>
public static class OverlayBackdropCapture
{
    private const double BlurMin = 6;
    private const double BlurMax = 14;
    private const int OverlayZIndexThreshold = 200;

    public static BitmapSource? TryCaptureForDimAnimation(Window? window)
        => TryCaptureForDimAnimation(window, GetBlurRadius(window));

    /// <summary>
    /// Captures window content with modal overlays (ZIndex &gt;= 200) temporarily hidden,
    /// so the scrim matches the real layout 1:1 — including the sidebar/content gap.
    /// </summary>
    public static BitmapSource? TryCaptureBehindOverlays(Window? window)
        => TryCaptureBehindOverlays(window, GetBlurRadius(window));

    public static BitmapSource? TryCaptureBehindOverlays(Window? window, double blurRadius)
    {
        if (window?.Content is not Panel rootPanel)
            return TryCaptureForDimAnimation(window, blurRadius);

        var hidden = new List<(UIElement Element, Visibility Visibility)>();
        try
        {
            foreach (UIElement child in rootPanel.Children)
            {
                if (child is not FrameworkElement element)
                    continue;
                if (Panel.GetZIndex(element) < OverlayZIndexThreshold)
                    continue;
                if (element.Visibility != Visibility.Visible)
                    continue;

                hidden.Add((element, element.Visibility));
                element.Visibility = Visibility.Hidden;
            }

            rootPanel.UpdateLayout();
            return TryCaptureForDimAnimation(window, blurRadius);
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (var (element, visibility) in hidden)
                element.Visibility = visibility;
        }
    }

    public static BitmapSource? TryCaptureForDimAnimation(Window? window, double blurRadius)
    {
        if (window?.Content is not FrameworkElement root)
            return null;

        var suspendedEffects = SuspendEffectsForCapture(root);
        try
        {
            root.UpdateLayout();

            var sourceWidth = root.RenderSize.Width;
            var sourceHeight = root.RenderSize.Height;
            if (sourceWidth < 1 || sourceHeight < 1)
                return null;

            var dpi = VisualTreeHelper.GetDpi(root);
            var captured = RenderAtDpi(root, sourceWidth, sourceHeight, dpi);
            if (captured == null)
                return null;

            return blurRadius > 0.5
                ? ApplyBlur(captured, blurRadius, dpi)
                : captured;
        }
        catch
        {
            return null;
        }
        finally
        {
            RestoreEffects(suspendedEffects);
        }
    }

    /// <summary>
    /// DropShadow/Blur on large feature shells makes RenderTargetBitmap extremely expensive
    /// under nested ShowDialog. Suspend effects for the capture pass only.
    /// </summary>
    private static List<(UIElement Element, Effect? Effect)> SuspendEffectsForCapture(DependencyObject root)
    {
        var suspended = new List<(UIElement, Effect?)>();
        SuspendEffectsWalk(root, suspended);
        return suspended;
    }

    private static void SuspendEffectsWalk(DependencyObject current, List<(UIElement Element, Effect? Effect)> suspended)
    {
        if (current is UIElement element && element.Effect is not null)
        {
            suspended.Add((element, element.Effect));
            element.Effect = null;
        }

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
            SuspendEffectsWalk(VisualTreeHelper.GetChild(current, i), suspended);
    }

    private static void RestoreEffects(List<(UIElement Element, Effect? Effect)> suspended)
    {
        foreach (var (element, effect) in suspended)
            element.Effect = effect;
    }

    public static (int Width, int Height) GetScrimLogicalSize(double windowWidth, double windowHeight)
        => (
            Math.Max(1, (int)Math.Ceiling(windowWidth)),
            Math.Max(1, (int)Math.Ceiling(windowHeight)));

    public static double GetBlurRadius(Window? window)
    {
        var height = window?.ActualHeight ?? 720;
        return Math.Clamp(height / 90.0, BlurMin, BlurMax);
    }

    private static BitmapSource RenderAtDpi(Visual root, double dipWidth, double dipHeight, DpiScale dpi)
    {
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(dipWidth * scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(dipHeight * scaleY));

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? ApplyBlur(BitmapSource source, double radius, DpiScale dpi)
    {
        try
        {
            var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            var dipWidth = source.PixelWidth / scaleX;
            var dipHeight = source.PixelHeight / scaleY;

            var image = new Image
            {
                Source = source,
                Width = dipWidth,
                Height = dipHeight,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect
                {
                    Radius = radius,
                    RenderingBias = RenderingBias.Performance
                }
            };

            image.Measure(new Size(dipWidth, dipHeight));
            image.Arrange(new Rect(0, 0, dipWidth, dipHeight));
            image.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                source.PixelWidth,
                source.PixelHeight,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);

            bitmap.Render(image);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return source;
        }
    }
}
