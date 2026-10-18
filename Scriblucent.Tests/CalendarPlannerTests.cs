using Scriblucent.Models;
using Scriblucent.ViewModels;
namespace Scriblucent.Tests;
[Collection("Layout reveal")]
public sealed class CalendarPlannerTests
{
    [Fact]
    public void Week_start_follows_culture_until_overridden_without_changing_planner_state()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var vm = new CalendarPlannerViewModel { SelectedDate = new DateTime(2026, 10, 21) };
            var notices = new List<string?>();
            vm.PropertyChanged += (_, e) => notices.Add(e.PropertyName);
            System.Globalization.CultureInfo.CurrentCulture = new("en-US");
            Assert.Equal(DayOfWeek.Sunday, vm.FirstDayOfWeek);
            System.Globalization.CultureInfo.CurrentCulture = new("lt-LT");
            vm.RefreshLanguage();
            Assert.Equal(DayOfWeek.Monday, vm.FirstDayOfWeek);
            Assert.Contains(nameof(vm.FirstDayOfWeek), notices);
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                vm.FirstDayOfWeekOverride = day;
                System.Globalization.CultureInfo.CurrentCulture = new("en-US");
                vm.RefreshLanguage();
                Assert.Equal(day, vm.FirstDayOfWeek);
            }
            vm.FirstDayOfWeekOverride = null;
            Assert.Equal(DayOfWeek.Sunday, vm.FirstDayOfWeek);
            vm.FirstDayOfWeekOverride = (DayOfWeek)99;
            Assert.Null(vm.FirstDayOfWeekOverride);
            Assert.Equal(new DateTime(2026, 10, 21), vm.SelectedDate);
            Assert.Equal(0, vm.IndexRevision);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Week_start_settings_round_trip_and_old_settings_keep_the_language_default()
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { CalendarFirstDayOfWeek = day });
            Assert.Equal(day, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.CalendarFirstDayOfWeek);
        }
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.CalendarFirstDayOfWeek);
        Assert.Equal(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
            AppSettings.ResolveCalendarFirstDayOfWeek((DayOfWeek)99));
    }

    private static CalendarScheduledItemViewModel Item(ScheduledItemType type, DateTime date, string title="A thought", NoteScheduleEntry? entry=null)
        => new() { ItemType=type, ScheduledAt=date, Title=title, SourceEntry=entry ?? new() { ScheduledAt=date } };
    [Fact]
    public void Upcoming_groups_today_and_future_and_filters_every_item_type()
    {
        var vm=new CalendarPlannerViewModel();
        vm.ReplaceIndex([Item(ScheduledItemType.Note,DateTime.Today.AddDays(-1)),Item(ScheduledItemType.Note,DateTime.Today),Item(ScheduledItemType.Flashcard,DateTime.Today.AddYears(2))]);
        vm.Mode = CalendarPlannerMode.Upcoming;
        Assert.Equal(2,vm.Rows.Count(row=>!row.IsHeading)); Assert.Equal(2,vm.Rows.Count(row=>row.IsHeading));
        vm.FilterIndex=2; Assert.Single(vm.DateCounts); Assert.Single(vm.Rows, row=>!row.IsHeading);
        Assert.Equal(ScheduledItemType.Flashcard,vm.Rows.Last().Item!.ItemType);
    }
    [Fact]
    public void Rapid_navigation_projects_cached_index_without_rebuilding_density()
    {
        var vm=new CalendarPlannerViewModel(); vm.ReplaceIndex(Enumerable.Range(0,10000).Select(i=>Item((ScheduledItemType)(i%4),DateTime.Today.AddDays(i%90).AddMinutes(i))).ToArray());
        var revision=vm.IndexRevision; var counts=vm.DateCounts;
        for(var i=0;i<100;i++) { vm.SelectedDate=DateTime.Today.AddDays(i%90); vm.Mode=(CalendarPlannerMode)(i%3); }
        Assert.Equal(revision,vm.IndexRevision); Assert.Same(counts,vm.DateCounts);
        vm.FilterIndex=4; Assert.Equal(revision,vm.IndexRevision); Assert.All(vm.DayItems,item=>Assert.Equal(ScheduledItemType.Quiz,item.ItemType));
    }
    [Fact]
    public void Replacing_metadata_preserves_duplicate_occurrences_and_current_selection()
    {
        var vm=new CalendarPlannerViewModel { SelectedDate=DateTime.Today.AddDays(5) };
        var entryA=new NoteScheduleEntry { ScheduledAt=vm.SelectedDate }; var entryB=new NoteScheduleEntry { ScheduledAt=vm.SelectedDate };
        var items=new[] { Item(ScheduledItemType.Note,vm.SelectedDate,entry:entryA),Item(ScheduledItemType.Note,vm.SelectedDate,entry:entryB) };
        vm.ReplaceIndex(items); var revision=vm.IndexRevision; vm.ReplaceIndex(items);
        Assert.Equal(revision,vm.IndexRevision); Assert.Equal(2,vm.DayItems.Count); Assert.Same(entryB,vm.DayItems[1].SourceEntry);
        vm.Mode = CalendarPlannerMode.Upcoming; vm.ReplaceIndex([items[0]]); Assert.Equal(CalendarPlannerMode.Upcoming,vm.Mode); Assert.Equal(DateTime.Today.AddDays(5),vm.SelectedDate);
    }
    [Fact]
    public void Library_chooser_does_not_read_note_content_and_legacy_reminders_are_retained()
    {
        var doc=new NoteDocument { Title="Legacy", ScheduledAt=DateTime.Today.AddHours(11),ScheduleNote="Keep this", Content="not a document" };
        var owner=new CalendarScheduledItemViewModel { ItemType=ScheduledItemType.Note,Note=new NoteCardViewModel(doc,_=>{}),Title=doc.Title,ScheduledAt=DateTime.Today };
        var schedules=MainViewModel.GetCalendarOwnerSchedules(owner);
        Assert.Single(schedules); Assert.Equal("Keep this",schedules[0].Note); Assert.Equal(doc.Id,owner.OwnerId);
    }
    [Fact]
    public void Shared_library_index_excludes_trash_preserves_every_type_and_ignores_dashboard_filters()
    {
        // Bypass the persistence-owning constructor: this test exercises only metadata projection.
        var vm=(MainViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        void Set(string field,object value) => typeof(MainViewModel).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(vm,value);
        var date=DateTime.Today.AddHours(10);
        var active=new NoteCardViewModel(new NoteDocument { Title="A note", ScheduledAt=date, Content="not a rich-text document" },_=>{});
        var trash=new NoteCardViewModel(new NoteDocument { Title="Trash", ScheduledAt=date, IsDeleted=true },_=>{});
        Set("<Notes>k__BackingField",new System.Collections.ObjectModel.ObservableCollection<NoteCardViewModel> { active,trash });
        Set("<FlashcardSets>k__BackingField",new System.Collections.ObjectModel.ObservableCollection<FlashcardSetViewModel> { new(new FlashcardSetDocument { Title="Cards",Schedules=[new() { ScheduledAt=date }] }) });
        Set("<MindMaps>k__BackingField",new System.Collections.ObjectModel.ObservableCollection<MindMapViewModel> { new(new MindMapDocument { Title="Map",Schedules=[new() { ScheduledAt=date }] }) });
        Set("_quizzes",new System.Collections.ObjectModel.ObservableCollection<QuizViewModel> { new(new QuizDocument { Title="Quiz",Schedules=[new() { ScheduledAt=date }] }) });
        vm.RefreshCalendarPlanner();
        Assert.Equal(4,vm.CalendarPlanner.DayItems.Count); Assert.All(vm.CalendarPlanner.DayItems,item=>Assert.False(item.IsDeleted));
        Assert.Equal(4,vm.GetCalendarOwners().Count);
        var revision=vm.CalendarPlanner.IndexRevision;
        vm.CalendarPlanner.FilterIndex=4; vm.CalendarPlanner.SelectedDate=DateTime.Today.AddDays(1); vm.RefreshCalendarPlanner();
        Assert.Empty(vm.CalendarPlanner.DayItems); Assert.Equal(revision,vm.CalendarPlanner.IndexRevision);
        vm.CalendarPlanner.SelectedDate=DateTime.Today; Assert.Equal(ScheduledItemType.Quiz,Assert.Single(vm.CalendarPlanner.DayItems).ItemType);
    }
    [Fact]
    public void Past_groups_newest_days_first_with_chronological_rows_and_neutral_semantics()
    {
        var vm = new CalendarPlannerViewModel();
        vm.ReplaceIndex([Item(ScheduledItemType.Note,DateTime.Today.AddDays(-3)),
            Item(ScheduledItemType.Quiz,DateTime.Today.AddDays(-1).AddHours(15)),
            Item(ScheduledItemType.Note,DateTime.Today.AddDays(-1).AddHours(9)),
            Item(ScheduledItemType.Note,DateTime.Today),Item(ScheduledItemType.Flashcard,DateTime.Today.AddDays(1))]);
        vm.Mode = CalendarPlannerMode.Past;
        Assert.Equal(new[] { DateTime.Today.AddDays(-1),DateTime.Today.AddDays(-3) },vm.Rows.Where(row=>row.IsHeading).Select(row=>row.Date));
        Assert.Equal(new[] { 9,15 },vm.Rows.Where(row=>!row.IsHeading && row.Date==DateTime.Today.AddDays(-1)).Select(row=>row.Item!.ScheduledAt.Hour));
        Assert.Equal(DateTime.Today,vm.AddDate);
        vm.FilterIndex=1; Assert.Equal(2,vm.Rows.Count(row=>!row.IsHeading)); Assert.Equal(CalendarPlannerMode.Past,vm.Mode);
        vm.SelectedDate=DateTime.Today.AddDays(-3); Assert.Equal(CalendarPlannerMode.Day,vm.Mode);
        Assert.Equal(vm.SelectedDate,vm.AddDate);
    }
    [Fact]
    public void Midnight_refresh_reprojects_past_and_upcoming_without_changing_mode_or_selection()
    {
        var vm = new CalendarPlannerViewModel { SelectedDate=DateTime.Today.AddDays(-1) };
        vm.ReplaceIndex([Item(ScheduledItemType.Note,DateTime.Today.AddDays(-1)),Item(ScheduledItemType.Note,DateTime.Today)]);
        vm.Mode=CalendarPlannerMode.Past;
        typeof(CalendarPlannerViewModel).GetField("_indexedToday",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(vm,DateTime.Today.AddDays(-1));
        var revision=vm.IndexRevision; vm.RefreshToday();
        Assert.Equal(revision,vm.IndexRevision); Assert.Equal(CalendarPlannerMode.Past,vm.Mode);
        Assert.Single(vm.Rows,row=>!row.IsHeading); Assert.Equal(DateTime.Today.AddDays(-1),vm.SelectedDate);
        vm.Mode=CalendarPlannerMode.Upcoming; Assert.Single(vm.Rows,row=>!row.IsHeading);
        Assert.Equal(DateTime.Today,vm.Rows.Last().Date);
    }
}
