using System.Windows;
using Scriblucent.Controls;
using Scriblucent.ViewModels;
using Scriblucent.Views;
namespace Scriblucent;
public partial class MainWindow
{
    private void CalendarPlanner_Request(object? sender, CalendarPlannerRequestEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        e.Handled = true; var item = e.Item;
        if (e.Action == CalendarPlannerAction.Today)
        {
            vm.CalendarPlanner.Mode = CalendarPlannerMode.Day; vm.CalendarPlanner.SelectedDate = DateTime.Today; vm.CalendarPlanner.DisplayedDate = DateTime.Today;
            if (FindDashboardName("CalendarSectionBodySurface") is CalendarPlannerView view) view.CalendarControl.DisplayMode = System.Windows.Controls.CalendarMode.Month;
            return;
        }
        if (e.Action == CalendarPlannerAction.Open && item is not null)
        {
            if (item.Note is not null) OpenNoteEditor(item.Note);
            else if (item.FlashcardSet is not null) OpenFlashcardSetEditor(vm, item.FlashcardSet);
            else if (item.MindMap is not null) OpenMindMapEditor(item.MindMap);
            else if (item.Quiz is not null) OpenQuizEditor(vm, item.Quiz);
            return;
        }
        if (e.Action == CalendarPlannerAction.Remove && item is not null)
        {
            vm.CalendarReminderActions.Remove(item); return;
        }
        if (e.Action == CalendarPlannerAction.Move && item is not null && e.DestinationDate is { } destination)
        { vm.CalendarReminderActions.Move(item, destination); return; }
        if (e.Action == CalendarPlannerAction.Undo) { vm.CalendarReminderActions.Undo(); return; }
        if (e.Action == CalendarPlannerAction.DismissRecovery) { vm.CalendarReminderActions.Dismiss(); return; }
        if (FindName("NoteSchedulePanelControl") is not NoteSchedulePanel panel) return;
        panel.ShowPlanner(e.Action == CalendarPlannerAction.Add ? vm.GetCalendarOwners() : [], item,
            e.Action == CalendarPlannerAction.Edit ? item?.SourceEntry : null, e.Date,
            (owner, entries, date) =>
            {
                vm.SetCalendarOwnerSchedules(owner, entries);
                if (vm.CalendarPlanner.FilterIndex != 0 && vm.CalendarPlanner.FilterIndex != (int)owner.ItemType + 1) vm.CalendarPlanner.FilterIndex = 0;
                vm.CalendarPlanner.Mode = CalendarPlannerMode.Day; vm.CalendarPlanner.SelectedDate = date; vm.CalendarPlanner.DisplayedDate = date;
            });
    }
}
