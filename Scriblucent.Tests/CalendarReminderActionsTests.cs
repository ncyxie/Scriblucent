using Scriblucent.Models;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public sealed class CalendarReminderActionsTests
{
    private sealed class Library
    {
        public CalendarPlannerViewModel Planner { get; } = new();
        public List<CalendarScheduledItemViewModel> Owners { get; } = [];
        public CalendarReminderActions Actions { get; }
        public int Writes { get; private set; }
        public Library() => Actions = new(Planner, (type,id)=>Owners.FirstOrDefault(owner=>owner.ItemType==type && owner.OwnerId==id),
            MainViewModel.GetCalendarOwnerSchedules, Write, Refresh);
        public CalendarScheduledItemViewModel Add(ScheduledItemType type, params NoteScheduleEntry[] entries)
        {
            var owner = new CalendarScheduledItemViewModel
            {
                ItemType=type, Title="Study", ScheduledAt=DateTime.MinValue,
                Note=type==ScheduledItemType.Note ? new(new NoteDocument { Title="Study",Schedules=[..entries],Content="not a document" },_=>{}) : null,
                FlashcardSet=type==ScheduledItemType.Flashcard ? new(new FlashcardSetDocument { Title="Study",Schedules=[..entries] }) : null,
                MindMap=type==ScheduledItemType.MindMap ? new(new MindMapDocument { Title="Study",Schedules=[..entries] }) : null,
                Quiz=type==ScheduledItemType.Quiz ? new(new QuizDocument { Title="Study",Schedules=[..entries] }) : null
            };
            Owners.Add(owner); Refresh(); return owner;
        }
        public static CalendarScheduledItemViewModel Occurrence(CalendarScheduledItemViewModel owner, int index=0)
        {
            var entry=MainViewModel.GetCalendarOwnerSchedules(owner)[index];
            return new() { ItemType=owner.ItemType,Title=owner.Title,ScheduledAt=entry.ScheduledAt,ScheduleNote=entry.Note,
                SourceEntry=entry,Note=owner.Note,FlashcardSet=owner.FlashcardSet,MindMap=owner.MindMap,Quiz=owner.Quiz };
        }
        public void Write(CalendarScheduledItemViewModel owner,IEnumerable<NoteScheduleEntry> entries)
        {
            // Persistence-free fixture: production setters also normalize and replace entry references.
            var next=entries.Select(entry=>new NoteScheduleEntry { ScheduledAt=entry.ScheduledAt,Note=entry.Note.Trim() })
                .OrderBy(entry=>entry.ScheduledAt).ThenBy(entry=>entry.Note,StringComparer.CurrentCultureIgnoreCase).ToList();
            if(owner.Note is { } note) { note.Document.Schedules=next; note.Document.ScheduledAt=next.FirstOrDefault()?.ScheduledAt; note.Document.ScheduleNote=next.FirstOrDefault()?.Note ?? ""; }
            else if(owner.FlashcardSet is { } set) set.Document.Schedules=next;
            else if(owner.MindMap is { } map) map.Document.Schedules=next;
            else owner.Quiz!.Document.Schedules=next;
            Writes++;
        }
        private void Refresh() => Planner.ReplaceIndex(Owners.Where(owner=>!owner.IsDeleted)
            .SelectMany(owner=>MainViewModel.GetCalendarOwnerSchedules(owner).Select((_,i)=>Occurrence(owner,i))).ToArray());
    }
    [Theory]
    [InlineData(ScheduledItemType.Note)]
    [InlineData(ScheduledItemType.Flashcard)]
    [InlineData(ScheduledItemType.MindMap)]
    [InlineData(ScheduledItemType.Quiz)]
    public void Exact_duplicate_moves_preserve_time_description_and_view_and_undo_all_steps(ScheduledItemType type)
    {
        var library=new Library(); var at=DateTime.Today.AddDays(3).AddHours(11).AddMinutes(23).AddSeconds(17);
        var owner=library.Add(type,new NoteScheduleEntry() { ScheduledAt=at,Note="same" },new NoteScheduleEntry() { ScheduledAt=at,Note="same" });
        library.Planner.SelectedDate=at.Date; library.Planner.DisplayedDate=at.Date; library.Planner.FilterIndex=(int)type+1;
        library.Planner.Mode=CalendarPlannerMode.Upcoming;
        Assert.True(library.Actions.Move(Library.Occurrence(owner,1),DateTime.Today.AddDays(5)));
        var entries=MainViewModel.GetCalendarOwnerSchedules(owner);
        Assert.Equal(at,entries[0].ScheduledAt); Assert.Equal(at.TimeOfDay,entries[1].ScheduledAt.TimeOfDay); Assert.Equal("same",entries[1].Note);
        Assert.Equal(CalendarPlannerMode.Upcoming,library.Planner.Mode); Assert.Equal(at.Date,library.Planner.SelectedDate);
        Assert.Equal(at.Date,library.Planner.DisplayedDate); Assert.Equal((int)type+1,library.Planner.FilterIndex);
        Assert.True(library.Actions.Remove(Library.Occurrence(owner))); Assert.Single(MainViewModel.GetCalendarOwnerSchedules(owner));
        Assert.True(library.Actions.Undo()); Assert.True(library.Actions.Undo());
        Assert.Equal(2,MainViewModel.GetCalendarOwnerSchedules(owner).Count);
        Assert.All(MainViewModel.GetCalendarOwnerSchedules(owner),entry=>Assert.Equal(at,entry.ScheduledAt));
        Assert.False(library.Planner.CanUndo); Assert.Equal(4,library.Writes);
    }
    [Fact]
    public void No_op_and_stale_occurrences_do_not_write_or_create_history()
    {
        var library=new Library(); var owner=library.Add(ScheduledItemType.Note,new NoteScheduleEntry() { ScheduledAt=DateTime.Today.AddHours(9) });
        var item=Library.Occurrence(owner);
        Assert.False(library.Actions.Move(item,item.ScheduledAt.Date)); Assert.Equal(0,library.Writes);
        library.Write(owner,[new NoteScheduleEntry() { ScheduledAt=item.ScheduledAt,Note="Later edit" }]);
        Assert.False(library.Actions.Remove(item)); Assert.Equal(1,library.Writes); Assert.Equal(0,library.Actions.UndoCount);
        Assert.True(library.Planner.FeedbackIsError); Assert.Equal("Later edit",MainViewModel.GetCalendarOwnerSchedules(owner)[0].Note);
    }
    [Fact]
    public void Undo_conflict_preserves_later_edits_and_keeps_other_owners_recoverable()
    {
        var library=new Library(); var at=DateTime.Today.AddHours(9);
        var a=library.Add(ScheduledItemType.Note,new NoteScheduleEntry() { ScheduledAt=at });
        var b=library.Add(ScheduledItemType.Quiz,new NoteScheduleEntry() { ScheduledAt=at });
        library.Actions.Remove(Library.Occurrence(a)); library.Actions.Remove(Library.Occurrence(b));
        library.Write(b,[new NoteScheduleEntry() { ScheduledAt=at.AddDays(4),Note="Added elsewhere" }]);
        Assert.False(library.Actions.Undo()); Assert.Single(MainViewModel.GetCalendarOwnerSchedules(b));
        Assert.Equal("Added elsewhere",MainViewModel.GetCalendarOwnerSchedules(b)[0].Note);
        Assert.True(library.Actions.Undo()); Assert.Single(MainViewModel.GetCalendarOwnerSchedules(a));
    }
    [Fact]
    public void Deleted_owner_is_not_restored_and_dismiss_clears_session_history()
    {
        var library=new Library(); var owner=library.Add(ScheduledItemType.Note,new NoteScheduleEntry() { ScheduledAt=DateTime.Today });
        library.Actions.Remove(Library.Occurrence(owner)); owner.Note!.Document.IsDeleted=true;
        Assert.False(library.Actions.Undo()); Assert.Empty(owner.Note.Document.Schedules);
        library.Actions.Dismiss(); Assert.False(library.Planner.CanUndo); Assert.False(library.Planner.HasFeedback);
    }
    [Fact]
    public void Rapid_moves_keep_twenty_inverse_steps_and_never_modify_document_content()
    {
        var library=new Library(); var owner=library.Add(ScheduledItemType.Note,new NoteScheduleEntry() { ScheduledAt=DateTime.Today.AddHours(12) });
        for(var i=1;i<=25;i++) Assert.True(library.Actions.Move(Library.Occurrence(owner),DateTime.Today.AddDays(i)));
        Assert.Equal(20,library.Actions.UndoCount);
        for(var i=0;i<20;i++) Assert.True(library.Actions.Undo());
        Assert.False(library.Actions.Undo()); Assert.Equal(DateTime.Today.AddDays(5).AddHours(12),MainViewModel.GetCalendarOwnerSchedules(owner)[0].ScheduledAt);
        Assert.Equal("not a document",owner.Note!.Document.Content);
    }
    [Fact]
    public void Undo_ignores_display_order_but_preserves_duplicate_multiplicity()
    {
        var library=new Library(); var at=DateTime.Today.AddHours(9);
        var owner=library.Add(ScheduledItemType.Quiz,new NoteScheduleEntry() { ScheduledAt=at,Note="a" },new NoteScheduleEntry() { ScheduledAt=at,Note="a" },new NoteScheduleEntry() { ScheduledAt=at,Note="b" });
        library.Actions.Remove(Library.Occurrence(owner,1)); owner.Quiz!.Document.Schedules.Reverse();
        Assert.True(library.Actions.Undo()); Assert.Equal(2,owner.Quiz.Document.Schedules.Count(entry=>entry.Note=="a"));
    }
}
