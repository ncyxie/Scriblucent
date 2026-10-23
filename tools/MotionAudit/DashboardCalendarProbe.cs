using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using Calendar = System.Windows.Controls.Calendar;
internal static partial class Program
{
    private static void ProbeCalendarWeekStart()
    {
        var report = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            var (window, vm, store) = LoadingWindow(theme, MotionPreference.Off, 24);
            try
            {
                window.Show(); WaitLoading(() => vm.IsActiveDashboardReady);
                vm.IsRecentSectionVisible = false; vm.IsCalendarFirst = true;
                vm.CalendarPlanner.SelectedDate = new DateTime(2026, 10, 21);
                vm.CalendarPlanner.DisplayedDate = new DateTime(2026, 10, 1);
                var settings = (SettingsPanel)window.FindName("SettingsPanelControl");
                // Suppress unrelated Settings initialization/save handlers in this isolated fixture.
                typeof(SettingsPanel).GetField("_isApplyingSettings", LoadingFlags)!.SetValue(settings, true);
                var choice = (ComboBox)settings.FindName("CalendarWeekStartBox");
                Require((string?)choice.SelectedValue == "Default", "Existing settings did not select the language default.");
                foreach (var day in Enum.GetValues<DayOfWeek>())
                {
                    choice.SelectedValue = day.ToString(); Pump(25);
                    Require(vm.SelectedCalendarWeekStart == day.ToString(), "Settings selection did not update its two-way binding.");
                    foreach (var kind in Enum.GetValues<EditorDocumentKind>())
                    {
                        SelectLoadingDashboard(vm, kind); WaitLoading(() => vm.IsActiveDashboardReady); Pump(25);
                        var page = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                        var view = ControlTree(page).OfType<CalendarPlannerView>().Single();
                        Require(view.CalendarControl.FirstDayOfWeek == day, $"{theme}/{kind}: wrong dashboard week start.");
                        window.UpdateLayout();
                        var firstColumn = ControlTree(view.CalendarControl).OfType<CalendarDayButton>()
                            .First(button => Grid.GetColumn(button) == 0 && button.DataContext is DateTime);
                        Require(((DateTime)firstColumn.DataContext).DayOfWeek == day, "Calendar date columns did not rearrange to match the preference.");
                        Require(((Calendar)view.FindName("MoveCalendar")).FirstDayOfWeek == day, $"{theme}/{kind}: move popup ignored week start.");
                    }
                    var schedule = (NoteSchedulePanel)window.FindName("NoteSchedulePanelControl");
                    typeof(NoteSchedulePanel).GetMethod("SetEntryEditorsFromNow", LoadingFlags)!.Invoke(schedule, null);
                    Require(((DatePicker)schedule.FindName("ScheduleDatePicker")).FirstDayOfWeek == day, "Reminder date picker ignored week start.");
                    AppSettingsService.Flush();
                    var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(store, "settings.json")))!;
                    Require(saved.CalendarFirstDayOfWeek == day, "Week start did not persist to settings.json.");
                }
                choice.SelectedValue = "Default";
                foreach (var language in new[] { "lt", "en" })
                {
                    vm.SelectedLanguage = language; Pump(25);
                    Require(vm.CalendarPlanner.FirstDayOfWeek == System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
                        "Language default did not follow a language change.");
                    settings.Visibility = Visibility.Visible;
                    var host = (FrameworkElement)settings.FindName("PanelMotionHost"); host.Opacity = 1; host.RenderTransform = Transform.Identity;
                    ((UIElement)settings.FindName("OverlayDim")).Opacity = 1;
                    foreach (var width in new[] { 1280d, 520d })
                    {
                        window.Width = width; window.UpdateLayout(); Pump(50);
                        var section = (FrameworkElement)settings.FindName("SettingsSectionWorkspace");
                        var viewer = (ScrollViewer)settings.FindName("SettingsScrollViewer");
                        viewer.ScrollToVerticalOffset(section.TranslatePoint(new Point(), (UIElement)viewer.Content).Y);
                        Pump(60); window.UpdateLayout();
                        Require(choice.ActualWidth >= 150 && choice.ActualHeight >= 36, "Week-start choice lost its usable target in compact Settings.");
                        var recent = (FrameworkElement)settings.FindName("RecentItemsLimitFieldBorder");
                        Require(choice.TranslatePoint(new Point(), settings).Y > recent.TranslatePoint(new Point(), settings).Y + recent.ActualHeight + 16,
                            "Calendar preference must have its own row below recent-items controls.");
                        SaveControlCapture((FrameworkElement)window.Content, $"week-start-{theme}-{language}-{width}");
                    }
                }
                choice.SelectedValue = "Saturday"; vm.SelectedLanguage = "lt";
                Require(vm.CalendarPlanner.FirstDayOfWeek == DayOfWeek.Saturday, "Language change overrode an explicit weekday.");
                Require(vm.CalendarPlanner.SelectedDate == new DateTime(2026, 10, 21), "Week-start edits changed the selected date.");
                AppSettingsService.Flush();
                typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
                var restored = new MainViewModel(AppSettingsService.Load(), loadPersistedContent: false, applyThemeAndCulture: false);
                Require(restored.SelectedCalendarWeekStart == "Saturday" && restored.CalendarPlanner.FirstDayOfWeek == DayOfWeek.Saturday,
                    "Reloading settings lost the explicit week start.");
                settings.DataContext = restored; Pump(30);
                Require((string?)choice.SelectedValue == "Saturday", "Reopening Settings did not select its persisted weekday.");
                report.Add($"PASS {theme}: all seven weekdays via the Settings binding, all four dashboards, move/reminder pickers, disk save/reload, language default/override and wide/compact localized Settings.");
            }
            finally { CloseLoadingWindow(window); }
        }
        File.WriteAllLines(Path.Combine(_output, "week-start-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }

    public sealed class CalendarDashboardFixture
    {
        public bool IsCalendarSectionVisible => true;
        public bool IsCalendarSectionExpanded => true;
        public bool IsCalendarFirst => true;
        public CalendarPlannerViewModel CalendarPlanner { get; } = new();
        private CalendarScheduledItemViewModel[] _owners = [];
        internal CalendarReminderActions Actions { get; }
        public CalendarDashboardFixture() => Actions = new(CalendarPlanner,
            (type,id)=>_owners.FirstOrDefault(owner=>owner.ItemType==type && owner.OwnerId==id),
            MainViewModel.GetCalendarOwnerSchedules, SetSchedules, Refresh);
        private static void SetSchedules(CalendarScheduledItemViewModel owner,IEnumerable<NoteScheduleEntry> entries)
        {
            var next=entries.Select(entry=>new NoteScheduleEntry { ScheduledAt=entry.ScheduledAt,Note=entry.Note })
                .OrderBy(entry=>entry.ScheduledAt).ThenBy(entry=>entry.Note,StringComparer.CurrentCultureIgnoreCase).ToList();
            if(owner.Note is { } note) { note.Document.Schedules=next; note.Document.ScheduledAt=next.FirstOrDefault()?.ScheduledAt; note.Document.ScheduleNote=next.FirstOrDefault()?.Note ?? ""; }
            else if(owner.FlashcardSet is { } set) set.Document.Schedules=next;
            else if(owner.MindMap is { } map) map.Document.Schedules=next;
            else owner.Quiz!.Document.Schedules=next;
        }
        private void Refresh() => CalendarPlanner.ReplaceIndex(_owners.SelectMany(owner=>MainViewModel.GetCalendarOwnerSchedules(owner)
            .Select(entry=>new CalendarScheduledItemViewModel { ItemType=owner.ItemType,Title=owner.Title,ScheduledAt=entry.ScheduledAt,
                ScheduleNote=entry.Note,SourceEntry=entry,Note=owner.Note,FlashcardSet=owner.FlashcardSet,MindMap=owner.MindMap,Quiz=owner.Quiz })).ToArray());
        public void Populate()
        {
            _owners = [
                new() { ItemType=ScheduledItemType.Note,Note=new(new NoteDocument { Title="Sketch the next chapter" },_=>{}),Title="Sketch the next chapter",ScheduledAt=DateTime.Today },
                new() { ItemType=ScheduledItemType.Flashcard,FlashcardSet=new(new FlashcardSetDocument { Title="Remember the details" }),Title="Remember the details",ScheduledAt=DateTime.Today },
                new() { ItemType=ScheduledItemType.MindMap,MindMap=new(new MindMapDocument { Title="Connect an idea" }),Title="Connect an idea",ScheduledAt=DateTime.Today },
                new() { ItemType=ScheduledItemType.Quiz,Quiz=new(new QuizDocument { Title="Check what you know" }),Title="Check what you know",ScheduledAt=DateTime.Today }
            ];
            for(var i=0;i<32;i++)
            {
                var owner=_owners[i%4];
                var date=DateTime.Today.AddDays(i<12 ? 0 : i<24 ? i-11 : -1-(i-24)/4).AddHours(8+i%12);
                var entry=new NoteScheduleEntry { ScheduledAt=date,Note=i%3==0 ? "" : "A useful reminder, with enough detail to recognise the task." };
                SetSchedules(owner,MainViewModel.GetCalendarOwnerSchedules(owner).Append(entry));
            }
            Refresh();
        }
        public void Handle(object? sender, CalendarPlannerRequestEventArgs e)
        {
            switch(e.Action)
            {
                case CalendarPlannerAction.Move when e.Item is not null && e.DestinationDate.HasValue: Actions.Move(e.Item,e.DestinationDate.Value); break;
                case CalendarPlannerAction.Remove when e.Item is not null: Actions.Remove(e.Item); break;
                case CalendarPlannerAction.Undo: Actions.Undo(); break;
                case CalendarPlannerAction.DismissRecovery: Actions.Dismiss(); break;
            }
        }
    }
    private static void ProbeDashboardCalendar()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>(); var quick = Environment.GetCommandLineArgs().Contains("--quick");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"; XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load("Scriblucent/MainWindow.xaml");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in quick ? new[] { "en" } : new[] { "en", "lt" })
        foreach (var mode in quick ? new[] { MotionPreference.Full } : new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language); MotionPolicy.Configure(mode, false);
            foreach (var dashboard in quick || Environment.GetCommandLineArgs().Contains("--shared-planner") ? new[] { "Notes" } : new[] { "Notes", "Flashcards", "Quizzes", "MindMaps" })
            {
                var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
                var section = new XElement(source.Descendants().Single(node => ((string?)node.Attribute(x + "Name"))?.EndsWith("CalendarSectionContainer") == true));
                var wrapper = new XElement(wpf + "Grid", new XAttribute("Margin", "16"), new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()),
                    new XElement(wpf + "Grid.RowDefinitions", new XElement(wpf + "RowDefinition", new XAttribute("Height", "Auto"))), section);
                StripFixtureEvents(wrapper); var content = Fragment(wrapper, source.Root!); var fixture = new CalendarDashboardFixture(); content.DataContext = fixture;
                var window = new Window { Content = content, Width = 1060, Height = 510, ShowActivated = false, ShowInTaskbar = false };
                window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
                try
                {
                    window.Show(); Pump(380); window.UpdateLayout(); var view = ControlTree(content).OfType<CalendarPlannerView>().Single(); var calendar = view.CalendarControl; view.Request += fixture.Handle;
                    var item = ControlTree(calendar).OfType<CalendarItem>().Single(); var month = (Grid)item.Template.FindName("PART_MonthView", item);
                    var suffix = $"{dashboard}-{theme}-{language}-{mode}";
                    Require(calendar.FirstDayOfWeek==System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,"Calendar weekday ordering did not follow the selected language.");
                    Require(Math.Abs(month.RowDefinitions[0].ActualHeight - 24) < 1 && month.RowDefinitions.Skip(1).All(row => Math.Abs(row.ActualHeight - 36) < 1), "V2 calendar rows clip.");
                    Require(ControlTree(month).OfType<TextBlock>().Count(label => !string.IsNullOrWhiteSpace(label.Text)) >= 7, "Weekday headings disappeared.");
                    var sectionVisual = (FrameworkElement)content.FindName(section.Attribute(x + "Name")!.Value);
                    SaveLocalControlCapture(sectionVisual, "empty-" + suffix);
                    fixture.Populate(); Pump(350); window.UpdateLayout();
                    var list = (ItemsControl)view.FindName("AgendaList"); var scroll = ControlTree(list).OfType<ScrollViewer>().First();
                    Require(scroll.ScrollableHeight > 0 && VirtualizingPanel.GetIsVirtualizing(list), "The agenda lost its bounded virtualized viewport.");
                    Require(ControlTree(list).OfType<DashboardTileHost>().All(host => host.PreserveLiveInputInBitmapMode), "Agenda cache would use obsolete row geometry.");
                    var plate = (FrameworkElement)item.Template.FindName("CalendarSelectionPlate", item);
                    Require(plate.IsVisible && plate.ActualWidth == 32 && plate.ActualHeight == 32, "Selected date plate missing.");
                    foreach (var day in month.Children.OfType<CalendarDayButton>().Where(day => day.DataContext is DateTime))
                    {
                        var label = ControlTree(day).OfType<CalendarDayLabel>().Single(); var marker = (FrameworkElement)day.Template.FindName("DayMarker", day);
                        var a = marker.TranslatePoint(new Point(16,16), day); var b = label.TranslatePoint(new Point(label.ActualWidth/2,label.ActualHeight/2),day);
                        Require((a-b).Length < .01, "Date glyph lost optical alignment.");
                        var count = fixture.CalendarPlanner.DateCounts.GetValueOrDefault(((DateTime)day.DataContext).Date);
                        var mark = (FrameworkElement)day.Template.FindName("ScheduleMark", day);
                        Require(mark.IsVisible == (count > 0) && (count == 0 || mark.Width == Math.Min(4,count)*4), "Schedule density is incorrect.");
                    }
                    var selectedDay = month.Children.OfType<CalendarDayButton>().Single(day => day.IsSelected);
                    Require((plate.TranslatePoint(new Point(16,16),month) - selectedDay.TranslatePoint(new Point(selectedDay.ActualWidth/2,selectedDay.ActualHeight/2),month)).Length < .1, "Selection plate is not centred on its date.");
                    fixture.CalendarPlanner.Mode = CalendarPlannerMode.Upcoming;
                    selectedDay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,selectedDay));
                    Require(fixture.CalendarPlanner.Mode == CalendarPlannerMode.Day,"Clicking an already selected date did not return to Day."); Pump(260);
                    SaveLocalControlCapture(sectionVisual, "day-" + suffix);
                    var hosts=ControlTree(list).OfType<DashboardTileHost>().Where(host=>host.IsVisible).ToArray();
                    foreach(var host in hosts) host.IsBitmapMode=true; Pump(80);
                    Require(hosts.All(host=>host.ActualWidth>0 && host.ActualHeight>0),"Cached agenda collapsed its geometry.");
                    SaveLocalControlCapture(sectionVisual,"cached-"+suffix);
                    foreach(var host in hosts) host.IsBitmapMode=false;
                    var revision = fixture.CalendarPlanner.IndexRevision;
                    fixture.CalendarPlanner.Mode = CalendarPlannerMode.Upcoming; Pump(350); window.UpdateLayout();
                    Require(fixture.CalendarPlanner.Rows.Count(row => row.IsHeading) > 1, "Upcoming did not group dates.");
                    SaveLocalControlCapture(sectionVisual, "upcoming-" + suffix);
                    fixture.CalendarPlanner.FilterIndex = 2; Pump(260);
                    Require(fixture.CalendarPlanner.Rows.Where(row => !row.IsHeading).All(row => row.Item!.ItemType == ScheduledItemType.Flashcard), "Filter leaked a different type.");
                    for (var i = 0; i < 15; i++) { fixture.CalendarPlanner.SelectedDate = DateTime.Today.AddDays(i % 3); fixture.CalendarPlanner.Mode = (CalendarPlannerMode)(i % 3); }
                    Require(fixture.CalendarPlanner.IndexRevision == revision, "Navigation rebuilt the schedule index.");
                    fixture.CalendarPlanner.FilterIndex = 0; fixture.CalendarPlanner.SelectedDate = DateTime.Today; fixture.CalendarPlanner.Mode = CalendarPlannerMode.Day;
                    window.Width = 610; window.Height = 810; Pump(350); window.UpdateLayout();
                    var body = (DashboardCalendarPanel)view.FindName("Body");
                    Require(body.Children[1].TranslatePoint(new Point(), body).Y >= 303, "Compact planner did not stack.");
                    Require(ControlTree(content).OfType<CalendarPlannerActions>().Single().ActualWidth < content.ActualWidth, "Planner heading actions overflow.");
                    SaveLocalControlCapture(sectionVisual, "narrow-" + suffix);
                    for (var i=0;i<12;i++) calendar.DisplayDate = DateTime.Today.AddMonths(i+1);
                    Pump(380); calendar.DisplayMode = CalendarMode.Year; Pump(280);
                    Require(((Grid)item.Template.FindName("PART_YearView", item)).IsVisible, "Native year picker broke.");
                    calendar.DisplayMode = CalendarMode.Decade; Pump(280); calendar.DisplayMode = CalendarMode.Month;
                    CalendarNavigationMotionBehavior.TodayCommand.Execute(null, calendar); Pump(300);
                    Require(calendar.SelectedDate == DateTime.Today, "Native Today command failed.");
                    CalendarPlannerRequestEventArgs? request = null; view.Request += (_, e) => request = e;
                    var open = ControlTree(list).OfType<Button>().First(button => button.Tag is CalendarScheduledItemViewModel && button.Content is StackPanel);
                    Require(open.InputHitTest(new Point(open.ActualWidth-3,open.ActualHeight/2)) is not null,"Reminder action has an unclickable blank area.");
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(request?.Action == CalendarPlannerAction.Open, "Agenda open action does not route.");
                    ProbeCalendarComfort(view,fixture,window,sectionVisual,suffix);
                    fixture.CalendarPlanner.FilterIndex=1; Pump(35);
                    window.Content=new Grid(); Pump(30); window.Content=content; Pump(300);
                    var agenda=(FrameworkElement)view.FindName("AgendaContent");
                    Require(agenda.Opacity==1 && Math.Abs(UiMotion.ChannelTranslate(agenda,MotionChannel.Presence).Y)<.01,"Interrupted agenda stayed in its transition pose after returning.");
                    Require(ControlTree(list).OfType<SurfaceMaterialBorder>().Where(face=>face.Tag is CalendarScheduledItemViewModel).All(face=>face.Opacity==1 && Math.Abs(UiMotion.ChannelTranslate(face,MotionChannel.Presence).Y)<.01),"Interrupted reminder entry stayed displaced after returning.");
                    checks.Add("PASS " + suffix + ": shape/optical geometry/density; empty/day/upcoming/past; filter; unchanged navigation index; native calendar; input-preserving cache; routed open; move/undo; scroll preservation; chooser cancel/commit/unload; narrow header wrapping.");
                }
                finally { window.Close(); Pump(80); }
            }
            ProbePlannerSchedule(theme + "-" + language + "-" + mode);
            checks.Add($"PASS schedule-{theme}-{language}-{mode}: exact duplicate edit; shared date picker; staged clear/Cancel; direct Add/Save; date/search/draft preservation; validation and stale/concurrent protection.");
            Pump(280); Require(!RenderTransitionClock.IsAnyRunning, "Planner left motion after unload.");
            Console.WriteLine($"PASS calendar v2 {theme}/{language}/{mode}"); File.WriteAllLines(Path.Combine(_output,"calendar-checks.txt"),checks);
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null) is null,"Calendar probe queued preferences.");
        File.WriteAllText(Path.Combine(_output,"README.txt"),"V2 compiled shared planner in four actual dashboard fragments, plus scheduling overlay. Synthetic metadata only; no user documents/preferences writes. Captures are four raster scales, not physical monitor FPS or DPI-transition measurements.\n");
    }
    private static void ProbePlannerSchedule(string suffix)
    {
        var date = DateTime.Today.AddDays(5).AddHours(11);
        var first = new NoteScheduleEntry { ScheduledAt = date, Note = "same" }; var second = new NoteScheduleEntry { ScheduledAt = date, Note = "same" };
        var owner = new CalendarScheduledItemViewModel { ItemType=ScheduledItemType.Note, Note = new NoteCardViewModel(new NoteDocument { Title="A thought for later", Schedules=[first,second] },_=>{}), Title="A thought for later", ScheduledAt=date, SourceEntry=second };
        var panel = new NoteSchedulePanel(); var window = new Window { Content=panel, Width=860, Height=720, ShowInTaskbar=false, ShowActivated=false }; window.Show();
        NoteScheduleEntry[]? saved = null;
        try
        {
            panel.ShowPlanner([], owner, second, date.Date, (_,entries,_)=>saved=entries.ToArray()); Pump(500); window.UpdateLayout();
            Require(panel.Language.IetfLanguageTag==System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag,"Reminder date bindings use the wrong language.");
            Require(panel.ScheduleItems.Count==2, "Duplicate reminder collapsed on opening.");
            var note=(TextBox)panel.FindName("ScheduleNoteTextBox"); note.Text="Edited exact occurrence"; Pump(80);
            SaveLocalControlCapture((FrameworkElement)panel.FindName("PanelCard"),"schedule-edit-"+suffix);
            var datePicker=(DatePicker)panel.FindName("ScheduleDatePicker"); datePicker.IsDropDownOpen=true; Pump(350);
            var popup=(Popup)datePicker.Template.FindName("PART_Popup",datePicker);
            var popupCalendar=ControlTree(popup.Child).OfType<Calendar>().Single();
            var popupDay=ControlTree(popupCalendar).OfType<CalendarDayButton>().First(day=>day.IsSelected);
            Require(((FrameworkElement)popupDay.Template.FindName("DayMarker",popupDay)).ActualWidth==32,"Date picker lost the shared date contour.");
            datePicker.IsDropDownOpen=false; Pump(80);
            InvokePrivate(panel,"SaveButton_Click"); Require(saved is { Length:2 } && saved.Count(entry=>entry.Note=="Edited exact occurrence")==1 && second.Note=="same", "Exact edit was not staged/preserved duplicates.");
            Pump(300); saved=null;
            panel.ShowPlanner([],owner,second,date.Date,(_,entries,_)=>saved=entries.ToArray()); Pump(350);
            InvokePrivate(panel,"ClearButton_Click"); InvokePrivate(panel,"CloseButton_Click"); Require(saved is null && owner.Note!.Document.Schedules.Count==2,"Cancel committed a draft clear.");
            Pump(300); panel.ShowPlanner([owner],null,null,date.Date,(_,entries,_)=>saved=entries.ToArray()); Pump(350);
            var picker=(ComboBox)panel.FindName("OwnerPicker"); picker.SelectedIndex=0;
            Require(((DatePicker)panel.FindName("ScheduleDatePicker")).SelectedDate==date.Date,"Add lost selected date.");
            note.Text="A new reminder"; InvokePrivate(panel,"SaveButton_Click"); Require(saved is { Length:3 },"Save ignored a first un-staged reminder for an already scheduled item.");
            Pump(300); panel.ShowPlanner([owner],null,null,date.Date,(_,entries,_)=>saved=entries.ToArray()); Pump(350);
            window.Width=550; window.Height=620; Pump(150); window.UpdateLayout();
            SaveLocalControlCapture((FrameworkElement)panel.FindName("PanelCard"),"schedule-picker-narrow-"+suffix);
            saved=null; InvokePrivate(panel,"SaveButton_Click");
            Require(saved is null && ((InlineValidationPresenter)panel.FindName("ReminderValidation")).IsError,"Missing content selection silently saved.");
            picker.SelectedIndex=0; InvokePrivate(panel,"AddScheduleButton_Click");
            var stagedCount=panel.ScheduleItems.Count;
            ((TextBox)panel.FindName("OwnerSearchTextBox")).Text="no matching title";
            Require(ReferenceEquals(picker.SelectedItem,owner) && panel.ScheduleItems.Count==stagedCount,"Searching discarded a selected owner's staged reminders.");
            Require(((DatePicker)panel.FindName("ScheduleDatePicker")).SelectedDate==date.Date,"Staging an add reset the chosen date.");
            InvokePrivate(panel,"CloseButton_Click"); Pump(300);
            saved=null; panel.ShowPlanner([],owner,new NoteScheduleEntry { ScheduledAt=date },date.Date,(_,entries,_)=>saved=entries.ToArray()); Pump(260);
            InvokePrivate(panel,"SaveButton_Click"); Require(saved is null && ((InlineValidationPresenter)panel.FindName("ReminderValidation")).IsError,"A stale occurrence overwrote current reminders.");
            InvokePrivate(panel,"CloseButton_Click"); Pump(260);
            panel.ShowPlanner([],owner,second,date.Date,(_,entries,_)=>saved=entries.ToArray()); Pump(260);
            owner.Note!.Document.Schedules.Add(new NoteScheduleEntry { ScheduledAt=date.AddDays(1) });
            InvokePrivate(panel,"SaveButton_Click"); Require(saved is null && ((InlineValidationPresenter)panel.FindName("ReminderValidation")).IsError,"Concurrent changes were overwritten.");
            InvokePrivate(panel,"CloseButton_Click"); Pump(260);
        }
        finally { window.Close(); Pump(100); }
    }
    private static void InvokePrivate(NoteSchedulePanel panel,string method) => typeof(NoteSchedulePanel).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[panel,new RoutedEventArgs()]);
    private static void ProbeCalendarComfort(CalendarPlannerView view,CalendarDashboardFixture fixture,Window window,FrameworkElement section,string suffix)
    {
        var planner=fixture.CalendarPlanner;
        var body=(FrameworkElement)view.FindName("Body");
        var list=(ItemsControl)view.FindName("AgendaList");
        var scroll=ControlTree(list).OfType<ScrollViewer>().First();
        var undo=(Button)view.FindName("UndoButton");
        Button More() => ControlTree(list).OfType<Button>().First(button=>button.Name=="ReminderMoreButton" && button.IsVisible);
        ContextMenu Menu(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(240);
            return (ContextMenu)typeof(CalendarPlannerView).GetField("_menu",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
        }
        void Choose()
        {
            var menu=Menu(More());
            var move=(MenuItem)menu.Items[1];
            ((MenuItem)move.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen=false; Pump(350);
        }
        planner.Mode=CalendarPlannerMode.Past; Pump(280);
        Require(planner.Rows.Where(row=>!row.IsHeading).All(row=>row.Date<DateTime.Today),"Past includes current/future entries.");
        Require(planner.Rows.Where(row=>row.IsHeading).Select(row=>row.Date).SequenceEqual(planner.Rows.Where(row=>row.IsHeading).Select(row=>row.Date).OrderDescending()),"Past date groups are not newest first.");
        Require(planner.AddDate==DateTime.Today,"Adding from Past defaults to an old date.");
        var modePlate=(FrameworkElement)view.FindName("ModePlate");
        Require(Math.Abs(UiMotion.ChannelTranslate(modePlate,MotionChannel.Selection).X-modePlate.ActualWidth*2)<.01,"Past selection plate is misaligned.");
        SaveLocalControlCapture(section,"past-"+suffix);
        planner.Mode=CalendarPlannerMode.Day; Pump(250);
        scroll.ScrollToVerticalOffset(80); Pump(140);
        var offset=scroll.VerticalOffset; var height=body.ActualHeight; var selected=planner.SelectedDate; var displayed=planner.DisplayedDate;
        var menu=Menu(More()); var moveTo=(MenuItem)menu.Items[1]; var original=((Button)menu.PlacementTarget).Tag as CalendarScheduledItemViewModel;
        ((MenuItem)moveTo.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen=false; Pump(300);
        Require(planner.Mode==CalendarPlannerMode.Day && planner.SelectedDate==selected && planner.DisplayedDate==displayed,"Quick move unexpectedly changed navigation.");
        Require(Math.Abs(scroll.VerticalOffset-offset)<1,"Quick move reset agenda scrolling.");
        Require(Math.Abs(body.ActualHeight-height)<.01 && planner.CanUndo,"Undo changed planner size or disappeared.");
        Require(MainViewModel.GetCalendarOwnerSchedules(original!).Any(entry=>entry.ScheduledAt.Date==DateTime.Today.AddDays(1) && entry.ScheduledAt.TimeOfDay==original!.ScheduledAt.TimeOfDay),"Tomorrow did not preserve time.");
        SaveLocalControlCapture(section,"recovery-"+suffix);
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250); Require(!planner.CanUndo,"Undo did not consume its inverse step.");
        var popup=(Popup)view.FindName("MovePopup"); var calendar=(Calendar)view.FindName("MoveCalendar");
        var confirm=(Button)view.FindName("ConfirmMoveButton");
        Choose(); Require(popup.IsOpen && !confirm.IsEnabled,"Move chooser failed to open or same-date move is enabled.");
        Require(popup.Child.InputHitTest(new Point(1,1)) is null,"Move popup shadow padding expanded its hit area.");
        var focused=System.Windows.Input.FocusManager.GetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(calendar));
        Require(focused is CalendarDayButton { IsSelected:true },"Move chooser did not focus its selected date.");
        var chosenFrom=calendar.SelectedDate!.Value; calendar.SelectedDate=chosenFrom.AddDays(4); Pump(280);
        Require(confirm.IsEnabled,"Move chooser ignores date selection.");
        SaveLocalControlCapture((FrameworkElement)view.FindName("MovePopupCard"),"move-chooser-"+suffix);
        popup.IsOpen=false; Pump(250); Require(fixture.Actions.UndoCount==0,"Cancelling move changed schedules.");
        Choose(); calendar.SelectedDate=chosenFrom.AddDays(4);
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(300);
        Require(!popup.IsOpen && fixture.Actions.UndoCount==1,"Confirm move failed or popup remained open.");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(220);
        fixture.Actions.Dismiss(); Pump(220);
        Require(!planner.HasFeedback && Math.Abs(body.ActualHeight-height)<.01,"Dismissing recovery resized the planner.");
        window.Width=380; window.Height=870; Pump(300); window.UpdateLayout();
        var header=(CalendarPlannerHeaderPanel)view.FindName("PlannerHeader"); var filter=(FrameworkElement)view.FindName("TypeFilter");
        Require(header.ActualHeight==72 && filter.TranslatePoint(new Point(),header).Y==40,"Narrow filter did not wrap cleanly.");
        var sectionHeader=ControlTree(section).OfType<CalendarSectionHeaderPanel>().Single();
        Require(sectionHeader.ActualHeight==80,"Narrow section actions did not wrap.");
        Require(sectionHeader.Children[1].TranslatePoint(new Point(),sectionHeader).X+sectionHeader.Children[1].RenderSize.Width<=sectionHeader.ActualWidth+.01,"Narrow Add reminder action is clipped.");
        SaveLocalControlCapture(section,"wrapped-"+suffix);
        window.Width=610; window.Height=810; Pump(250);
        Choose(); Require(popup.IsOpen,"Chooser did not reopen.");
        var content=window.Content; window.Content=new Grid(); Pump(240);
        Require(!popup.IsOpen,"Unloading left the move popup open.");
        window.Content=content; Pump(300);
    }
}
