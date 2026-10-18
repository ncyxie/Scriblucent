using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Retains native calendar navigation; only the newly displayed grid settles.</summary>
public static class CalendarNavigationMotionBehavior
{
    public static readonly RoutedUICommand TodayCommand = new("Today", "Today", typeof(CalendarNavigationMotionBehavior));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CalendarNavigationMotionBehavior), new PropertyMetadata(false, Enabled));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(CalendarNavigationMotionBehavior));

    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);

    private sealed class State(Calendar calendar)
    {
        private DispatcherOperation? _pending;
        private FrameworkElement? _surface;
        private int _direction;
        private readonly CommandBinding _today = new(TodayCommand, (_, e) =>
        {
            calendar.DisplayMode = CalendarMode.Month;
            calendar.DisplayDate = DateTime.Today;
            calendar.SelectedDate = DateTime.Today;
            e.Handled = true;
        }, (_, e) =>
        {
            var today = DateTime.Today;
            e.CanExecute = calendar.IsEnabled && calendar.SelectionMode != CalendarSelectionMode.None
                && (calendar.DisplayDateStart is null || today >= calendar.DisplayDateStart.Value.Date)
                && (calendar.DisplayDateEnd is null || today <= calendar.DisplayDateEnd.Value.Date)
                && !calendar.BlackoutDates.Contains(today);
            e.Handled = true;
        });

        internal void Attach()
        {
            calendar.DisplayDateChanged += DateChanged;
            calendar.DisplayModeChanged += ModeChanged;
            calendar.CommandBindings.Add(_today);
            CommandManager.InvalidateRequerySuggested();
        }
        internal void Detach()
        {
            calendar.DisplayDateChanged -= DateChanged;
            calendar.DisplayModeChanged -= ModeChanged;
            calendar.CommandBindings.Remove(_today);
            _pending?.Abort(); _pending = null;
            if (_surface is { } surface)
            {
                SurfaceMotion.Stop(surface, MotionChannel.Presence);
                UiMotion.ChannelTranslate(surface, MotionChannel.Presence).X = 0;
                surface.Opacity = 1;
                _surface = null;
            }
        }
        private void DateChanged(object? sender, CalendarDateChangedEventArgs e)
        {
            if (e.AddedDate is not { } added || e.RemovedDate is not { } removed) return;
            if (added.Year == removed.Year && added.Month == removed.Month) return;
            Queue(added > removed ? 1 : -1);
        }
        private void ModeChanged(object? sender, CalendarModeChangedEventArgs e) => Queue(0);
        private void Queue(int direction)
        {
            _direction = direction;
            _pending?.Abort();
            _pending = calendar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Arrive);
        }
        private void Arrive()
        {
            _pending = null;
            if (!calendar.IsLoaded || UiMotion.SuppressChildEntrances) return;
            calendar.ApplyTemplate();
            if (calendar.Template.FindName("PART_CalendarItem", calendar) is not System.Windows.Controls.Primitives.CalendarItem item) return;
            item.ApplyTemplate();
            var part = calendar.DisplayMode == CalendarMode.Month ? "PART_MonthView" : "PART_YearView";
            if (item.Template.FindName(part, item) is not FrameworkElement surface) return;
            if (_surface is { } old)
            {
                SurfaceMotion.Stop(old, MotionChannel.Presence);
                UiMotion.ChannelTranslate(old, MotionChannel.Presence).X = 0;
                old.Opacity = 1;
            }
            if (calendar.DisplayMode == CalendarMode.Month && surface.Parent is FrameworkElement { Name: "MonthFrame" } frame) surface = frame;
            _surface = surface;
            var translate = UiMotion.ChannelTranslate(surface, MotionChannel.Presence);
            surface.Opacity = MotionPolicy.Quality == MotionQuality.Off ? 1 : .65;
            translate.X = MotionPolicy.Spatial ? _direction * 6 : 0;
            SurfaceMotion.AnimateTracks(surface, MotionChannel.Presence,
                [new(surface, UIElement.OpacityProperty, 1), new(translate, TranslateTransform.XProperty, 0, true)],
                220, UiMotion.InOutSine);
        }
    }

    private static void Enabled(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Calendar calendar) return;
        calendar.Loaded -= Loaded; calendar.Unloaded -= Unloaded;
        Unloaded(calendar, new());
        if (args.NewValue is true)
        {
            calendar.Loaded += Loaded; calendar.Unloaded += Unloaded;
            if (calendar.IsLoaded) Loaded(calendar, new());
        }
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var calendar = (Calendar)sender;
        if (calendar.GetValue(StateProperty) is State) return;
        var state = new State(calendar);
        calendar.SetValue(StateProperty, state);
        state.Attach();
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var calendar = (Calendar)sender;
        (calendar.GetValue(StateProperty) as State)?.Detach();
        calendar.ClearValue(StateProperty);
    }
}
