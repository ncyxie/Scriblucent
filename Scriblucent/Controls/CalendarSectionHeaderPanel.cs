using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Controls;

/// <summary>Keep the section title/navigation and planning actions intact at compact widths.</summary>
public sealed class CalendarSectionHeaderPanel : Panel
{
    internal const double RowHeight = 36, Gap = 12;
    internal const double WrapBelowWidth = 420, WrapExtraHeight = 44;
    private double _left, _right;
    internal static bool NeedsWrap(double width, double left, double right)
        => width < Math.Max(WrapBelowWidth, left + Gap + right);
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, RowHeight));
        _left = InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Width : 0;
        _right = InternalChildren.Count > 1 ? InternalChildren[1].DesiredSize.Width : 0;
        var width = double.IsFinite(available.Width) ? Math.Max(0, available.Width) : _left + Gap + _right;
        return new(width, NeedsWrap(width, _left, _right) ? RowHeight + WrapExtraHeight : RowHeight);
    }
    protected override Size ArrangeOverride(Size available)
    {
        var wrapped = NeedsWrap(available.Width, _left, _right);
        if (InternalChildren.Count > 0) InternalChildren[0].Arrange(new Rect(0, 0, Math.Min(_left, available.Width), RowHeight));
        if (InternalChildren.Count > 1) InternalChildren[1].Arrange(new Rect(Math.Max(0, available.Width - _right), wrapped ? WrapExtraHeight : 0,
            Math.Min(_right, available.Width), RowHeight));
        return available;
    }
}
