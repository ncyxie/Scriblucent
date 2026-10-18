using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Scriblucent.Rendering;

/// <summary>Immutable paint instructions from an already laid-out card. Rasterization never
/// reads its controls, bindings, resources, or view model.</summary>
internal sealed record FrozenCardDrawing(DrawingGroup Drawing, double Width, double Height)
{
    internal static int CaptureAttempts, Unarranged, Unfreezable;
    internal const double Fringe = 17;

    internal static FrozenCardDrawing? Capture(FrameworkElement content)
    {
        using var timing = FlashcardScrollTrace.Measure(FlashcardScrollTrace.Kind.Capture, content);
        var builder = Builder.Create(content);
        if (builder is null) return null;
        while (!builder.Advance(double.PositiveInfinity)) { }
        return builder.Result;
    }

    // Freeze each node before yielding. A host validates its revision/layout before
    // every slice and cancels this builder if scrolling or interaction resumes.
    internal sealed class Builder
    {
        private readonly Stack<(Visual? Visual, DrawingGroup Group)> _work = new();
        private readonly DrawingGroup _root = new();
        private readonly double _width, _height;
        private bool _complete;
        internal FrozenCardDrawing? Result { get; private set; }
        private Builder(FrameworkElement content)
        {
            _width = content.ActualWidth; _height = content.ActualHeight;
            _work.Push((null, _root)); _work.Push((content, _root));
        }
        internal static Builder? Create(FrameworkElement content)
        {
            CaptureAttempts++;
            if (!content.IsArrangeValid || content.ActualWidth <= 0 || content.ActualHeight <= 0) { Unarranged++; return null; }
            return new Builder(content);
        }
        internal bool Advance(double budgetMs)
        {
            if (_complete) return true;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            do
            {
                var (visual, parent) = _work.Pop();
                if (visual is null)
                {
                    if (!parent.CanFreeze) { Unfreezable++; _work.Clear(); _complete = true; return true; }
                    parent.Freeze();
                }
                else
                {
                    var group = new DrawingGroup { Opacity = VisualTreeHelper.GetOpacity(visual) };
                    var offset = VisualTreeHelper.GetOffset(visual);
                    var matrix = VisualTreeHelper.GetTransform(visual)?.Value ?? Matrix.Identity;
                    matrix.Translate(offset.X, offset.Y);
                    var transform = new MatrixTransform(matrix); transform.Freeze(); group.Transform = transform;
                    if (VisualTreeHelper.GetClip(visual) is { } clip)
                    { var frozen = clip.CloneCurrentValue(); frozen.Freeze(); group.ClipGeometry = frozen; }
                    if (VisualTreeHelper.GetDrawing(visual) is { } own)
                    {
                        var frozen = own.CloneCurrentValue();
                        if (!frozen.CanFreeze) { Unfreezable++; _work.Clear(); _complete = true; return true; }
                        frozen.Freeze(); group.Children.Add(frozen);
                    }
                    parent.Children.Add(group); _work.Push((null, group));
                    // Push in reverse to preserve native painter order.
                    for (var i = VisualTreeHelper.GetChildrenCount(visual) - 1; i >= 0; i--)
                    {
                        if (VisualTreeHelper.GetChild(visual, i) is not Visual child) continue;
                        if (child is UIElement element && element.Visibility != Visibility.Visible) continue;
                        if (VisualTreeHelper.GetOpacity(child) <= 0) continue;
                        _work.Push((child, group));
                    }
                }
            } while (_work.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < budgetMs);
            if (_work.Count == 0) { _complete = true; Result = new(_root, _width, _height); }
            return _complete;
        }
    }

