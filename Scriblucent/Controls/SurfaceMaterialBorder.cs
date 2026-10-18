using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Rendering;

namespace Scriblucent.Controls;

/// <summary>A native Border with one material fill: no replacement bindings, child wrappers or duplicate background.</summary>
public sealed class SurfaceMaterialBorder : Border
{
    private (Size Size, CornerRadius Radius, Thickness Frame)? _key;
    private Geometry? _inner, _frame, _edge, _openEdge;
    private readonly WorkspaceGradientTexture.Fill _workspace = new();

    public SurfaceMaterialBorder()
    {
        SetResourceReference(SurfaceMaterial.DarkProperty, "SurfaceMaterialIsDark");
        // Adopt scoped implicit Border styles without unresolved resource expressions on every unstyled face.
        Loaded += (_, _) =>
        {
            if (ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue && Style is null && TryFindResource(typeof(Border)) is Style)
                SetResourceReference(StyleProperty, typeof(Border));
        };
    }

    private void UpdateContour()
    {
        var frame = SurfaceContour.EffectiveFrame(this);
        var key = (RenderSize, CornerRadius, frame);
        if (_key == key) return;
        _key = key;
        var outer = SurfaceContour.Create(RenderSize, CornerRadius, frame);
        var size = new Size(Math.Max(0, ActualWidth - frame.Left - frame.Right), Math.Max(0, ActualHeight - frame.Top - frame.Bottom));
        var radius = new CornerRadius(
            Math.Max(0, CornerRadius.TopLeft - Math.Max(frame.Left, frame.Top) / 2),
            Math.Max(0, CornerRadius.TopRight - Math.Max(frame.Right, frame.Top) / 2),
            Math.Max(0, CornerRadius.BottomRight - Math.Max(frame.Right, frame.Bottom) / 2),
            Math.Max(0, CornerRadius.BottomLeft - Math.Max(frame.Left, frame.Bottom) / 2));
        Geometry Inner(double inset)
        {
            var shape = SurfaceContour.Create(size, radius, new Thickness(), inset).Clone();
            if (frame.Left != 0 || frame.Top != 0) shape.Transform = new TranslateTransform(frame.Left, frame.Top);
            shape.Freeze(); return shape;
        }
        _inner = Inner(0); _frame = SurfaceContour.Ring(outer, _inner); _edge = SurfaceContour.Ring(_inner, Inner(1));
        _openEdge = null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var material = SurfaceMaterial.Paint(Background, SurfaceMaterial.GetKind(this), SurfaceMaterial.IsDark(this), SurfaceMaterial.GetForeground(this));
        if (material is null) { base.OnRender(drawing); return; }
        UpdateContour();
        var fill = SurfaceMaterial.GetKind(this) == SurfaceMaterialKind.Workspace
            ? _workspace.Get(material.Colors, ActualHeight, VisualTreeHelper.GetDpi(this)) : material.Fill;
        var metrics = SurfaceContour.EffectiveFrame(this);
        var round = CornerRadius.TopLeft;
        if (metrics == new Thickness() && CornerRadius.TopRight == round && CornerRadius.BottomLeft == round && CornerRadius.BottomRight == round && ActualWidth >= 2 * round && ActualHeight >= 2 * round)
            drawing.DrawRoundedRectangle(fill, null, new Rect(RenderSize), round, round);
        else drawing.DrawGeometry(fill, null, _inner);
        if (metrics != new Thickness() && BorderBrush is not null) drawing.DrawGeometry(BorderBrush, null, _frame);
        if (SurfaceMaterial.GetKind(this) != SurfaceMaterialKind.Data)
        {
            // A raised neutral object's existing hairline is its lit perimeter, not a second parallel stroke.
            var native = BorderBrush is SolidColorBrush solid &&
                Math.Max(solid.Color.R, Math.Max(solid.Color.G, solid.Color.B)) - Math.Min(solid.Color.R, Math.Min(solid.Color.G, solid.Color.B)) <= 8;
            var frame = SurfaceContour.EffectiveFrame(this);
            var singleHairline = Math.Max(Math.Max(frame.Left, frame.Right), Math.Max(frame.Top, frame.Bottom)) is > 0 and <= 1.5;
            drawing.DrawGeometry(material.Edge, null, SurfaceMaterial.GetKind(this) == SurfaceMaterialKind.Raised && native && singleHairline ? _frame
                : SurfaceMaterial.GetOpenTop(this) ? _openEdge ??= SurfaceContour.WithoutTop(_edge!, _inner!.Bounds, CornerRadius) : _edge);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); _key = null; InvalidateVisual(); }
}
