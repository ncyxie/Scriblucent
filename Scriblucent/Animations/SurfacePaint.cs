using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

/// <summary>Paint in the surface's own tree, using its outer contour and live border metrics.</summary>
internal abstract class SurfacePaint : FrameworkElement
{
    private static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        "Radius", typeof(CornerRadius), typeof(SurfacePaint), new FrameworkPropertyMetadata(new CornerRadius(14), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        "Frame", typeof(Thickness), typeof(SurfacePaint), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PaintWidthProperty = DependencyProperty.Register(
        "PaintWidth", typeof(double), typeof(SurfacePaint), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    private (Size Size, CornerRadius Radius, Thickness Frame)? _key;
    private readonly Dictionary<double, Geometry> _geometry = new();
    private readonly Dictionary<(double Thickness, double Inset, bool OpenTop), Geometry> _rings = new();
    private Border? _metrics;
    private readonly SurfacePlacement _placement;
    protected SurfacePaint()
    {
        _placement = new(this, () => _metrics);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _metrics is DashboardGroupCard) InvalidateVisual();
        };
    }
    protected bool OwnerClipsContour { get; private set; }

    internal bool Attach(FrameworkElement surface, int order)
    {
        if (surface is Control control)
        {
            control.ApplyTemplate();
            var templateFace = FindBorder(control);
            if (templateFace is not null) surface = templateFace;
        }
        var border = surface as Border;
        var host = surface as Grid;
        var metrics = border ?? host?.Children.OfType<Border>().FirstOrDefault();
        _metrics = metrics;
        if (_metrics is not null)
        {
            _metrics.SizeChanged += FaceSizeChanged;
            Unloaded += (_, _) => _metrics.SizeChanged -= FaceSizeChanged;
            Loaded += (_, _) => { _metrics.SizeChanged -= FaceSizeChanged; _metrics.SizeChanged += FaceSizeChanged; InvalidateVisual(); };
        }
        OwnerClipsContour = border is InteractionSurface || border is not null && Behaviors.RoundedClipBehavior.GetIsEnabled(border);
        if (border is not null)
        {
            if (border.Child is null) border.Child = new PaintHost();
            host = border.Child as Grid;
            if (host is null)
            {
                var child = border.Child; border.Child = null;
                host = new PaintHost(); host.Children.Add(child); border.Child = host;
            }
            BindOuterMargin(this, border);
        }
        if (host is null) return false;
        if (metrics is not null)
        {
            SetBinding(RadiusProperty, new Binding(nameof(Border.CornerRadius)) { Source = metrics });
            SetBinding(FrameProperty, new Binding(nameof(Border.BorderThickness)) { Source = metrics });
        }
        if (surface is InteractionSurface face)
            SetBinding(PaintWidthProperty, new Binding(nameof(InteractionSurface.PaintedWidth)) { Source = face });
        IsHitTestVisible = false;
        Grid.SetRowSpan(this, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(this, Math.Max(1, host.ColumnDefinitions.Count));
        Panel.SetZIndex(this, order); host.Children.Add(this);
        return true;
    }

    internal static void BindOuterMargin(FrameworkElement target, Border border)
    {
        var margin = new MultiBinding { Converter = SurfaceMarginConverter.Instance };
        margin.Bindings.Add(new Binding(nameof(Border.Padding)) { Source = border });
        margin.Bindings.Add(new Binding(nameof(Border.BorderThickness)) { Source = border });
        margin.Bindings.Add(new Binding { Source = border });
        margin.Bindings.Add(new Binding(nameof(Border.UseLayoutRounding)) { Source = border });
        target.SetBinding(MarginProperty, margin);
    }

    internal static Border? FindBorder(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border) return border;
            if (FindBorder(child) is { } found) return found;
        }
        return null;
    }

    protected Geometry Contour(double inset = 0)
    {
        var width = (double)GetValue(PaintWidthProperty);
        var bounds = _metrics is { ActualWidth: > 0, ActualHeight: > 0 } ? _metrics.RenderSize : RenderSize;
        var size = new Size(double.IsNaN(width) ? bounds.Width : Math.Min(bounds.Width, Math.Max(0, width)), bounds.Height);
        var radius = (CornerRadius)GetValue(RadiusProperty);
        var frame = (Thickness)GetValue(FrameProperty);
        if (_metrics is not null) frame = SurfaceContour.EffectiveFrame(_metrics);
        var key = (size, radius, frame);
        if (_key != key)
        {
            _key = key;
            _geometry.Clear();
            _rings.Clear();
        }
        if (!_geometry.TryGetValue(inset, out var geometry))
            _geometry.Add(inset, geometry = SurfaceContour.Create(size, radius, frame, inset));
        return geometry;
    }

    protected Geometry Rim(double thickness, double inset = 0, bool openTop = false)
    {
        var outer = Contour(inset); // Refresh both caches before looking up a ring.
        var key = (thickness, inset, openTop);
        if (!_rings.TryGetValue(key, out var geometry))
        {
            var metrics = _key!.Value;
            geometry = SurfaceContour.Ring(metrics.Size, metrics.Radius, metrics.Frame, inset, thickness);
            if (openTop) geometry = SurfaceContour.WithoutTop(geometry, outer.Bounds, (CornerRadius)GetValue(RadiusProperty));
            _rings.Add(key, geometry);
        }
        return geometry;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        BindingOperations.GetMultiBindingExpression(this, MarginProperty)?.UpdateTarget();
        _key = null; InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new();
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_metrics is not DashboardGroupCard { IsViewportActive: false }) _placement.Arm();
        return base.ArrangeOverride(finalSize);
    }
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
    protected sealed override void OnRender(DrawingContext drawing)
    {
        // Hidden WPF elements can still rebuild render data when their layout size
        // changes. Parked group frames must not rebuild contours/materials per pixel.
        if (_metrics is DashboardGroupCard { IsViewportActive: false }) return;
        // Fractional DPI rounds host slots and negative margins independently. Use the face's
        // actual origin and size rather than treating the overlay's rounded slot as its bounds.
        drawing.PushTransform(_placement.Transform);
        DrawSurface(drawing);
        drawing.Pop();
    }
    protected abstract void DrawSurface(DrawingContext drawing);
    private void FaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_metrics is not DashboardGroupCard { IsViewportActive: false }) InvalidateVisual();
    }

    private sealed class PaintHost : Grid
    {
        // Paint deliberately extends across the owner's padding. A fractional-width
        // layout mismatch can make Grid add a content-slot clip even with ClipToBounds
        // false, cutting that paint into an inset rectangle. The face/layer owns its
        // rounded contour; retain a slot clip only when explicitly requested.
        protected override Geometry? GetLayoutClip(Size layoutSlotSize)
            => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : null;
    }

    private sealed class SurfaceMarginConverter : IMultiValueConverter
    {
        internal static readonly SurfaceMarginConverter Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var p = values[0] is Thickness padding ? padding : new Thickness();
            var b = values[1] is Thickness border ? border : new Thickness();
            if (values[2] is Border face) b = SurfaceContour.EffectiveFrame(face);
            return new Thickness(-p.Left - b.Left, -p.Top - b.Top, -p.Right - b.Right, -p.Bottom - b.Bottom);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

internal static class SurfaceContour
{
    private const int MaxCachedContours = 256;
    private readonly record struct ContourKey(Size Size, CornerRadius Radius, Thickness Frame, double Inset, double? RingThickness);
    private static readonly Dictionary<ContourKey, Geometry> Contours = new();
    private static readonly Queue<ContourKey> ContourOrder = new();
    private static readonly object ContourGate = new();
    private static bool TryGet(ContourKey key, out Geometry geometry)
    { lock (ContourGate) return Contours.TryGetValue(key, out geometry!); }
    private static Geometry Remember(ContourKey key, Geometry geometry)
    {
        lock (ContourGate)
        {
            if (Contours.TryGetValue(key, out var existing)) return existing;
            if (Contours.Count >= MaxCachedContours) Contours.Remove(ContourOrder.Dequeue());
            Contours.Add(key, geometry); ContourOrder.Enqueue(key);
            return geometry;
        }
    }
    internal static Thickness EffectiveFrame(Border border)
    {
        var frame = border.BorderThickness;
        if (!border.UseLayoutRounding) return frame;
        var dpi = VisualTreeHelper.GetDpi(border);
        return new(Math.Round(frame.Left * dpi.DpiScaleX) / dpi.DpiScaleX,
            Math.Round(frame.Top * dpi.DpiScaleY) / dpi.DpiScaleY,
            Math.Round(frame.Right * dpi.DpiScaleX) / dpi.DpiScaleX,
            Math.Round(frame.Bottom * dpi.DpiScaleY) / dpi.DpiScaleY);
    }
    internal static Geometry Ring(Geometry outer, Geometry inner)
    {
        var ring = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { outer, inner } };
        ring.Freeze(); return ring;
    }
    internal static Geometry Ring(Size size, CornerRadius radius, Thickness frame, double inset, double thickness)
    {
        var key = new ContourKey(size, radius, frame, inset, thickness);
        if (TryGet(key, out var cached)) return cached;
        // One native stream retains both exact contours. GeometryGroup otherwise
        // converts its children to PathGeometry repeatedly during render precompute.
        var ring = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = ring.Open())
        {
            WriteContour(context, size, radius, frame, inset);
            WriteContour(context, size, radius, frame, inset + thickness);
        }
        ring.Freeze(); return Remember(key, ring);
    }
    internal static Geometry WithoutTop(Geometry rim, Rect bounds, CornerRadius radius)
    {
        var depth = Math.Min(bounds.Height, Math.Max(1, Math.Max(radius.TopLeft, radius.TopRight)));
        var opening = new RectangleGeometry(new Rect(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, depth + 1));
        var result = new CombinedGeometry(GeometryCombineMode.Exclude, rim, opening);
        result.Freeze(); return result;
    }
    // WPF's outer Border radii include half the adjacent stroke width. A zero corner stays square.
    internal static Geometry Create(Size size, CornerRadius radius, Thickness frame, double inset = 0)
    {
        var key = new ContourKey(size, radius, frame, inset, null);
        if (TryGet(key, out var cached)) return cached;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) WriteContour(context, size, radius, frame, inset);
        geometry.Freeze(); return Remember(key, geometry);
    }
    private static void WriteContour(StreamGeometryContext c, Size size, CornerRadius radius, Thickness frame, double inset)
    {
        var w = Math.Max(0, size.Width - inset * 2); var h = Math.Max(0, size.Height - inset * 2);
        double R(double r, double b) => r == 0 ? 0 : Math.Max(0, r + b / 2 - inset);
        var tl = new Size(R(radius.TopLeft, frame.Left), R(radius.TopLeft, frame.Top));
        var tr = new Size(R(radius.TopRight, frame.Right), R(radius.TopRight, frame.Top));
        var br = new Size(R(radius.BottomRight, frame.Right), R(radius.BottomRight, frame.Bottom));
        var bl = new Size(R(radius.BottomLeft, frame.Left), R(radius.BottomLeft, frame.Bottom));
        double Ratio(double extent, double sum) => sum > extent && sum > 0 ? extent / sum : 1;
        // WPF partitions each overlapping side independently, rather than scaling every corner.
        var top = Ratio(w, tl.Width + tr.Width); var bottomScale = Ratio(w, bl.Width + br.Width);
        var left = Ratio(h, tl.Height + bl.Height); var rightScale = Ratio(h, tr.Height + br.Height);
        tl = new(tl.Width * top, tl.Height * left); tr = new(tr.Width * top, tr.Height * rightScale);
        br = new(br.Width * bottomScale, br.Height * rightScale); bl = new(bl.Width * bottomScale, bl.Height * left);
        var x = inset; var y = inset; var right = x + w; var bottom = y + h;
        void Arc(Point point, Size r)
        {
            if (r.Width <= 0 || r.Height <= 0) c.LineTo(point, true, false);
            else c.ArcTo(point, r, 0, false, SweepDirection.Clockwise, true, false);
        }
        c.BeginFigure(new(x + tl.Width, y), true, true);
        c.LineTo(new(right - tr.Width, y), true, false); Arc(new(right, y + tr.Height), tr);
        c.LineTo(new(right, bottom - br.Height), true, false); Arc(new(right - br.Width, bottom), br);
        c.LineTo(new(x + bl.Width, bottom), true, false); Arc(new(x, bottom - bl.Height), bl);
        c.LineTo(new(x, y + tl.Height), true, false); Arc(new(x + tl.Width, y), tl);
    }
}
