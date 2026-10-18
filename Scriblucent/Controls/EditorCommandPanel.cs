using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Controls;

/// <summary>A command band stays on one line until the shared header explicitly gives it a full row.</summary>
public sealed class EditorCommandPanel : Panel
{
    public static readonly DependencyProperty CanWrapProperty = DependencyProperty.Register(nameof(CanWrap), typeof(bool),
        typeof(EditorCommandPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool CanWrap { get => (bool)GetValue(CanWrapProperty); set => SetValue(CanWrapProperty, value); }
    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, height = 0, width = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            var size = child.DesiredSize;
            if (CanWrap && x > 0 && x + size.Width > available.Width + .5) { width = Math.Max(width, x); x = 0; y += height; height = 0; }
            x += size.Width; height = Math.Max(height, size.Height);
        }
        return new Size(Math.Max(width, x), y + height);
    }
    protected override Size ArrangeOverride(Size available)
    {
        double x = 0, y = 0, height = 0;
        var row = new List<(UIElement Child, double X, Size Size)>();
        void ArrangeRow()
        {
            foreach (var item in row)
                item.Child.Arrange(new Rect(item.X, y + (height - item.Size.Height) / 2, item.Size.Width, item.Size.Height));
            row.Clear(); y += height; x = 0; height = 0;
        }
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            if (CanWrap && x > 0 && x + size.Width > available.Width + .5) ArrangeRow();
            row.Add((child, x, size));
            x += size.Width; height = Math.Max(height, size.Height);
        }
        ArrangeRow();
        return available;
    }
}
