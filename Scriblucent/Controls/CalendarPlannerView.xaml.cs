using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Localization;
using Scriblucent.ViewModels;

namespace Scriblucent.Controls;

public enum CalendarPlannerAction { Add, Open, Edit, Remove, Manage, Today, Move, Undo, DismissRecovery }
public sealed class CalendarPlannerRequestEventArgs(CalendarPlannerAction action, CalendarScheduledItemViewModel? item,
    DateTime date, DateTime? destinationDate = null) : RoutedEventArgs
{
    public CalendarPlannerAction Action { get; } = action;
    public CalendarScheduledItemViewModel? Item { get; } = item;
    public DateTime Date { get; } = date;
    public DateTime? DestinationDate { get; } = destinationDate;
}

public partial class CalendarPlannerView : UserControl
{
    public static readonly RoutedEvent RequestEvent = EventManager.RegisterRoutedEvent(nameof(Request), RoutingStrategy.Bubble,
        typeof(EventHandler<CalendarPlannerRequestEventArgs>), typeof(CalendarPlannerView));
    public event EventHandler<CalendarPlannerRequestEventArgs> Request { add => AddHandler(RequestEvent, value); remove => RemoveHandler(RequestEvent, value); }
    private CalendarPlannerViewModel? _observed;
    private int _rowOrdinal;
    private Window? _window;
    private ContextMenu? _menu;
    private DispatcherOperation? _restoreOperation, _chooserOperation;
    private CalendarScheduledItemViewModel? _moveItem;
    private Button? _moveLauncher;
    private bool _preservingView, _moveCommitted;
    public Calendar CalendarControl => PlannerCalendar;
    private CalendarPlannerViewModel? Planner => DataContext as CalendarPlannerViewModel;

