using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace Scriblucent.Services;

internal enum SketchTool { Pen, Marker, Eraser, Line, Arrow, Rectangle, Ellipse }

internal static class SketchShapes
{
    public static StrokeCollection Create(SketchTool tool, Point start, Point end, DrawingAttributes attributes, bool constrain)
    {
        var delta = end - start;
        if (constrain)
        {
            if (tool is SketchTool.Line or SketchTool.Arrow)
            {
                var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / (Math.PI / 4)) * Math.PI / 4;
                end = start + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
            }
            else
            {
                var side = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
                end = start + new Vector(Math.CopySign(side, delta.X), Math.CopySign(side, delta.Y));
            }
        }
        var result = new StrokeCollection();
        void Add(IEnumerable<Point> points)
        {
            var ink = new Stroke(new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y))), attributes.Clone());
            ink.DrawingAttributes.FitToCurve = false; ink.DrawingAttributes.IgnorePressure = true; result.Add(ink);
        }
        if (tool is SketchTool.Line or SketchTool.Arrow)
        {
            Add([start, end]);
            if (tool == SketchTool.Arrow && (end - start).Length > .1)
            {
                var direction = end - start; direction.Normalize();
                var length = Math.Min((end - start).Length * .35, Math.Max(12, attributes.Width * 4));
                var back = end - direction * length; var normal = new Vector(-direction.Y, direction.X) * length * .48;
                Add([back + normal, end, back - normal]);
            }
        }
        else
        {
            var bounds = new Rect(start, end);
            if (tool == SketchTool.Rectangle) Add([bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft, bounds.TopLeft]);
            else Add(Enumerable.Range(0, 97).Select(i => new Point(bounds.X + bounds.Width / 2 * (1 + Math.Cos(i * Math.PI / 48)),
                bounds.Y + bounds.Height / 2 * (1 + Math.Sin(i * Math.PI / 48)))));
        }
        return result;
    }
}
