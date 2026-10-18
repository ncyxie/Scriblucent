using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

/// <summary>A small local light, painted through the surface's own cached contour.</summary>
internal sealed class CommandSurfaceLight : Animatable
{
    internal static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double), typeof(CommandSurfaceLight), new PropertyMetadata(0d, Dirty));
    internal static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double), typeof(CommandSurfaceLight), new PropertyMetadata(0d, Dirty));
    internal static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(CommandSurfaceLight), new PropertyMetadata(0d, Dirty));
    internal static readonly DependencyProperty RadiusXProperty = DependencyProperty.Register(nameof(RadiusX), typeof(double), typeof(CommandSurfaceLight), new PropertyMetadata(170d, Dirty));
    internal static readonly DependencyProperty RadiusYProperty = DependencyProperty.Register(nameof(RadiusY), typeof(double), typeof(CommandSurfaceLight), new PropertyMetadata(120d, Dirty));
    private readonly WeakReference<FrameworkElement> _owner;
    private readonly WeakReference<FrameworkElement> _paint;
    private (Color Color, double Shoulder)? _palette;
    private RadialGradientBrush? _wash, _edge;
    private readonly TranslateTransform _position = new();
    private bool _paintWasReachable;
    internal CommandSurfaceLight(FrameworkElement owner, FrameworkElement paint)
    { _owner = new(owner); _paint = new(paint); }
    private CommandSurfaceLight(WeakReference<FrameworkElement> owner, WeakReference<FrameworkElement> paint)
    { _owner = owner; _paint = paint; }
    protected override Freezable CreateInstanceCore() => new CommandSurfaceLight(_owner, _paint);
    internal double X { get => (double)GetValue(XProperty); set => SetCurrentValue(XProperty, value); }
    internal double Y { get => (double)GetValue(YProperty); set => SetCurrentValue(YProperty, value); }
    internal double Strength { get => (double)GetValue(StrengthProperty); set => SetCurrentValue(StrengthProperty, value); }
    internal double RadiusX { get => (double)GetValue(RadiusXProperty); set => SetCurrentValue(RadiusXProperty, value); }
    internal double RadiusY { get => (double)GetValue(RadiusYProperty); set => SetCurrentValue(RadiusYProperty, value); }
    internal static Size Footprint(Size face) => new(Math.Clamp(face.Width * .65 + face.Height * .35, 18, 170),
        Math.Clamp(face.Height * .85 + Math.Min(face.Width, face.Height * 2) * .12, 18, 120));
    private static void Dirty(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((CommandSurfaceLight)target).Invalidate();
    internal void Invalidate()
    {
        if (!_paint.TryGetTarget(out var paint) || !_owner.TryGetTarget(out var owner)) return;
        var bounds = owner is TabSelectionSurface tab ? tab.Bounds : new Rect(owner.RenderSize);
        if (owner is InteractionSurface { PaintedWidth: var width } && double.IsFinite(width)) bounds.Width = Math.Min(bounds.Width, Math.Max(0, width));
        var reachable = Strength > .001 && new Rect(X - RadiusX, Y - RadiusY, RadiusX * 2, RadiusY * 2).IntersectsWith(bounds);
        // Shared chrome can contain many buttons. Distant faces need neither new
        // drawing commands nor repeated paint invalidation on every pointer move.
        if (reachable || _paintWasReachable) paint.InvalidateVisual();
        _paintWasReachable = reachable;
    }
    internal void Draw(DrawingContext drawing, Geometry contour, Geometry? rim, Point origin = default)
    {
        if (Strength <= .001 || !_owner.TryGetTarget(out var owner)) return;
        var color = CommandLightBehavior.GetLightColor(owner);
        var compact = MotionPolicy.Spatial && owner is InteractionSurface
            && SurfaceMaterial.GetKind(owner) != SurfaceMaterialKind.Filled
            && CommandLightBehavior.GetRole(owner) != CommandLightRole.Item;
        if (compact) color = Color.FromArgb((byte)Math.Min(255, Math.Round(color.A * 1.65)), color.R, color.G, color.B);
        var shoulder = compact ? .52 : .35;
        // Filled semantic colours receive a neutral sheen, preserving red/green/blue meaning.
        if (owner is InteractionSurface && SurfaceMaterial.GetKind(owner) == SurfaceMaterialKind.Filled)
            color = Color.FromArgb((byte)Math.Min(56, color.A + 12), 255, 255, 255);
        if (_palette != (color, shoulder))
        {
            _palette = (color, shoulder);
            RadialGradientBrush Brush(double gain)
            {
                Color Tone(double alpha) => Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * alpha), 0, 255), color.R, color.G, color.B);
                var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = new(0, 0), GradientOrigin = new(0, 0),
                    RadiusX = 170, RadiusY = 120, Transform = _position };
                brush.GradientStops.Add(new(Tone(gain), 0));
                brush.GradientStops.Add(new(Tone(gain * shoulder), .5));
                brush.GradientStops.Add(new(Tone(0), 1)); return brush;
            }
            _wash = Brush(1); _edge = Brush(compact ? 1.9 : 2.4);
        }
        if (_position.X != X - origin.X) _position.X = X - origin.X;
        if (_position.Y != Y - origin.Y) _position.Y = Y - origin.Y;
        _wash!.RadiusX = _edge!.RadiusX = Math.Max(1, RadiusX);
        _wash.RadiusY = _edge.RadiusY = Math.Max(1, RadiusY);
        drawing.PushOpacity(Math.Clamp(Strength, 0, 1));
        // A colour sample is data: light its rim without recolouring the sample.
        if (CommandLightBehavior.GetRole(owner) != CommandLightRole.Value) drawing.DrawGeometry(_wash, null, contour);
        if (rim is not null) drawing.DrawGeometry(_edge, null, rim);
        drawing.Pop();
    }
}
