using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>
/// Shows a small schedule dot on <see cref="CalendarDayButton"/>s whose date
/// appears in <see cref="ScheduledDatesProperty"/>.
/// </summary>
public static class CalendarScheduleMarkBehavior
{
    private const string ScheduleMarkName = "ScheduleMark";

    private sealed class State
    {
        public bool IsAttached;
        public EventHandler<SelectionChangedEventArgs>? SelectedDatesHandler;
        public EventHandler<CalendarDateChangedEventArgs>? DisplayDateHandler;
        public RoutedEventHandler? LoadedHandler;
        public HashSet<DateTime> Dates = new();
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(CalendarScheduleMarkBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ScheduledDatesProperty = DependencyProperty.RegisterAttached(
        "ScheduledDates",
        typeof(IEnumerable),
        typeof(CalendarScheduleMarkBehavior),
        new PropertyMetadata(null, OnScheduledDatesChanged));

    public static void SetScheduledDates(DependencyObject element, IEnumerable? value)
        => element.SetValue(ScheduledDatesProperty, value);

    public static IEnumerable? GetScheduledDates(DependencyObject element)
        => (IEnumerable?)element.GetValue(ScheduledDatesProperty);

    private static void OnScheduledDatesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Calendar calendar)
            return;

        var state = GetOrCreateState(calendar);
        state.Dates = ToDateSet(e.NewValue as IEnumerable);
        EnsureAttached(calendar, state);
        ScheduleRefresh(calendar);
    }

    private static void EnsureAttached(Calendar calendar, State state)
    {
        if (state.IsAttached)
            return;

        state.IsAttached = true;
        state.LoadedHandler = (_, _) => ScheduleRefresh(calendar);
        state.DisplayDateHandler = (_, _) => ScheduleRefresh(calendar);
        state.SelectedDatesHandler = (_, _) => ScheduleRefresh(calendar);

        calendar.Loaded += state.LoadedHandler;
        calendar.DisplayDateChanged += state.DisplayDateHandler;
        calendar.SelectedDatesChanged += state.SelectedDatesHandler;
    }

    private static State GetOrCreateState(Calendar calendar)
    {
        if (calendar.GetValue(StateProperty) is State existing)
            return existing;

        var state = new State();
        calendar.SetValue(StateProperty, state);
        return state;
    }

    private static void ScheduleRefresh(Calendar calendar)
    {
        calendar.Dispatcher.BeginInvoke(
            () => ApplyMarks(calendar),
            DispatcherPriority.Loaded);
    }

    private static void ApplyMarks(Calendar calendar)
    {
        if (calendar.GetValue(StateProperty) is not State state)
            return;

        foreach (var dayButton in EnumerateDayButtons(calendar))
        {
            dayButton.ApplyTemplate();
            if (dayButton.Template?.FindName(ScheduleMarkName, dayButton) is not FrameworkElement mark)
                continue;

            var hasSchedule = dayButton.DataContext is DateTime date && state.Dates.Contains(date.Date);
            mark.Visibility = hasSchedule ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static HashSet<DateTime> ToDateSet(IEnumerable? source)
    {
        var dates = new HashSet<DateTime>();
        if (source is null)
            return dates;

        foreach (var item in source)
        {
            if (item is DateTime dateTime)
                dates.Add(dateTime.Date);
            else if (item is DateOnly dateOnly)
                dates.Add(dateOnly.ToDateTime(TimeOnly.MinValue));
        }

        return dates;
    }

    private static IEnumerable<CalendarDayButton> EnumerateDayButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CalendarDayButton dayButton)
                yield return dayButton;

            foreach (var nested in EnumerateDayButtons(child))
                yield return nested;
        }
    }
}
