using System.Windows;
using System.Windows.Controls;
namespace Scriblucent.Controls;
/// <summary>Shared planner body: a month rail and a stable agenda viewport.</summary>
public sealed class DashboardCalendarPanel : Panel
{
    internal const double StackBelowWidth = 616; // 640 including the shared 12 DIP body inset.
    internal const double Rail = 280, Gap = 16, WideHeight = 296, StackedHeight = 560, AgendaTop = 304;
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? StackBelowWidth : Math.Max(0, available.Width);
        var stacked = width < StackBelowWidth;
        var extra = CalendarPlannerHeaderPanel.NeedsWrap(stacked ? width : width - Rail - Gap, this) ? CalendarPlannerHeaderPanel.WrapExtraHeight : 0;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(new Size(stacked ? width : i == 0 ? Rail : Math.Max(0, width - Rail - Gap), stacked ? i == 0 ? 288 : 256 + extra : WideHeight + extra));
        return new Size(width, (stacked ? StackedHeight : WideHeight) + extra);
    }
    protected override Size ArrangeOverride(Size available)
    {
        var stacked = available.Width < StackBelowWidth;
        var extra = CalendarPlannerHeaderPanel.NeedsWrap(stacked ? available.Width : available.Width - Rail - Gap, this) ? CalendarPlannerHeaderPanel.WrapExtraHeight : 0;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(stacked ? new Rect(0, i == 0 ? 0 : AgendaTop, available.Width, i == 0 ? 288 : 256 + extra)
                : i == 0 ? new Rect(0, 0, Rail, WideHeight + extra) : new Rect(Rail + Gap, 0, Math.Max(0, available.Width - Rail - Gap), WideHeight + extra));
        return available;
    }
}
