using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Rendering;
using SkiaSharp;

namespace Scriblucent.Controls;

/// <summary>Uses the scrolling tile's exact text/chip layout, rasterized once per content/size/theme/DPI change.</summary>
public sealed class NotePreviewPresenter : FrameworkElement
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview), typeof(NotePreviewSnapshot),
        typeof(NotePreviewPresenter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty HasTagsProperty = DependencyProperty.Register(nameof(HasTags), typeof(bool),
        typeof(NotePreviewPresenter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(nameof(TextBrush));
    public static readonly DependencyProperty ChipBackgroundProperty = BrushProperty(nameof(ChipBackground));
    public static readonly DependencyProperty ChipForegroundProperty = BrushProperty(nameof(ChipForeground));
    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(NotePreviewPresenter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public NotePreviewSnapshot? Preview { get => (NotePreviewSnapshot?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public bool HasTags { get => (bool)GetValue(HasTagsProperty); set => SetValue(HasTagsProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush ChipBackground { get => (Brush)GetValue(ChipBackgroundProperty); set => SetValue(ChipBackgroundProperty, value); }
    public Brush ChipForeground { get => (Brush)GetValue(ChipForegroundProperty); set => SetValue(ChipForegroundProperty, value); }
    private BitmapSource? _bitmap;
    private SkiaTextLayout? _layout;
    private float _layoutWidth, _layoutScale;
    private int _layoutLines;
    private Size _paintSize;
    private double _paintDpi;
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var presenter = (NotePreviewPresenter)sender;
        presenter._bitmap = null;
        if (args.Property == PreviewProperty || args.Property == HasTagsProperty) presenter._layout = null;
    }
    public NotePreviewPresenter()
    {
        IsHitTestVisible = false; ClipToBounds = true; VerticalAlignment = VerticalAlignment.Top;
        Loaded += (_, _) => { LocalizationService.CultureChanged += CultureChanged; _bitmap = null; _layout = null; InvalidateMeasure(); InvalidateVisual(); };
        Unloaded += (_, _) => { LocalizationService.CultureChanged -= CultureChanged; _bitmap = null; _layout = null; };
    }
    private void CultureChanged(object? sender, EventArgs args) { _bitmap = null; _layout = null; InvalidateMeasure(); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new PreviewPeer(this);
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); _bitmap = null; InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 190;
        if (Preview is null) return new Size(width, 0);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var layout = Layout((float)(width * dpi), HasTags ? 2 : 3, (float)dpi);
        return new Size(width, Math.Ceiling(layout.Height) / dpi);
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Preview is null || ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_bitmap is null || _paintSize != RenderSize || _paintDpi != dpi)
        {
            var scale = (float)dpi;
            var lines = Math.Max(1, Math.Min(HasTags ? 2 : 3, (int)Math.Floor((ActualHeight + .1) / NotePreviewRaster.LineHeight)));
            var layout = Layout((float)ActualWidth * scale, lines, scale);
            using var bitmap = new SKBitmap((int)Math.Ceiling(ActualWidth * dpi), (int)Math.Ceiling(ActualHeight * dpi), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
            SkiaCardRenderer.PaintLayout(canvas, layout, 0, 0, Color(TextBrush), scale, Color(ChipBackground), Color(ChipForeground), SurfaceMaterialRaster.ResourceDark);
            _bitmap = SkiaMindMapSnapshotCache.ToBitmapSource(bitmap, scale);
            _paintSize = RenderSize; _paintDpi = dpi;
        }
        context.DrawImage(_bitmap, new Rect(RenderSize));
    }
    private static SKColor Color(Brush brush)
    { var c = (brush as SolidColorBrush)?.Color ?? Colors.Transparent; return new SKColor(c.R, c.G, c.B, c.A); }

    private SkiaTextLayout Layout(float width, int lines, float scale)
    {
        if (_layout is null || _layoutWidth != width || _layoutLines != lines || _layoutScale != scale)
        {
            _layout = NotePreviewRaster.Layout(Preview!, width, lines, scale);
            _layoutWidth = width; _layoutLines = lines; _layoutScale = scale;
        }
        return _layout;
    }

    private sealed class PreviewPeer(NotePreviewPresenter owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(NotePreviewPresenter);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => string.Concat(owner.Preview?.Parts.Select(part => part.ImageCount == 0 ? part.Text
            : " " + (part.ImageCount == 1 ? LocalizationService.GetString("NotePreviewImage")
                : string.Format(LocalizationService.GetString("NotePreviewImages"), part.ImageCount)) + " ") ?? []);
    }
}
