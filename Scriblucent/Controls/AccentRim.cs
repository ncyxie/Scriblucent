using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>A field's focus paint follows its actual face, without changing its layout or stroke width.</summary>
public sealed class AccentRim : FrameworkElement
{
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(nameof(Surface), typeof(FrameworkElement), typeof(AccentRim), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(AccentRim), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(AccentRim), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(AccentRim), new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RadiusProperty = DependencyProperty.Register("Radius", typeof(CornerRadius), typeof(AccentRim), new FrameworkPropertyMetadata(new CornerRadius(10), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FrameProperty = DependencyProperty.Register("Frame", typeof(Thickness), typeof(AccentRim), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsRender));
    public FrameworkElement? Surface { get => (FrameworkElement?)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    private Border? _face;
    private (Size Size, CornerRadius Radius, Thickness Frame, double Stroke)? _key;
    private Geometry? _rim;
    private readonly SurfacePlacement _placement;
    public AccentRim()
    {
        _placement = new(this, () => _face);
        IsHitTestVisible = false; Focusable = false; Opacity = 0;
        SetResourceReference(BrushProperty, "AccentRimBrush");
        Loaded += (_, _) => { BindFace(); SetCurrentValue(OpacityProperty, IsActive ? 1d : 0d); };
        Unloaded += (_, _) => SurfaceMotion.Stop(this);
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var rim = (AccentRim)d;
        if (e.Property == SurfaceProperty) { if (rim.IsLoaded) rim.BindFace(); return; }
        if (!rim.IsLoaded) { rim.Opacity = rim.IsActive ? 1 : 0; return; }
        // Give keyboard focus an immediate readable foothold, then let the light arrive.
        if (rim.IsActive && rim.Opacity < .35) rim.SetCurrentValue(OpacityProperty, .35d);
        SurfaceMotion.Animate(rim, rim.IsActive ? 1 : 0, rim.IsActive ? 160 : 130, UiMotion.OutSine, false);
    }
    private void BindFace()
    {
        if (Surface is Control control) control.ApplyTemplate();
        _face = Surface as Border ?? (Surface is null ? null : SurfacePaint.FindBorder(Surface));
        if (_face is null) return;
        SetBinding(RadiusProperty, new Binding(nameof(Border.CornerRadius)) { Source = _face });
        SetBinding(FrameProperty, new Binding(nameof(Border.BorderThickness)) { Source = _face });
        _key = null; InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize) => new();
    protected override Size ArrangeOverride(Size finalSize) { _placement.Arm(); return base.ArrangeOverride(finalSize); }
    protected override void OnRender(DrawingContext drawing)
    {
        var radius = (CornerRadius)GetValue(RadiusProperty);
        var frame = _face is null ? (Thickness)GetValue(FrameProperty) : SurfaceContour.EffectiveFrame(_face);
        var bounds = _face?.RenderSize ?? RenderSize;
        var key = (bounds, radius, frame, Thickness);
        if (_key != key)
        {
            _key = key;
            _rim = SurfaceContour.Ring(SurfaceContour.Create(bounds, radius, frame), SurfaceContour.Create(bounds, radius, frame, Thickness));
        }
        drawing.PushTransform(_placement.Transform);
        drawing.DrawGeometry(Brush, null, _rim);
        drawing.Pop();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); _key = null; InvalidateVisual(); }
}
