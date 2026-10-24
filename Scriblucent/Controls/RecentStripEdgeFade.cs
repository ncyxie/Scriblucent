using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Rendering;

namespace Scriblucent.Controls;

/// <summary>Small background-matched veils over overflowing edges; never masks the scrolling content tree.</summary>
public sealed class RecentStripEdgeFade : FrameworkElement
{
    public static readonly DependencyProperty ViewerProperty = DependencyProperty.Register(nameof(Viewer), typeof(ScrollViewer),
        typeof(RecentStripEdgeFade), new PropertyMetadata(null, ViewerChanged));
    public ScrollViewer? Viewer { get => (ScrollViewer?)GetValue(ViewerProperty); set => SetValue(ViewerProperty, value); }
    private static readonly DependencyProperty BackdropProperty = DependencyProperty.Register("Backdrop", typeof(Brush),
        typeof(RecentStripEdgeFade), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty MaterialProperty = DependencyProperty.Register("Material", typeof(SurfaceMaterialKind),
        typeof(RecentStripEdgeFade), new FrameworkPropertyMetadata(SurfaceMaterialKind.None, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush LeadingMask = CreateMask(false), TrailingMask = CreateMask(true);
    private readonly WorkspaceGradientTexture.Fill _workspace = new();
    private FrameworkElement? _surface;
    private ScrollViewer? _observed;
    private Point _origin;
    private Size _surfaceSize;
    internal double LeftFadeWidth { get; private set; }
    internal double RightFadeWidth { get; private set; }

    public RecentStripEdgeFade()
    {
        IsHitTestVisible = false;
        SetResourceReference(SurfaceMaterial.DarkProperty, "SurfaceMaterialIsDark");
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => { UpdateEdges(); InvalidateVisual(); };
    }
    private static void ViewerChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { if (target is RecentStripEdgeFade fade && fade.IsLoaded) fade.Attach(); }
    private void Attach()
    {
        Detach(); _observed = Viewer;
        if (_observed is null) return;
        _observed.ScrollChanged += Scrolled; _observed.LayoutUpdated += LayoutChanged;
        SmoothScrollCompositor.ObserveHorizontalOffset(_observed, UpdateEdges);
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch { Border border => border.Background, Panel panel => panel.Background, Control control => control.Background, _ => null };
            if (brush is null or SolidColorBrush { Color.A: 0 } || parent is not FrameworkElement surface) continue;
            _surface = surface;
            SetBinding(BackdropProperty, new Binding("Background") { Source = surface });
            SetBinding(MaterialProperty, new Binding { Source = surface, Path = new PropertyPath(SurfaceMaterial.KindProperty) });
            break;
        }
        UpdateEdges(); InvalidateVisual();
    }
    private void Detach()
    {
        if (_observed is not null)
        {
            _observed.ScrollChanged -= Scrolled; _observed.LayoutUpdated -= LayoutChanged;
            SmoothScrollCompositor.UnobserveHorizontalOffset(_observed, UpdateEdges);
        }
        _observed = null; _surface = null;
        BindingOperations.ClearBinding(this, BackdropProperty); BindingOperations.ClearBinding(this, MaterialProperty);
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, _observed)) UpdateEdges(); }
    private void UpdateEdges()
    {
        var limit = Math.Min(20, Math.Max(0, ActualWidth / 3));
        var offset = _observed is null ? 0 : SmoothScrollCompositor.GetVisualHorizontalOffset(_observed);
        var left = _observed is null ? 0 : Math.Min(limit, Math.Max(0, offset));
        var right = _observed is null ? 0 : Math.Min(limit, Math.Max(0, _observed.ScrollableWidth - offset));
        if (Math.Abs(left - LeftFadeWidth) < .01 && Math.Abs(right - RightFadeWidth) < .01) return;
        LeftFadeWidth = left; RightFadeWidth = right; InvalidateVisual();
    }
    private void LayoutChanged(object? sender, EventArgs e)
    {
        if (_surface is null || LeftFadeWidth + RightFadeWidth == 0) return;
        var origin = TranslatePoint(new Point(), _surface);
        if (origin == _origin && _surface.RenderSize == _surfaceSize) return;
        _origin = origin; _surfaceSize = _surface.RenderSize; InvalidateVisual();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SurfaceMaterial.DarkProperty) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_surface is null || ActualWidth <= 0 || ActualHeight <= 0 || LeftFadeWidth + RightFadeWidth == 0) return;
        var background = (Brush?)GetValue(BackdropProperty);
        if (background is null) return;
        var kind = (SurfaceMaterialKind)GetValue(MaterialProperty);
        var material = SurfaceMaterial.Paint(background, kind, SurfaceMaterial.IsDark(this), SurfaceMaterial.GetForeground(_surface));
        var fill = material is null ? background : kind == SurfaceMaterialKind.Workspace
            ? _workspace.Get(material.Colors, _surface.ActualHeight, VisualTreeHelper.GetDpi(_surface)) : material.Fill;
        var backdrop = new DrawingGroup();
        using (var context = backdrop.Open()) context.DrawRectangle(fill, null, new Rect(_surface.RenderSize));
        if (backdrop.CanFreeze) backdrop.Freeze();
        _origin = TranslatePoint(new Point(), _surface); _surfaceSize = _surface.RenderSize;
        void Edge(Rect bounds, Brush mask)
        {
            if (bounds.Width <= 0) return;
            var brush = new DrawingBrush(backdrop)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(_origin.X + bounds.X, _origin.Y + bounds.Y, bounds.Width, bounds.Height),
                Stretch = Stretch.Fill
            };
            if (brush.CanFreeze) brush.Freeze();
            // Only the 20-DIP veil is composited, never the full list or its live text.
            drawing.PushOpacityMask(mask); drawing.DrawRectangle(brush, null, bounds); drawing.Pop();
        }
        // Cover fractional-pixel content clipping too. The extra two DIPs stay
        // inside the viewport's existing six-DIP padding and match its background.
        if (LeftFadeWidth > 0) Edge(new Rect(-2, 0, LeftFadeWidth + 2, ActualHeight), LeadingMask);
        if (RightFadeWidth > 0) Edge(new Rect(ActualWidth - RightFadeWidth, 0, RightFadeWidth + 2, ActualHeight), TrailingMask);
    }
    private static Brush CreateMask(bool trailing)
    {
        var brush = new LinearGradientBrush { StartPoint = trailing ? new(1, 0) : new(0, 0), EndPoint = trailing ? new(0, 0) : new(1, 0) };
        brush.GradientStops.Add(new(Colors.Black, 0));
        brush.GradientStops.Add(new(Colors.Black, .12));
        brush.GradientStops.Add(new(Color.FromArgb(213, 0, 0, 0), .4));
        brush.GradientStops.Add(new(Color.FromArgb(55, 0, 0, 0), .75));
        brush.GradientStops.Add(new(Colors.Transparent, 1));
        brush.Freeze(); return brush;
    }
}
