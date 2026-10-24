using System.Windows;
using System.Windows.Controls;
namespace Scriblucent.Controls;
/// <summary>Shared planner body: a month rail and a stable agenda viewport.</summary>
public sealed class DashboardCalendarPanel : Panel
{
    internal const double StackBelowWidth = 616; // 640 including the shared 12 DIP body inset.
    internal const double Rail = 280, Gap = 16, WideHeight = 296;
    internal const double MonthWidth = 256, MonthHeight = 288, AgendaHeight = 256;
    internal static double MonthSlotHeight(double width) => MonthHeight * Math.Min(1, Math.Max(0, width) / MonthWidth);
    internal static double BodyHeight(double width, double extra) =>
        (width < StackBelowWidth ? MonthSlotHeight(width) + Gap + AgendaHeight : WideHeight) + extra;
    private CalendarPlannerHeaderPanel? _observedHeader;
    private CalendarPlannerHeaderPanel? AgendaHeader
    {
        get
        {
            if (_observedHeader is not null && InternalChildren.Count > 1
                && ReferenceEquals(_observedHeader.Parent, InternalChildren[1])) return _observedHeader;
            var header = InternalChildren.Count > 1 && InternalChildren[1] is Panel agenda
                ? agenda.Children.OfType<CalendarPlannerHeaderPanel>().FirstOrDefault() : null;
            if (!ReferenceEquals(header, _observedHeader))
            {
                if (_observedHeader is not null) _observedHeader.RequiredHeightChanged -= HeaderHeightChanged;
                _observedHeader = header;
                if (header is not null) header.RequiredHeightChanged += HeaderHeightChanged;
            }
            return header;
        }
    }
    // A bounded agenda Grid can absorb a taller header by shrinking its star row,
    // without changing its own DesiredSize. Explicitly refresh the body's extent.
    private void HeaderHeightChanged() => InvalidateMeasure();
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? StackBelowWidth : Math.Max(0, available.Width);
        var stacked = width < StackBelowWidth;
        var agendaWidth = stacked ? width : Math.Max(0, width - Rail - Gap);
        var header = AgendaHeader;
        // Use the actual header's typography/choice width instead of predicting it
        // from application defaults. Grid reuses this same Auto-row measurement.
        header?.Measure(new Size(agendaWidth, double.PositiveInfinity));
        var extra = header is not null ? header.RequiredHeight(agendaWidth) - 32
            : CalendarPlannerHeaderPanel.NeedsWrap(agendaWidth, this) ? CalendarPlannerHeaderPanel.WrapExtraHeight : 0;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(new Size(stacked ? width : i == 0 ? Rail : Math.Max(0, width - Rail - Gap), stacked ? i == 0 ? MonthSlotHeight(width) : AgendaHeight + extra : WideHeight + extra));
        return new Size(width, BodyHeight(width, extra));
    }
    protected override Size ArrangeOverride(Size available)
    {
        var stacked = available.Width < StackBelowWidth;
        var agendaWidth = stacked ? available.Width : Math.Max(0, available.Width - Rail - Gap);
        var extra = AgendaHeader is { } header ? header.RequiredHeight(agendaWidth) - 32
            : CalendarPlannerHeaderPanel.NeedsWrap(agendaWidth, this) ? CalendarPlannerHeaderPanel.WrapExtraHeight : 0;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(stacked ? new Rect(0, i == 0 ? 0 : MonthSlotHeight(available.Width) + Gap, available.Width, i == 0 ? MonthSlotHeight(available.Width) : AgendaHeight + extra)
                : i == 0 ? new Rect(0, 0, Rail, WideHeight + extra) : new Rect(Rail + Gap, 0, Math.Max(0, available.Width - Rail - Gap), WideHeight + extra));
        return available;
    }
}
