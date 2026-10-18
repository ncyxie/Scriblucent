using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Controls;

/// <summary>Two balanced reading columns become a single flow without rebuilding content.</summary>
public sealed class ResponsiveSectionPanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.Register(
        nameof(Breakpoint), typeof(double), typeof(ResponsiveSectionPanel),
        new FrameworkPropertyMetadata(760d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number > 0);
    public double Breakpoint { get => (double)GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveSectionPanel),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number >= 0);
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private int Columns(double width) => double.IsFinite(width) && width >= Breakpoint ? 2 : 1;
    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var width = (availableSize.Width - Gap * (columns - 1)) / columns;
        double[] heights = new double[columns];
        var index = 0;
        var desiredWidth = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
            heights[index++ % columns] += child.DesiredSize.Height + Gap;
            desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
        }
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : desiredWidth,
            Math.Max(0, heights.Max() - Gap));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double[] heights = new double[columns];
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var column = index++ % columns;
            child.Arrange(new Rect(column * (width + Gap), heights[column], width, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }
        return finalSize;
    }
}
