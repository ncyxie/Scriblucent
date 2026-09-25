using Scriblucent.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Controls;

/// <summary>
/// Wraps a dashboard listing card template (flashcard sets, quizzes, mind maps) and gives it
/// the same scroll-tile behavior as the notes cards: during smooth-scroll sessions the host
/// shows the off-thread Skia-rendered tile, and at rest the live template renders with all
/// its interactions (motion, drag/drop, context menus) intact.
/// </summary>
public class DashboardTileHost : ContentControl
{
    public static readonly DependencyProperty RendererKeyProperty = DependencyProperty.Register(
        nameof(RendererKey),
        typeof(string),
        typeof(DashboardTileHost),
        new PropertyMetadata(null, OnRendererKeyChanged));

    public static readonly DependencyProperty IsBitmapModeProperty = DependencyProperty.Register(
        nameof(IsBitmapMode),
        typeof(bool),
        typeof(DashboardTileHost),
        new PropertyMetadata(false, OnIsBitmapModeChanged));

    private Image? _bitmapLayer;
    private ContentPresenter? _liveContent;

    static DashboardTileHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DashboardTileHost),
            new FrameworkPropertyMetadata(typeof(DashboardTileHost)));
    }

    public string? RendererKey
    {
        get => (string?)GetValue(RendererKeyProperty);
        set => SetValue(RendererKeyProperty, value);
    }

    public bool IsBitmapMode
    {
        get => (bool)GetValue(IsBitmapModeProperty);
        set => SetValue(IsBitmapModeProperty, value);
    }

    public DashboardTileHost()
    {
        DataContextChanged += (_, _) => ApplyBitmapMode();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bitmapLayer = GetTemplateChild("BitmapLayer") as Image;
        _liveContent = GetTemplateChild("LiveContent") as ContentPresenter;
        ApplyBitmapMode();
    }

    private static void OnRendererKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DashboardTileHost)d).ApplyBitmapMode();

    private static void OnIsBitmapModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DashboardTileHost)d).ApplyBitmapMode();

    private void ApplyBitmapMode()
    {
        var layer = _bitmapLayer;
        var live = _liveContent;
        if (layer is null || live is null)
            return;

        var bitmapMode = IsBitmapMode;
        layer.Visibility = bitmapMode ? Visibility.Visible : Visibility.Collapsed;
        live.Visibility = bitmapMode ? Visibility.Collapsed : Visibility.Visible;

        if (bitmapMode)
        {
            var renderer = SkiaTileRenderers.ForKey(RendererKey ?? string.Empty);
            var vm = DataContext;
            if (renderer is null || vm is null)
                return;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var requestedVm = vm;
            var widthDip = ActualWidth > 0 ? (float)ActualWidth : 0f;
            layer.Source = SkiaCardTileCache.GetOrRequest(vm, dpi, renderer, source =>
            {
                // The render landed on the thread pool and marshaled back; only apply if this
                // container still shows the same card in bitmap mode.
                if (!IsBitmapMode || !ReferenceEquals(DataContext, requestedVm))
                    return;

                var previousHeight = (layer.Source as BitmapSource)?.PixelHeight;
                layer.Source = source;
                if (previousHeight != source.PixelHeight)
                    InvalidateMeasure();
            }, widthDip);
        }
        else
        {
            layer.Source = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (IsBitmapMode && _bitmapLayer?.Source is BitmapSource bitmap)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return new Size(bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi);
        }

        return base.MeasureOverride(availableSize);
    }
}
