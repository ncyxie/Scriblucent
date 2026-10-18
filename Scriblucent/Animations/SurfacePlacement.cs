using System.Windows;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>Validate paint placement once after arrangement. No persistent layout or frame observer.</summary>
internal sealed class SurfacePlacement
{
    private readonly FrameworkElement _paint;
    private readonly Func<FrameworkElement?> _face;
    private Point _offset;
    private TranslateTransform? _transform;
    private bool _armed;
    internal SurfacePlacement(FrameworkElement paint, Func<FrameworkElement?> face)
    {
        _paint = paint; _face = face;
        paint.Unloaded += (_, _) => Disarm();
    }
    private Point Offset => _face()?.TranslatePoint(new Point(), _paint) ?? new Point();
    internal Transform Transform
    {
        get
        {
            var offset = Offset;
            if (_transform is null || offset != _offset)
            { _offset = offset; _transform = new(offset.X, offset.Y); _transform.Freeze(); }
            return _transform;
        }
    }
    internal void Arm()
    {
        if (_armed) return;
        _armed = true; _paint.LayoutUpdated += Validate;
    }
    private void Disarm() { _armed = false; _paint.LayoutUpdated -= Validate; }
    private void Validate(object? sender, EventArgs e)
    {
        Disarm();
        // Child OnRender may run before WPF publishes its new visual offset. Redraw only
        // if that final placement differs; the validation of the redraw then disarms.
        if (Offset != _offset) _paint.InvalidateVisual();
    }
}
