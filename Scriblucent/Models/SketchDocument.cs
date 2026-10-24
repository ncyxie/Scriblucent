using System.Windows.Ink;
using System.Windows.Media;

namespace Scriblucent.Models;

internal sealed class SketchDocument
{
    public double Width { get; init; } = 640;
    public double Height { get; init; } = 400;
    public int PixelWidth { get; init; } = 1280;
    public int PixelHeight { get; init; } = 800;
    public Color? Background { get; set; } = Colors.White;
    public StrokeCollection Strokes { get; } = new();
    public byte[]? BaseImage { get; init; }
}
