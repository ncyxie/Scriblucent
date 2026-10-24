using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace Scriblucent.Controls;

internal sealed class SketchInkPreview : FrameworkElement
{
    private StrokeCollection? _strokes;
    public void SetStrokes(StrokeCollection? strokes) { _strokes = strokes; InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing) => _strokes?.Draw(drawing);
}
