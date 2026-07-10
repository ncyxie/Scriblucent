using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Scriblucent.Animations;

/// <summary>
/// Captures a downscaled, pre-blurred snapshot for overlay scrims. The reduced resolution is scaled up
/// by the overlay viewbox so dim fades stay smooth on fullscreen windows.
/// </summary>
public static class OverlayBackdropCapture
{
    private const int MaxCaptureWidth = 1280;
    private const int MaxCaptureHeight = 720;
    private const double BlurMin = 8;
    private const double BlurMax = 20;
    private const int OverlayZIndexThreshold = 200;

    public static BitmapSource? TryCaptureForDimAnimation(Window? window)
        => TryCaptureForDimAnimation(window, GetBlurRadius(window));

    /// <summary>
    /// Captures only the window content behind modal overlays (ZIndex &lt; 200).
    /// Used while an overlay stays visible so the scrim can swap without hiding the panel.
    /// </summary>
    public static BitmapSource? TryCaptureBehindOverlays(Window? window)
        => TryCaptureBehindOverlays(window, GetBlurRadius(window));

    public static BitmapSource? TryCaptureBehindOverlays(Window? window, double blurRadius)
    {
        if (window?.Content is not Panel rootPanel)
            return TryCaptureForDimAnimation(window, blurRadius);

        try
        {
            rootPanel.UpdateLayout();

            var sourceWidth = rootPanel.RenderSize.Width;
            var sourceHeight = rootPanel.RenderSize.Height;
            if (sourceWidth < 1 || sourceHeight < 1)
                return null;

            var (targetWidth, targetHeight) = GetScrimLogicalSize(sourceWidth, sourceHeight);
            var dpi = VisualTreeHelper.GetDpi(rootPanel);

            var captured = RenderPanelContentBehindOverlays(
                window,
                rootPanel,
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                dpi);

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
    }

    public static BitmapSource? TryCaptureForDimAnimation(Window? window, double blurRadius)
    {
        if (window?.Content is not FrameworkElement root)
            return null;

        try
        {
            root.UpdateLayout();

            var sourceWidth = root.RenderSize.Width;
            var sourceHeight = root.RenderSize.Height;
            if (sourceWidth < 1 || sourceHeight < 1)
                return null;

            var (targetWidth, targetHeight) = GetScrimLogicalSize(sourceWidth, sourceHeight);
            var dpi = VisualTreeHelper.GetDpi(root);

            var needsDownscale = targetWidth < sourceWidth - 0.5 || targetHeight < sourceHeight - 0.5;
            var captured = needsDownscale
                ? RenderDownscaled(root, sourceWidth, sourceHeight, targetWidth, targetHeight, dpi)
                : RenderDirect(root, targetWidth, targetHeight, dpi);

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
    }

    public static (int Width, int Height) GetScrimLogicalSize(double windowWidth, double windowHeight)
        => GetCaptureSize(windowWidth, windowHeight);

    public static double GetBlurRadius(Window? window)
    {
        var height = window?.ActualHeight ?? 720;
        return Math.Clamp(height / 72.0, BlurMin, BlurMax);
    }

    private static BitmapSource? RenderPanelContentBehindOverlays(
        Window window,
        Panel rootPanel,
        double sourceWidth,
        double sourceHeight,
        int targetWidth,
        int targetHeight,
        DpiScale dpi)
    {
        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            if (window.Background is Brush windowBackground)
                drawingContext.DrawRectangle(windowBackground, null, new Rect(0, 0, sourceWidth, sourceHeight));

            foreach (UIElement child in rootPanel.Children)
            {
                if (child is not FrameworkElement element)
                    continue;

                if (Panel.GetZIndex(element) >= OverlayZIndexThreshold
                    || element.Visibility != Visibility.Visible)
                    continue;

                if (element.ActualWidth < 1 || element.ActualHeight < 1)
                    continue;

                var topLeft = element.TransformToAncestor(rootPanel).Transform(new Point(0, 0));
                drawingContext.DrawRectangle(
                    new VisualBrush(element)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight),
                        ViewboxUnits = BrushMappingMode.Absolute
                    },
                    null,
                    new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight));
            }
        }

        var needsDownscale = targetWidth < sourceWidth - 0.5 || targetHeight < sourceHeight - 0.5;
        if (!needsDownscale)
            return RenderDirect(drawingVisual, targetWidth, targetHeight, dpi);

        return RenderDownscaled(drawingVisual, sourceWidth, sourceHeight, targetWidth, targetHeight, dpi);
    }

    private static BitmapSource RenderDirect(Visual root, int width, int height, DpiScale dpi)
    {
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? RenderDownscaled(
        Visual root,
        double sourceWidth,
        double sourceHeight,
        int targetWidth,
        int targetHeight,
        DpiScale dpi)
    {
        var previousScalingMode = BitmapScalingMode.Unspecified;
        if (root is DependencyObject dependencyRoot)
            previousScalingMode = RenderOptions.GetBitmapScalingMode(dependencyRoot);

        try
        {
            if (root is DependencyObject dependencyObject)
                RenderOptions.SetBitmapScalingMode(dependencyObject, BitmapScalingMode.Linear);

            var drawingVisual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(drawingVisual, BitmapScalingMode.Linear);
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawRectangle(
                    new VisualBrush(root)
                    {
                        Stretch = Stretch.Fill,
                        Viewbox = new Rect(0, 0, sourceWidth, sourceHeight),
                        ViewboxUnits = BrushMappingMode.Absolute
                    },
                    null,
                    new Rect(0, 0, targetWidth, targetHeight));
            }

            var bitmap = new RenderTargetBitmap(
                targetWidth,
                targetHeight,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);

            bitmap.Render(drawingVisual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (root is DependencyObject restoreRoot)
                RenderOptions.SetBitmapScalingMode(restoreRoot, previousScalingMode);
        }
    }

    private static BitmapSource? ApplyBlur(BitmapSource source, double radius, DpiScale dpi)
    {
        try
        {
            var width = source.PixelWidth;
            var height = source.PixelHeight;

            var image = new Image
            {
                Source = source,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect
                {
                    Radius = radius,
                    RenderingBias = RenderingBias.Performance
                }
            };

            image.Measure(new Size(width, height));
            image.Arrange(new Rect(0, 0, width, height));
            image.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                width,
                height,
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

    private static (int Width, int Height) GetCaptureSize(double sourceWidth, double sourceHeight)
    {
        var scale = Math.Min(
            MaxCaptureWidth / sourceWidth,
            MaxCaptureHeight / sourceHeight);

        if (scale >= 1.0)
        {
            return (
                Math.Max(1, (int)Math.Ceiling(sourceWidth)),
                Math.Max(1, (int)Math.Ceiling(sourceHeight)));
        }

        return (
            Math.Max(1, (int)Math.Ceiling(sourceWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(sourceHeight * scale)));
    }
}