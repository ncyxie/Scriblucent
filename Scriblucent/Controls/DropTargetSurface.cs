using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Temporary drop feedback borrows the target's contour, not the feedback border's stroke radius.</summary>
public sealed class DropTargetSurface : Border
{
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(nameof(Surface), typeof(Border), typeof(DropTargetSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) =>
        {
            if (e.NewValue is Border face)
            {
                SurfacePaint.BindOuterMargin((DropTargetSurface)d, face);
                ((DropTargetSurface)d).SetBinding(CornerRadiusProperty, new Binding(nameof(CornerRadius)) { Source = face });
            }
        }));
    public Border? Surface { get => (Border?)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    private (Size Size, CornerRadius Radius, Thickness Frame, double Stroke)? _key;
    private Geometry? _contour;
    private Geometry? _rim;
    private readonly SurfacePlacement _placement;
    public DropTargetSurface() => _placement = new(this, () => Surface);
    protected override Size ArrangeOverride(Size finalSize) { _placement.Arm(); return base.ArrangeOverride(finalSize); }
    protected override void OnRender(DrawingContext drawing)
    {
        var face = Surface;
        var radius = face?.CornerRadius ?? CornerRadius;
        var frame = face is null ? new Thickness() : SurfaceContour.EffectiveFrame(face);
        var stroke = BorderThickness.Left;
        var bounds = face?.RenderSize ?? RenderSize;
        var key = (bounds, radius, frame, stroke);
        if (_key != key)
        {
            _key = key; _contour = SurfaceContour.Create(bounds, radius, frame);
            _rim = SurfaceContour.Ring(_contour, SurfaceContour.Create(bounds, radius, frame, stroke));
        }
        drawing.PushTransform(_placement.Transform);
        if (face is not null && Behaviors.RoundedClipBehavior.GetIsEnabled(face)) drawing.DrawRectangle(Background, null, new Rect(bounds));
        else drawing.DrawGeometry(Background, null, _contour);
        if (stroke > 0) drawing.DrawGeometry(BorderBrush, null, _rim);
        drawing.Pop();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); BindingOperations.GetMultiBindingExpression(this, MarginProperty)?.UpdateTarget(); _key = null; InvalidateVisual(); }
}
