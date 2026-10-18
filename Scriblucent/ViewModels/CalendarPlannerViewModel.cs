using System.ComponentModel;
using System.Globalization;
using Scriblucent.Localization;

namespace Scriblucent.ViewModels;

public enum CalendarPlannerMode { Day, Upcoming, Past }
internal enum CalendarPlannerFeedback { None, Moved, Removed, Restored, Stale, UndoConflict }

public sealed record CalendarAgendaRow(DateTime Date, CalendarScheduledItemViewModel? Item)
{
    public bool IsHeading => Item is null;
    public string DateLabel => Date.ToString(Date.Year == DateTime.Today.Year ? "dddd, dd MMM" : "dddd, dd MMM yyyy", CultureInfo.CurrentCulture);
}

/// <summary>Session-only planner state. Navigation projects metadata; it never decodes document content.</summary>
public sealed class CalendarPlannerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private DayOfWeek? _firstDayOfWeekOverride;
    public DayOfWeek? FirstDayOfWeekOverride
    {
        get => _firstDayOfWeekOverride;
        set
        {
            var normalized = value is { } day && Enum.IsDefined(day) ? value : null;
            if (_firstDayOfWeekOverride == normalized) return;
            _firstDayOfWeekOverride = normalized;
            Changed(nameof(FirstDayOfWeek));
        }
    }
    public DayOfWeek FirstDayOfWeek => Models.AppSettings.ResolveCalendarFirstDayOfWeek(_firstDayOfWeekOverride);
    private IReadOnlyList<CalendarScheduledItemViewModel> _index = [];
    private Dictionary<DateTime, CalendarScheduledItemViewModel[]> _days = new();
    private IReadOnlyList<CalendarAgendaRow> _upcomingRows = [];
    private IReadOnlyList<CalendarAgendaRow> _pastRows = [];
    private DateTime _indexedToday = DateTime.Today;
    private DateTime _selected = DateTime.Today, _displayed = DateTime.Today;
    private CalendarPlannerMode _mode;
    private CalendarPlannerFeedback _feedback;
    private DateTime _feedbackDate;
    private int _filter;
    public int IndexRevision { get; private set; }
    public DateTime SelectedDate { get => _selected; set { if (_selected == value.Date) return; _selected = value.Date; _mode = CalendarPlannerMode.Day; Project(); Changed(nameof(SelectedDate)); Changed(nameof(Mode)); } }
    public DateTime DisplayedDate { get => _displayed; set { if (_displayed == value.Date) return; _displayed = value.Date; Changed(nameof(DisplayedDate)); } }
    public CalendarPlannerMode Mode { get => _mode; set { if (_mode == value) return; if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _mode = value; Project(_indexedToday != DateTime.Today); Changed(nameof(Mode)); } }
    public int FilterIndex { get => _filter; set { var normalized = Math.Clamp(value, 0, 4); if (_filter == normalized) return; _filter = normalized; Project(true); Changed(nameof(FilterIndex)); } }
    internal static string[] FilterLabels => [L("CalendarAllTypes"), L("NotesDashboard"), L("FlashcardsDashboard"), L("MindMapsDashboard"), L("QuizzesDashboard")];
    public string[] Filters => FilterLabels;
    public IReadOnlyList<CalendarAgendaRow> Rows { get; private set; } = [];
    public IReadOnlyList<CalendarScheduledItemViewModel> DayItems { get; private set; } = [];
    public IReadOnlyDictionary<DateTime, int> DateCounts { get; private set; } = new Dictionary<DateTime, int>();
    public bool HasRows => Rows.Count > 0;
    public string Heading => Mode switch { CalendarPlannerMode.Upcoming => L("CalendarUpcoming"), CalendarPlannerMode.Past => L("CalendarPast"), _ => SelectedDate.ToString("dddd, dd MMM yyyy", CultureInfo.CurrentCulture) };
    public string Summary => string.Format(L("CalendarReminderCount"), Rows.Count(row => row.Item is not null));
    public string EmptyTitle => L(Mode switch { CalendarPlannerMode.Upcoming => "CalendarNothingUpcoming", CalendarPlannerMode.Past => "CalendarNothingPast", _ => "CalendarOpenDay" });
    public string EmptyDescription => L(Mode switch { CalendarPlannerMode.Upcoming => "CalendarUpcomingEmptyHint", CalendarPlannerMode.Past => "CalendarPastEmptyHint", _ => "CalendarNoSchedulesForDay" });
    public string EmptyActionText => L(Mode == CalendarPlannerMode.Past ? "CalendarViewUpcoming" : "CalendarAddSchedule");
    public DateTime AddDate => Mode == CalendarPlannerMode.Day ? SelectedDate : DateTime.Today;
    public bool HasFeedback => _feedback != CalendarPlannerFeedback.None;
    public bool FeedbackIsError => _feedback is CalendarPlannerFeedback.Stale or CalendarPlannerFeedback.UndoConflict;
    public bool CanUndo { get; private set; }
    public string FeedbackMessage => _feedback switch
    {
        CalendarPlannerFeedback.Moved => string.Format(L("CalendarReminderMoved"), _feedbackDate.ToString("dd MMM yyyy, HH:mm", CultureInfo.CurrentCulture)),
        CalendarPlannerFeedback.Removed => L("CalendarReminderRemoved"),
        CalendarPlannerFeedback.Restored => L("CalendarChangeUndone"),
        CalendarPlannerFeedback.Stale => L("CalendarStaleReminder"),
        CalendarPlannerFeedback.UndoConflict => L("CalendarUndoConflict"),
        _ => string.Empty
    };
    internal void ReportFeedback(CalendarPlannerFeedback feedback, bool canUndo, DateTime date = default)
    {
        _feedback = feedback; _feedbackDate = date; CanUndo = canUndo;
        Changed(nameof(CanUndo)); Changed(nameof(FeedbackIsError)); Changed(nameof(HasFeedback)); Changed(nameof(FeedbackMessage));
    }
    private static string L(string key) => LocalizationService.GetString(key);
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public void RefreshLanguage() { Project(true); Changed(nameof(Filters)); Changed(nameof(FeedbackMessage)); Changed(nameof(FirstDayOfWeek)); }
    public void ReplaceIndex(IReadOnlyList<CalendarScheduledItemViewModel> items)
    {
        if (_index.Count == items.Count && _index.Zip(items).All(pair => pair.First.ItemType == pair.Second.ItemType && pair.First.OwnerId == pair.Second.OwnerId
            && ReferenceEquals(pair.First.SourceEntry, pair.Second.SourceEntry) && pair.First.ScheduledAt == pair.Second.ScheduledAt
            && pair.First.Title == pair.Second.Title && pair.First.ScheduleNote == pair.Second.ScheduleNote)) return;
        _index = items.OrderBy(item => item.ScheduledAt).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        IndexRevision++; Project(true);
    }
    public void RefreshToday() { if (_indexedToday != DateTime.Today) Project(true); }
    private void Project(bool rebuild = false)
    {
        if (rebuild)
        {
            _indexedToday = DateTime.Today;
            var filtered = _index.Where(item => FilterIndex == 0 || (int)item.ItemType == FilterIndex - 1).ToArray();
            _days = filtered.GroupBy(item => item.ScheduledAt.Date).ToDictionary(group => group.Key, group => group.ToArray());
            DateCounts = _days.ToDictionary(pair => pair.Key, pair => pair.Value.Length);
            var future = new List<CalendarAgendaRow>();
            foreach (var (date, entries) in _days.Where(pair => pair.Key >= _indexedToday).OrderBy(pair => pair.Key))
            { future.Add(new(date, null)); future.AddRange(entries.Select(item => new CalendarAgendaRow(date, item))); }
            _upcomingRows = future;
            var past = new List<CalendarAgendaRow>();
            foreach (var (date, entries) in _days.Where(pair => pair.Key < _indexedToday).OrderByDescending(pair => pair.Key))
            { past.Add(new(date, null)); past.AddRange(entries.Select(item => new CalendarAgendaRow(date, item))); }
            _pastRows = past;
            Changed(nameof(DateCounts));
        }
        DayItems = _days.GetValueOrDefault(SelectedDate) ?? [];
        Rows = Mode switch { CalendarPlannerMode.Upcoming => _upcomingRows, CalendarPlannerMode.Past => _pastRows, _ => DayItems.Select(item => new CalendarAgendaRow(SelectedDate, item)).ToArray() };
        foreach (var name in new[] { nameof(Rows), nameof(DayItems), nameof(HasRows), nameof(Heading), nameof(Summary), nameof(EmptyTitle), nameof(EmptyDescription), nameof(EmptyActionText), nameof(AddDate) }) Changed(name);
    }
}