    public CalendarPlannerView()
    {
        InitializeComponent();
        UiMotion.AttachPopupOpenAnimation(MovePopup);
        PlannerCalendar.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(DayInvoked), true);
        ModeHost.SizeChanged += (_, _) => PositionMode(true);
        MovePopupCard.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { MovePopup.IsOpen = false; e.Handled = true; } };
        Loaded += LoadedView; Unloaded += UnloadedView; DataContextChanged += Context;
    }
    private void Context(object sender, DependencyPropertyChangedEventArgs e) { if (IsLoaded) Observe(); }
    private void LoadedView(object s, RoutedEventArgs e)
    {
        Planner?.RefreshToday(); Observe();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Activated += WindowActivated;
    }
    private void WindowActivated(object? sender, EventArgs e) { if (IsVisible) Planner?.RefreshToday(); }
    private void UnloadedView(object s, RoutedEventArgs e)
    {
        if (_window is not null) _window.Activated -= WindowActivated;
        _window = null;
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = null;
        _restoreOperation?.Abort(); _restoreOperation = null;
        _chooserOperation?.Abort(); _chooserOperation = null;
        _moveCommitted = true; MovePopup.IsOpen = false; _moveItem = null; _moveLauncher = null;
        if (_menu is not null) _menu.IsOpen = false;
        _menu = null;
        SurfaceMotion.Stop(AgendaContent); AgendaContent.Opacity = 1;
        UiMotion.ChannelTranslate(AgendaContent, MotionChannel.Presence).Y = 0;
        SurfaceMotion.Stop(ModePlate); SurfaceMotion.Stop(FeedbackHost);
    }
    private void Observe()
    {
        RefreshCulture();
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = Planner;
        if (_observed is not null) _observed.PropertyChanged += Changed;
        PositionMode(true); PositionFeedback(true);
    }
    private void RefreshCulture()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        MovePopupCard.Language = Language;
    }
    private void Changed(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarPlannerViewModel.Filters)) RefreshCulture();
        if (e.PropertyName == nameof(CalendarPlannerViewModel.Mode)) PositionMode(false);
        if (e.PropertyName == nameof(CalendarPlannerViewModel.HasFeedback)) PositionFeedback(false);
        if (e.PropertyName == nameof(CalendarPlannerViewModel.FeedbackMessage) && Planner?.HasFeedback == true)
            UIElementAutomationPeer.CreatePeerForElement(FeedbackText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        if (e.PropertyName != nameof(CalendarPlannerViewModel.Rows)) return;
        _rowOrdinal = 0;
        if (_preservingView) return;
        RestoreView(0, null, -1);
        if (UiMotion.SuppressChildEntrances) return;
        SurfaceMotion.Stop(AgendaContent, MotionChannel.Presence); AgendaContent.Opacity = .75;
        var translate = UiMotion.ChannelTranslate(AgendaContent, MotionChannel.Presence); translate.Y = MotionPolicy.Spatial ? 4 : 0;
        SurfaceMotion.AnimateTracks(AgendaContent, MotionChannel.Presence,
            [new(AgendaContent, OpacityProperty, 1), new(translate, TranslateTransform.YProperty, 0, true)], 220, UiMotion.InOutSine);
    }
    private void PositionMode(bool instant)
    {
        var mode = Planner?.Mode ?? CalendarPlannerMode.Day;
        var width = ModeHost.ActualWidth > 0 ? ModeHost.ActualWidth / 3 : 80;
        ModePlate.Width = width;
        var move = UiMotion.ChannelTranslate(ModePlate, MotionChannel.Selection);
        SurfaceMotion.AnimateTracks(ModePlate, MotionChannel.Selection,
            [new(move, TranslateTransform.XProperty, (int)mode * width, true)], 220, UiMotion.InOutSine, instant: instant);
        DayButton.FontWeight = mode == CalendarPlannerMode.Day ? FontWeights.SemiBold : FontWeights.Normal;
        UpcomingButton.FontWeight = mode == CalendarPlannerMode.Upcoming ? FontWeights.SemiBold : FontWeights.Normal;
        PastButton.FontWeight = mode == CalendarPlannerMode.Past ? FontWeights.SemiBold : FontWeights.Normal;
    }
    private void PositionFeedback(bool instant)
    {
        var visible = Planner?.HasFeedback == true;
        if (visible) FeedbackHost.Visibility = Visibility.Visible;
        FeedbackHost.IsHitTestVisible = visible;
        SurfaceMotion.AnimateTracks(FeedbackHost, MotionChannel.Presence,
            [new(FeedbackHost, OpacityProperty, visible ? 1 : 0)], 140, UiMotion.InOutSine,
            () => { if (Planner?.HasFeedback != true) FeedbackHost.Visibility = Visibility.Hidden; }, instant: instant);
    }
    private void SetMode(CalendarPlannerMode mode) { if (Planner is { } planner) planner.Mode = mode; }
    private void DayInvoked(object s, RoutedEventArgs e) { if (e.OriginalSource is System.Windows.Controls.Primitives.CalendarDayButton) SetMode(CalendarPlannerMode.Day); }
    private void Date_Selected(object s, SelectionChangedEventArgs e) => SetMode(CalendarPlannerMode.Day);
    private void Day_Click(object s, RoutedEventArgs e) => SetMode(CalendarPlannerMode.Day);
    private void Upcoming_Click(object s, RoutedEventArgs e) => SetMode(CalendarPlannerMode.Upcoming);
    private void Past_Click(object s, RoutedEventArgs e) => SetMode(CalendarPlannerMode.Past);
    private Button ModeButton => Planner?.Mode switch { CalendarPlannerMode.Upcoming => UpcomingButton, CalendarPlannerMode.Past => PastButton, _ => DayButton };

    private void Send(CalendarPlannerAction action, CalendarScheduledItemViewModel? item = null,
        DateTime? destination = null, FrameworkElement? launcher = null)
    {
        var preserve = action is CalendarPlannerAction.Move or CalendarPlannerAction.Remove or CalendarPlannerAction.Undo;
        var scroll = Descendants(AgendaList).OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.VerticalOffset ?? 0;
        var index = item is null ? -1 : Planner?.Rows.ToList().FindIndex(row => ReferenceEquals(row.Item, item)) ?? -1;
        if (preserve && scroll is not null) SmoothScrollBehavior.Interrupt(scroll);
        _preservingView = preserve;
        try { RaiseEvent(new CalendarPlannerRequestEventArgs(action, item, Planner?.AddDate ?? DateTime.Today, destination) { RoutedEvent = RequestEvent }); }
        finally { _preservingView = false; }
        if (preserve) RestoreView(offset, launcher, index);
    }
    private void RestoreView(double offset, FrameworkElement? launcher, int rowIndex)
    {
        _restoreOperation?.Abort();
        _restoreOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreOperation = null;
            if (!IsLoaded) return;
            AgendaList.UpdateLayout();
            var scroll = Descendants(AgendaList).OfType<ScrollViewer>().FirstOrDefault();
            if (launcher is not null)
            {
                bool InViewport(FrameworkElement element)
                {
                    if (scroll is null || !element.IsDescendantOf(scroll)) return true;
                    var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
                    return bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight;
                }
                if (launcher.IsLoaded && launcher.IsVisible && launcher.IsEnabled && InViewport(launcher)) launcher.Focus();
                else
                {
                    var rows = Planner?.Rows.ToList();
                    var next = Descendants(AgendaList).OfType<Button>()
                        .Where(button => button.Name == "ReminderMoreButton" && button.IsVisible && button.Tag is CalendarScheduledItemViewModel && InViewport(button))
                        .OrderBy(button => Math.Abs((rows?.FindIndex(row => ReferenceEquals(row.Item, button.Tag)) ?? 0) - rowIndex))
                        .FirstOrDefault();
                    if (next is not null) next.Focus(); else ModeButton.Focus();
                }
            }
            // Apply after focus: native BringIntoView must not supersede the user's saved offset.
            if (scroll is not null)
            {
                var target = Math.Clamp(offset, 0, scroll.ScrollableHeight);
                scroll.ScrollToVerticalOffset(target);
                SmoothScrollBehavior.NotifyOffsetChanged(scroll, scroll.HorizontalOffset, target);
            }
        }));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void EmptyAction_Click(object s, RoutedEventArgs e) { if (Planner?.Mode == CalendarPlannerMode.Past) SetMode(CalendarPlannerMode.Upcoming); else Send(CalendarPlannerAction.Add); }
    private void Undo_Click(object s, RoutedEventArgs e) => Send(CalendarPlannerAction.Undo, launcher: UndoButton);
    private void Dismiss_Click(object s, RoutedEventArgs e) { Send(CalendarPlannerAction.DismissRecovery); ModeButton.Focus(); }
    private void Open_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement { Tag: CalendarScheduledItemViewModel item }) Send(CalendarPlannerAction.Open, item); }
    private void Edit_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement { Tag: CalendarScheduledItemViewModel item }) Send(CalendarPlannerAction.Edit, item); }
    private void More_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button { Tag: CalendarScheduledItemViewModel item } button) return;
        if (_menu is not null) _menu.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = button }; _menu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        MenuItem Entry(string key, Action action)
        {
            var entry = new MenuItem { Header = LocalizationService.GetString(key) };
            entry.Click += (_, _) => action(); return entry;
        }
        menu.Items.Add(Entry("CalendarEditSchedule", () => Send(CalendarPlannerAction.Edit, item)));
        var move = new MenuItem { Header = LocalizationService.GetString("CalendarMoveTo") };
        var today = Entry("PremiumToday", () => Send(CalendarPlannerAction.Move, item, DateTime.Today, button));
        today.IsEnabled = item.ScheduledAt.Date != DateTime.Today;
        var tomorrow = Entry("PremiumTomorrow", () => Send(CalendarPlannerAction.Move, item, DateTime.Today.AddDays(1), button));
        tomorrow.IsEnabled = item.ScheduledAt.Date != DateTime.Today.AddDays(1);
        move.Items.Add(today); move.Items.Add(tomorrow); move.Items.Add(new Separator());
        move.Items.Add(Entry("CalendarChooseMoveDate", () =>
        {
            _chooserOperation?.Abort();
            _chooserOperation = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _chooserOperation = null;
                if (IsLoaded && button.IsLoaded) ShowMoveChooser(item, button);
            }));
        }));
        menu.Items.Add(move); menu.Items.Add(new Separator());
        menu.Items.Add(Entry("CalendarRemoveSchedule", () => Send(CalendarPlannerAction.Remove, item, launcher: button)));
        menu.Items.Add(Entry("CalendarManageSchedules", () => Send(CalendarPlannerAction.Manage, item)));
        UiMotion.AttachContextMenuOpenAnimation(menu); menu.IsOpen = true;
    }
    private void ShowMoveChooser(CalendarScheduledItemViewModel item, Button launcher)
    {
        _moveItem = item; _moveLauncher = launcher; _moveCommitted = false;
        MovePopup.PlacementTarget = launcher;
        MoveReminderDetails.Text = item.TimeText + " \u00b7 " + item.Title;
        MoveReminderDetails.ToolTip = MoveReminderDetails.Text;
        MoveCalendar.SelectedDate = item.ScheduledAt.Date; MoveCalendar.DisplayDate = item.ScheduledAt.Date;
        MoveCalendar.DisplayMode = CalendarMode.Month; ConfirmMoveButton.IsEnabled = false;
        MovePopup.IsOpen = true;
    }
    private void MovePopup_Opened(object s, EventArgs e)
    {
        MoveCalendar.UpdateLayout();
        var selected = Descendants(MoveCalendar).OfType<System.Windows.Controls.Primitives.CalendarDayButton>().FirstOrDefault(day => day.IsSelected);
        if (selected is not null) selected.Focus(); else MoveCalendar.Focus();
    }
    private void MovePopup_Closed(object s, EventArgs e)
    {
        if (!_moveCommitted && IsLoaded) RestoreView(Descendants(AgendaList).OfType<ScrollViewer>().FirstOrDefault()?.VerticalOffset ?? 0, _moveLauncher, -1);
        _moveItem = null; _moveLauncher = null;
    }
    private void MoveDate_Selected(object s, SelectionChangedEventArgs e)
    { if (ConfirmMoveButton is not null) ConfirmMoveButton.IsEnabled = _moveItem is not null && MoveCalendar.SelectedDate.HasValue && MoveCalendar.SelectedDate.Value.Date != _moveItem.ScheduledAt.Date; }
    private void MoveCancel_Click(object s, RoutedEventArgs e) => MovePopup.IsOpen = false;
    private void MoveConfirm_Click(object s, RoutedEventArgs e)
    {
        if (_moveItem is null || MoveCalendar.SelectedDate is not { } date || date.Date == _moveItem.ScheduledAt.Date) return;
        _moveCommitted = true;
        Send(CalendarPlannerAction.Move, _moveItem, date.Date, _moveLauncher);
        MovePopup.IsOpen = false;
    }
    private void AgendaRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: not null } row) return;
        SurfaceMotion.Stop(row, MotionChannel.Presence);
        row.Opacity = 1; UiMotion.ChannelTranslate(row, MotionChannel.Presence).Y = 0;
    }
    private void AgendaRow_Loaded(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { DataContext: CalendarScheduledItemViewModel, IsVisible: true } row || !MotionPolicy.Spatial || UiMotion.SuppressChildEntrances || _rowOrdinal >= 3) return;
        if (row.Tag is not null) return; row.Tag = row.DataContext;
        row.Opacity = .8; var move = UiMotion.ChannelTranslate(row, MotionChannel.Presence); move.Y = 4;
        SurfaceMotion.AnimateTracks(row, MotionChannel.Presence, [new(row, OpacityProperty, 1), new(move, TranslateTransform.YProperty, 0, true)], 180, UiMotion.OutSine, startDelayMs: _rowOrdinal++ * 24);
    }
}
