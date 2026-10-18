using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Localization;

namespace Scriblucent.Behaviors;

/// <summary>One retargetable selection plate and metadata-only date density marks.</summary>
public static class CalendarScheduleMarkBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(CalendarScheduleMarkBehavior), new PropertyMetadata(false, Enabled));
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty ScheduledDatesProperty = DependencyProperty.RegisterAttached("ScheduledDates", typeof(IEnumerable), typeof(CalendarScheduleMarkBehavior), new PropertyMetadata(null, Updated));
    public static void SetScheduledDates(DependencyObject value, IEnumerable? dates) => value.SetValue(ScheduledDatesProperty, dates);
    public static IEnumerable? GetScheduledDates(DependencyObject value) => (IEnumerable?)value.GetValue(ScheduledDatesProperty);
    public static readonly DependencyProperty DateCountsProperty = DependencyProperty.RegisterAttached("DateCounts", typeof(IReadOnlyDictionary<DateTime, int>), typeof(CalendarScheduleMarkBehavior), new PropertyMetadata(null, Updated));
    public static void SetDateCounts(DependencyObject value, IReadOnlyDictionary<DateTime, int>? counts) => value.SetValue(DateCountsProperty, counts);
    public static IReadOnlyDictionary<DateTime, int>? GetDateCounts(DependencyObject value) => (IReadOnlyDictionary<DateTime, int>?)value.GetValue(DateCountsProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(CalendarScheduleMarkBehavior));
    private static void Enabled(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not Calendar calendar) return;
        calendar.Loaded -= Loaded; calendar.Unloaded -= Unloaded; Unloaded(calendar, new());
        if (e.NewValue is true) { calendar.Loaded += Loaded; calendar.Unloaded += Unloaded; if (calendar.IsLoaded) Loaded(calendar, new()); }
    }
    private static void Updated(DependencyObject value, DependencyPropertyChangedEventArgs e) { if (value.GetValue(StateProperty) is State state) state.Queue(); }
    private static void Loaded(object sender, RoutedEventArgs e)
    { var calendar = (Calendar)sender; if (calendar.GetValue(StateProperty) is null) calendar.SetValue(StateProperty, new State(calendar)); }
    private static void Unloaded(object sender, RoutedEventArgs e)
    { var calendar = (Calendar)sender; (calendar.GetValue(StateProperty) as State)?.Dispose(); calendar.ClearValue(StateProperty); }
    private sealed class State : IDisposable
    {
        private readonly Calendar _calendar;
        private DispatcherOperation? _pending;
        private FrameworkElement? _plate;
        private DateTime? _month;
        private bool _placed;
        public State(Calendar calendar)
        { _calendar = calendar; calendar.SelectedDatesChanged += Selected; calendar.DisplayDateChanged += Navigated; calendar.DisplayModeChanged += Mode; calendar.SizeChanged += Resized; MotionPolicy.Changed += Policy; Queue(); }
        private void Selected(object? s, SelectionChangedEventArgs e) => Queue();
        private void Navigated(object? s, CalendarDateChangedEventArgs e) => Queue();
        private void Mode(object? s, CalendarModeChangedEventArgs e) => Queue();
        private void Resized(object s, SizeChangedEventArgs e) => Queue();
        private void Policy() => Queue();
        internal void Queue() { _pending?.Abort(); _pending = _calendar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Apply); }
        private void Apply()
        {
            _pending = null; if (!_calendar.IsLoaded) return;
            _calendar.ApplyTemplate(); if (_calendar.Template.FindName("PART_CalendarItem", _calendar) is not CalendarItem item) return;
            item.ApplyTemplate(); var month = item.Template.FindName("PART_MonthView", item) as Grid;
            _plate = item.Template.FindName("CalendarSelectionPlate", item) as FrameworkElement;
            if (month is null || _plate is null) return;
            var counts = GetDateCounts(_calendar);
            var dates = GetScheduledDates(_calendar)?.Cast<object>().OfType<DateTime>().Select(date => date.Date).ToHashSet();
            CalendarDayButton? selected = null;
            foreach (var day in month.Children.OfType<CalendarDayButton>())
            {
                day.ApplyTemplate(); if (day.DataContext is not DateTime date) continue;
                var count = counts?.GetValueOrDefault(date.Date) ?? (dates?.Contains(date.Date) == true ? 1 : 0);
                if (day.Template.FindName("ScheduleMark", day) is FrameworkElement mark)
                { mark.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed; mark.Width = 4 * Math.Min(4, count); }
                var description = date.ToString("D") + (count > 0 ? " \u00b7 " + string.Format(LocalizationService.GetString("CalendarReminderCount"), count) : "");
                day.ToolTip = description; System.Windows.Automation.AutomationProperties.SetHelpText(day, description);
                if (day.IsSelected) selected = day;
            }
            if (selected is null || _calendar.DisplayMode != CalendarMode.Month || month.ActualWidth <= 0)
            { SurfaceMotion.Stop(_plate); _plate.Visibility = Visibility.Hidden; _placed = false; return; }
            _plate.Visibility = Visibility.Visible; _plate.Opacity = selected.IsInactive ? .45 : 1;
            var target = selected.TranslatePoint(new Point((selected.ActualWidth - 32) / 2, (selected.ActualHeight - 32) / 2), (UIElement)month.Parent);
            var translate = UiMotion.ChannelTranslate(_plate, MotionChannel.Selection);
            var currentMonth = new DateTime(_calendar.DisplayDate.Year, _calendar.DisplayDate.Month, 1);
            var instant = !_placed || currentMonth != _month || UiMotion.SuppressChildEntrances;
            SurfaceMotion.AnimateTracks(_plate, MotionChannel.Selection,
                [new(translate, TranslateTransform.XProperty, target.X, true), new(translate, TranslateTransform.YProperty, target.Y, true)], 220, UiMotion.InOutSine, instant: instant);
            _placed = true; _month = currentMonth;
        }
        public void Dispose()
        {
            _pending?.Abort(); _calendar.SelectedDatesChanged -= Selected; _calendar.DisplayDateChanged -= Navigated; _calendar.DisplayModeChanged -= Mode; _calendar.SizeChanged -= Resized; MotionPolicy.Changed -= Policy;
            if (_plate is not null) SurfaceMotion.Stop(_plate);
        }
    }
}
