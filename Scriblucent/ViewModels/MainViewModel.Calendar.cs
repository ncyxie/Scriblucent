namespace Scriblucent.ViewModels;
public partial class MainViewModel
{
    private CalendarPlannerViewModel? _calendarPlanner;
    public CalendarPlannerViewModel CalendarPlanner => _calendarPlanner ??= new() { FirstDayOfWeekOverride = _settings?.CalendarFirstDayOfWeek };
    public string SelectedCalendarWeekStart
    {
        get => _settings.CalendarFirstDayOfWeek?.ToString() ?? "Default";
        set
        {
            DayOfWeek? day = Enum.TryParse<DayOfWeek>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
            if (_settings.CalendarFirstDayOfWeek == day) return;
            _settings.CalendarFirstDayOfWeek = day;
            if (_calendarPlanner is not null) _calendarPlanner.FirstDayOfWeekOverride = day;
            OnPropertyChanged(nameof(SelectedCalendarWeekStart));
            SaveAppSettings();
        }
    }
    private CalendarReminderActions? _calendarReminderActions;
    internal CalendarReminderActions CalendarReminderActions => _calendarReminderActions ??= new(
        CalendarPlanner, ResolveCalendarOwner, GetCalendarOwnerSchedules, SetCalendarOwnerSchedules, RefreshCalendarPlanner);
    private CalendarScheduledItemViewModel? ResolveCalendarOwner(ScheduledItemType type, Guid id)
    {
        var note = type == ScheduledItemType.Note ? Notes.FirstOrDefault(owner => owner.Document.Id == id && !owner.Document.IsDeleted) : null;
        var set = type == ScheduledItemType.Flashcard ? FlashcardSets.FirstOrDefault(owner => owner.Document.Id == id && !owner.Document.IsDeleted) : null;
        var map = type == ScheduledItemType.MindMap ? MindMaps.FirstOrDefault(owner => owner.Document.Id == id && !owner.Document.IsDeleted) : null;
        var quiz = type == ScheduledItemType.Quiz ? Quizzes.FirstOrDefault(owner => owner.Document.Id == id && !owner.Document.IsDeleted) : null;
        if (note is null && set is null && map is null && quiz is null) return null;
        return new() { ItemType = type, ScheduledAt = DateTime.MinValue, Title = note?.Title ?? set?.Title ?? map?.Title ?? quiz!.Title,
            Note = note, FlashcardSet = set, MindMap = map, Quiz = quiz };
    }
    internal void RefreshCalendarPlanner() => RefreshCalendarScheduledNotes();
    public IReadOnlyList<CalendarScheduledItemViewModel> GetCalendarOwners() =>
        Notes.Where(owner => !owner.Document.IsDeleted).Select(owner => new CalendarScheduledItemViewModel { ItemType = ScheduledItemType.Note, Note = owner, Title = owner.Title, ScheduledAt = DateTime.MinValue })
        .Concat(FlashcardSets.Where(owner => !owner.Document.IsDeleted).Select(owner => new CalendarScheduledItemViewModel { ItemType = ScheduledItemType.Flashcard, FlashcardSet = owner, Title = owner.Title, ScheduledAt = DateTime.MinValue }))
        .Concat(MindMaps.Where(owner => !owner.Document.IsDeleted).Select(owner => new CalendarScheduledItemViewModel { ItemType = ScheduledItemType.MindMap, MindMap = owner, Title = owner.Title, ScheduledAt = DateTime.MinValue }))
        .Concat(Quizzes.Where(owner => !owner.Document.IsDeleted).Select(owner => new CalendarScheduledItemViewModel { ItemType = ScheduledItemType.Quiz, Quiz = owner, Title = owner.Title, ScheduledAt = DateTime.MinValue }))
        .OrderBy(owner => owner.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public static IReadOnlyList<Scriblucent.Models.NoteScheduleEntry> GetCalendarOwnerSchedules(CalendarScheduledItemViewModel owner)
        => owner.Note is not null ? GetEffectiveSchedules(owner.Note.Document)
            : owner.FlashcardSet is not null ? owner.FlashcardSet.Document.Schedules
            : owner.MindMap is not null ? owner.MindMap.Document.Schedules
            : owner.Quiz is not null ? owner.Quiz.Document.Schedules : [];
    public void SetCalendarOwnerSchedules(CalendarScheduledItemViewModel owner, IEnumerable<Scriblucent.Models.NoteScheduleEntry> entries)
    {
        if (owner.Note is not null) SetNoteSchedules(owner.Note, entries);
        else if (owner.FlashcardSet is not null) SetFlashcardSetSchedules(owner.FlashcardSet, entries);
        else if (owner.MindMap is not null) SetMindMapSchedules(owner.MindMap, entries);
        else if (owner.Quiz is not null) SetQuizSchedules(owner.Quiz, entries);
    }

}