    internal SKBitmap Render(double dpi, out double heightDip)
    {
        heightDip = Height;
        var bitmap = new SKBitmap((int)Math.Ceiling((Width + Fringe * 2) * dpi),
            (int)Math.Ceiling((Height + Fringe * 2) * dpi), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)dpi); canvas.Translate((float)Fringe, (float)Fringe);
        using var replay = new Replay(canvas);
        try { replay.Draw(Drawing); }
        catch { bitmap.Dispose(); throw; }
        return bitmap;
    }

    private sealed class Replay(SKCanvas canvas) : IDisposable
    {
        private readonly Dictionary<BitmapSource, SKBitmap> _images = new();
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
        internal void Draw(Drawing drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    canvas.Save();
                    var m = group.Transform?.Value ?? Matrix.Identity;
                    canvas.Concat(new SKMatrix { ScaleX = (float)m.M11, SkewX = (float)m.M21, TransX = (float)m.OffsetX,
                        SkewY = (float)m.M12, ScaleY = (float)m.M22, TransY = (float)m.OffsetY, Persp2 = 1 });
                    if (group.ClipGeometry is { } clip) { using var path = Path(clip); canvas.ClipPath(path, antialias: true); }
                    if (group.Opacity < 1) { using var alpha = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * group.Opacity)) }; canvas.SaveLayer(alpha); }
                    foreach (var child in group.Children) Draw(child);
                    if (group.Opacity < 1) canvas.Restore();
                    canvas.Restore();
                    break;
                case GeometryDrawing geometry:
                    if (geometry.Brush is { } fill) PaintGeometry(geometry.Geometry, fill);
                    if (geometry.Pen is { Brush: { } stroke } pen && pen.Thickness > 0)
                        PaintGeometry(geometry.Geometry.GetWidenedPathGeometry(pen), stroke);
                    break;
                case GlyphRunDrawing glyph:
                    if (glyph.ForegroundBrush is { } foreground) PaintGeometry(glyph.GlyphRun.BuildGeometry(), foreground);
                    break;
                case ImageDrawing { ImageSource: BitmapSource source } image:
                    using (var paint = new SKPaint { IsAntialias = true })
                        canvas.DrawBitmap(Image(source), Rect(image.Rect), new SKSamplingOptions(SKFilterMode.Linear), paint);
                    break;
                default: throw new NotSupportedException($"Unsupported card paint: {drawing.GetType().Name}");
            }
        }
        private void PaintGeometry(Geometry geometry, Brush brush)
        {
            using var path = Path(geometry);
            using var paint = Paint(brush, geometry.Bounds);
            canvas.DrawPath(path, paint);
        }
        private static SKPath Path(Geometry geometry)
        {
            var flat = geometry.GetFlattenedPathGeometry(.08, ToleranceType.Absolute);
            using var path = new SKPathBuilder { FillType = flat.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
            foreach (var figure in flat.Figures)
            {
                path.MoveTo((float)figure.StartPoint.X, (float)figure.StartPoint.Y);
                foreach (var segment in figure.Segments)
                    if (segment is PolyLineSegment lines) foreach (var point in lines.Points) path.LineTo((float)point.X, (float)point.Y);
                    else if (segment is LineSegment line) path.LineTo((float)line.Point.X, (float)line.Point.Y);
                if (figure.IsClosed) path.Close();
            }
            return path.Detach();
        }
        private SKPaint Paint(Brush brush, Rect bounds)
        {
            var paint = new SKPaint { IsAntialias = true };
            SKColor Color(Color c) => new(c.R, c.G, c.B, (byte)Math.Round(c.A * brush.Opacity));
            SKPoint Point(Point p, BrushMappingMode mode) => mode == BrushMappingMode.Absolute ? new((float)p.X, (float)p.Y)
                : new((float)(bounds.Left + p.X * bounds.Width), (float)(bounds.Top + p.Y * bounds.Height));
            switch (brush)
            {
                case SolidColorBrush solid: paint.Color = Color(solid.Color); break;
                case LinearGradientBrush linear:
                    using (var shader = SKShader.CreateLinearGradient(Point(linear.StartPoint, linear.MappingMode), Point(linear.EndPoint, linear.MappingMode),
                        linear.GradientStops.Select(s => Color(s.Color)).ToArray(), linear.GradientStops.Select(s => (float)s.Offset).ToArray(), SKShaderTileMode.Clamp)) paint.Shader = shader;
                    break;
                case RadialGradientBrush radial:
                    // Pointer light is reset before capture. Retain any static elliptical material wash.
                    var center = Point(radial.Center, radial.MappingMode);
                    var rx = radial.RadiusX * (radial.MappingMode == BrushMappingMode.Absolute ? 1 : bounds.Width);
                    var ry = radial.RadiusY * (radial.MappingMode == BrushMappingMode.Absolute ? 1 : bounds.Height);
                    using (var shader = SKShader.CreateRadialGradient(new(0, 0), 1,
                        radial.GradientStops.Select(s => Color(s.Color)).ToArray(), radial.GradientStops.Select(s => (float)s.Offset).ToArray(), SKShaderTileMode.Clamp,
                        SKMatrix.CreateScaleTranslation((float)rx, (float)ry, center.X, center.Y))) paint.Shader = shader;
                    break;
                default: throw new NotSupportedException($"Unsupported card brush: {brush.GetType().Name}");
            }
            return paint;
        }
        private SKBitmap Image(BitmapSource source)
        {
            if (_images.TryGetValue(source, out var image)) return image;
            var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
            image = new SKBitmap(converted.PixelWidth, converted.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, image.GetPixels(), bytes.Length);
            _images.Add(source, image); return image;
        }
        private static SKRect Rect(Rect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }
}
