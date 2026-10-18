using Scriblucent.Models;

namespace Scriblucent.ViewModels;

/// <summary>Session-only recovery. Snapshots contain schedule metadata, never document content.</summary>
internal sealed class CalendarReminderActions(
    CalendarPlannerViewModel planner,
    Func<ScheduledItemType, Guid, CalendarScheduledItemViewModel?> resolve,
    Func<CalendarScheduledItemViewModel, IReadOnlyList<NoteScheduleEntry>> read,
    Action<CalendarScheduledItemViewModel, IEnumerable<NoteScheduleEntry>> write,
    Action refresh)
{
    private readonly record struct Value(DateTime At, string Note);
    private sealed record Change(ScheduledItemType Type, Guid Id, Value[] Before, Value[] After);
    private readonly List<Change> _history = [];
    internal int UndoCount => _history.Count;

    internal bool Move(CalendarScheduledItemViewModel item, DateTime date) => ChangeOccurrence(item, date.Date);
    internal bool Remove(CalendarScheduledItemViewModel item) => ChangeOccurrence(item, null);

    private bool ChangeOccurrence(CalendarScheduledItemViewModel item, DateTime? destination)
    {
        var owner = resolve(item.ItemType, item.OwnerId);
        if (owner is null || owner.IsDeleted || !SameOwner(owner, item)) return Stale();
        var entries = read(owner);
        var index = entries.ToList().FindIndex(entry => ReferenceEquals(entry, item.SourceEntry));
        if (index < 0 || entries[index].ScheduledAt != item.ScheduledAt || (entries[index].Note ?? "") != item.ScheduleNote) return Stale();
        if (destination == item.ScheduledAt.Date) return false;
        var before = Snapshot(entries);
        var next = entries.Select((entry, ordinal) => new NoteScheduleEntry
        {
            ScheduledAt = ordinal == index && destination.HasValue
                ? DateTime.SpecifyKind(destination.Value + entry.ScheduledAt.TimeOfDay, entry.ScheduledAt.Kind)
                : entry.ScheduledAt,
            Note = entry.Note ?? ""
        }).Where((_, ordinal) => destination.HasValue || ordinal != index).ToArray();
        write(owner, next);
        _history.Add(new(item.ItemType, item.OwnerId, before, Snapshot(read(owner))));
        if (_history.Count > 20) _history.RemoveAt(0);
        refresh();
        planner.ReportFeedback(destination.HasValue ? CalendarPlannerFeedback.Moved : CalendarPlannerFeedback.Removed,
            true, destination.HasValue ? destination.Value + item.ScheduledAt.TimeOfDay : default);
        return true;
    }

    internal bool Undo()
    {
        if (_history.Count == 0) return false;
        var change = _history[^1];
        var owner = resolve(change.Type, change.Id);
        if (owner is null || owner.IsDeleted || !Matches(Snapshot(read(owner)), change.After))
        {
            _history.RemoveAll(entry => entry.Type == change.Type && entry.Id == change.Id);
            refresh();
            planner.ReportFeedback(CalendarPlannerFeedback.UndoConflict, _history.Count > 0);
            return false;
        }
        write(owner, change.Before.Select(value => new NoteScheduleEntry { ScheduledAt = value.At, Note = value.Note }));
        _history.RemoveAt(_history.Count - 1);
        refresh();
        planner.ReportFeedback(CalendarPlannerFeedback.Restored, _history.Count > 0);
        return true;
    }

    internal void Dismiss()
    {
        _history.Clear();
        planner.ReportFeedback(CalendarPlannerFeedback.None, false);
    }

    private bool Stale()
    {
        refresh();
        planner.ReportFeedback(CalendarPlannerFeedback.Stale, _history.Count > 0);
        return false;
    }

    private static bool SameOwner(CalendarScheduledItemViewModel a, CalendarScheduledItemViewModel b) => a.ItemType switch
    {
        ScheduledItemType.Note => ReferenceEquals(a.Note, b.Note),
        ScheduledItemType.Flashcard => ReferenceEquals(a.FlashcardSet, b.FlashcardSet),
        ScheduledItemType.MindMap => ReferenceEquals(a.MindMap, b.MindMap),
        _ => ReferenceEquals(a.Quiz, b.Quiz)
    };
    private static Value[] Snapshot(IReadOnlyList<NoteScheduleEntry> entries) => entries.Select(entry => new Value(entry.ScheduledAt, entry.Note ?? "")).ToArray();
    // Ordering can change with language. Compare values and duplicate counts, independently of display order.
    private static bool Matches(Value[] a, Value[] b) => a.Length == b.Length &&
        a.OrderBy(value => value.At).ThenBy(value => value.Note, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(value => value.At).ThenBy(value => value.Note, StringComparer.Ordinal));
}
