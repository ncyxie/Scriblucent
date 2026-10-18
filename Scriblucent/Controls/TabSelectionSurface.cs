using System.Windows;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Paints the active tab at its animated bounds without measuring or moving headers.</summary>
public sealed class TabSelectionSurface : FrameworkElement
{
    public static readonly DependencyProperty BoundsProperty = DependencyProperty.Register(nameof(Bounds), typeof(Rect), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RimProperty = DependencyProperty.Register(nameof(Rim), typeof(Brush), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(new CornerRadius(10), FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is CornerRadius r && new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }
            .All(radius => double.IsFinite(radius) && radius >= 0));
    public static readonly DependencyProperty ConnectionDepthProperty = DependencyProperty.Register(nameof(ConnectionDepth), typeof(double), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), value => value is double depth && double.IsFinite(depth) && depth >= 0);
    public static readonly DependencyProperty ViewportBoundsProperty = DependencyProperty.Register(nameof(ViewportBounds), typeof(Rect), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PressProgressProperty = DependencyProperty.Register(nameof(PressProgress), typeof(double), typeof(TabSelectionSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), value => value is double progress && double.IsFinite(progress) && progress >= 0 && progress <= 1);
    public double PressProgress { get => (double)GetValue(PressProgressProperty); set => SetValue(PressProgressProperty, value); }
    internal CommandSurfaceLight? CommandLight { get; set; }

    public Rect Bounds { get => (Rect)GetValue(BoundsProperty); set => SetValue(BoundsProperty, value); }
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? Rim { get => (Brush?)GetValue(RimProperty); set => SetValue(RimProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double ConnectionDepth { get => (double)GetValue(ConnectionDepthProperty); set => SetValue(ConnectionDepthProperty, value); }
    public Rect ViewportBounds { get => (Rect)GetValue(ViewportBoundsProperty); set => SetValue(ViewportBoundsProperty, value); }

    private Pen? _rim;
    private (Size Size, CornerRadius Radius, bool Outline, double Connection)? _contourKey;
    private Geometry? _contour;
    private Rect _clipBounds = Rect.Empty;
    private RectangleGeometry? _clip;

    public TabSelectionSurface()
    {
        IsHitTestVisible = false;
        SetResourceReference(SurfaceMaterial.DarkProperty, "SurfaceMaterialIsDark");
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // A dragged tab paints its own rounded face above neighbouring labels. Its docked
        // connector must stand down until the header returns to the rail.
        if (EditorChromeContext.GetIsTabDragging(this)) return;
        var bounds = Bounds;
        if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1) return;
        var paint = SurfaceMaterial.Paint(Background, SurfaceMaterialKind.Raised, SurfaceMaterial.IsDark(this));
        var edge = Rim ?? paint?.Edge;
        if (_rim?.Brush != edge)
        {
            _rim = edge is null ? null : new Pen(edge, 1);
            if (_rim?.CanFreeze == true) _rim.Freeze();
        }
        var outline = edge is not null && edge is not SolidColorBrush { Color.A: 0 };
        var key = (bounds.Size, CornerRadius, outline, ConnectionDepth);
        if (_contourKey != key)
        {
            _contourKey = key;
            _contour = ConnectionDepth > 0 ? ConnectedContour(bounds.Size, CornerRadius, ConnectionDepth)
                : SurfaceContour.Create(bounds.Size, CornerRadius, new Thickness(), outline ? .5 : 0);
        }
        if (!ViewportBounds.IsEmpty)
        {
            if (_clipBounds != ViewportBounds)
            { _clipBounds = ViewportBounds; _clip = new RectangleGeometry(_clipBounds); _clip.Freeze(); }
            drawing.PushClip(_clip!);
        }
        // Curved shoulders meet the chrome; the foot fills only the connected area of its rounded top.
        // Origin/scroll changes never rebuild the contour or measure a header.
        drawing.PushTransform(new TranslateTransform(bounds.X, bounds.Y));
        var press = Math.Clamp(PressProgress, 0, 1);
        drawing.PushTransform(new ScaleTransform(1 - .006 * press, 1 - .015 * press, bounds.Width / 2, bounds.Height));
        drawing.DrawGeometry(paint?.Fill ?? Background, _rim, _contour);
        if (CommandLight is { } light)
        {
            // Keep the same pointer coordinate as the attached toolbar while the face glides.
            light.Draw(drawing, _contour!, null, new Point(bounds.X, bounds.Y));
        }
        drawing.Pop();
        drawing.Pop();
        if (!ViewportBounds.IsEmpty) drawing.Pop();
    }

    private static Geometry ConnectedContour(Size size, CornerRadius radius, double depth)
    {
        var width = size.Width; var height = size.Height;
        var left = Math.Min(radius.TopLeft, Math.Min(width / 2, height / 2));
        var right = Math.Min(radius.TopRight, Math.Min(width / 2, height / 2));
        var shoulder = Math.Min(10, Math.Min(width / 4, height / 2));
        var crown = new StreamGeometry();
        using (var path = crown.Open())
        {
            path.BeginFigure(new(-shoulder, height), true, true);
            path.BezierTo(new(-shoulder * .45, height), new(0, height - shoulder * .45), new(0, height - shoulder), true, false);
            path.LineTo(new(0, left), true, false);
            if (left > 0) path.ArcTo(new(left, 0), new(left, left), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new(width - right, 0), true, false);
            if (right > 0) path.ArcTo(new(width, right), new(right, right), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new(width, height - shoulder), true, false);
            path.BezierTo(new(width, height - shoulder * .45), new(width + shoulder * .45, height), new(width + shoulder, height), true, false);
        }
        crown.Freeze();
        var connection = new GeometryGroup { FillRule = FillRule.Nonzero };
        connection.Children.Add(crown);
        // Overlap half a DIP so fractional-DPI antialiasing cannot open a join between the two shapes.
        connection.Children.Add(new RectangleGeometry(new Rect(0, height - .5, width, depth + .5)));
        connection.Freeze(); return connection;
    }
}
