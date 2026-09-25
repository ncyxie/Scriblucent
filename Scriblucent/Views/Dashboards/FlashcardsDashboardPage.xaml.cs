using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Views.Dashboards;

public partial class FlashcardsDashboardPage : UserControl
{
    public FlashcardsDashboardPage()
    {
        InitializeComponent();
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void CalendarScheduleGearButton_Click(object sender, RoutedEventArgs e)
        => Host?.CalendarScheduleGearButton_Click(sender, e);
    private void CalendarScheduledItemCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Host?.CalendarScheduledItemCard_MouseLeftButtonUp(sender, e);
    private void CreateFlashcardsButton_Click(object sender, RoutedEventArgs e)
        => Host?.CreateFlashcardsButton_Click(sender, e);
    private void DashboardCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        => Host?.DashboardCalendar_SelectedDatesChanged(sender, e);
    private void DashboardGroupBorder_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDragLeave(sender, e);
    private void DashboardGroupBorder_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDragOver(sender, e);
    private void DashboardGroupBorder_PreviewDrop(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDrop(sender, e);
    private void DashboardGroupColorButton_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupColorButton_Click(sender, e);
    private void DashboardGroupColorMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupColorMenuItem_Click(sender, e);
    private void DashboardGroupMenuButton_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupMenuButton_Click(sender, e);
    private void DashboardListing_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Host?.DashboardListing_MouseLeftButtonUp(sender, e);
    private void DashboardListing_PreviewDragEnter(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragEnter(sender, e);
    private void DashboardListing_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragLeave(sender, e);
    private void DashboardListing_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragOver(sender, e);
    private void DashboardListing_PreviewDrop(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDrop(sender, e);
    private void DashboardListing_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Host?.DashboardListing_PreviewMouseLeftButtonDown(sender, e);
    private void DashboardListing_PreviewMouseMove(object sender, MouseEventArgs e)
        => Host?.DashboardListing_PreviewMouseMove(sender, e);
    private void DashboardScrollViewer_Drop(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_Drop(sender, e);
    private void DashboardScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_PreviewDragLeave(sender, e);
    private void DashboardScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_PreviewDragOver(sender, e);
    private void DashboardUngroupedDropZone_DragLeave(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_DragLeave(sender, e);
    private void DashboardUngroupedDropZone_DragOver(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_DragOver(sender, e);
    private void DashboardUngroupedDropZone_Drop(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_Drop(sender, e);
    private void DeleteFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DeleteFlashcardSetMenuItem_Click(sender, e);
    private void DisbandDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DisbandDashboardGroupMenuItem_Click(sender, e);
    private void DuplicateFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DuplicateFlashcardSetMenuItem_Click(sender, e);
    private void FlashcardDashboardActionsButton_Click(object sender, RoutedEventArgs e)
        => Host?.FlashcardDashboardActionsButton_Click(sender, e);
    private void FlashcardSetInfoMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.FlashcardSetInfoMenuItem_Click(sender, e);
    private void ImportButton_Click(object sender, RoutedEventArgs e)
        => Host?.ImportFlashcardsButton_Click(sender, e);
    private void ListingContextMenuButton_Click(object sender, RoutedEventArgs e)
        => Host?.ListingContextMenuButton_Click(sender, e);
    private void MoveCalendarDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarDownButton_Click(sender, e);
    private void MoveCalendarUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarUpButton_Click(sender, e);
    private void MoveDashboardContentDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentDownButton_Click(sender, e);
    private void MoveDashboardContentUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentUpButton_Click(sender, e);
    private void MoveDashboardGroupDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardGroupDownButton_Click(sender, e);
    private void MoveDashboardGroupUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardGroupUpButton_Click(sender, e);
    private void MoveFlashcardDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveFlashcardDashboardGroupsDownButton_Click(sender, e);
    private void MoveFlashcardDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveFlashcardDashboardGroupsUpButton_Click(sender, e);
    private void MoveFlashcardDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveFlashcardDashboardUngroupedDownButton_Click(sender, e);
    private void MoveFlashcardDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveFlashcardDashboardUngroupedUpButton_Click(sender, e);
    private void OpenFlashcardCombineMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenFlashcardCombineMenuItem_Click(sender, e);
    private void OpenFlashcardHistoryMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenFlashcardHistoryMenuItem_Click(sender, e);
    private void OpenFlashcardStatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenFlashcardStatisticsMenuItem_Click(sender, e);
    private void OpenFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenFlashcardSetMenuItem_Click(sender, e);
    private void RecentFlashcardSetButton_Click(object sender, RoutedEventArgs e)
        => Host?.RecentFlashcardSetButton_Click(sender, e);
    private void RemoveFlashcardSetFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RemoveFlashcardSetFromGroupMenuItem_Click(sender, e);
    private void RenameDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RenameDashboardGroupMenuItem_Click(sender, e);
    private void ScheduleFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ScheduleFlashcardSetMenuItem_Click(sender, e);
    private void StartDashboardMassSelectMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.StartDashboardMassSelectMenuItem_Click(sender, e);
    private void ToggleCalendarSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleCalendarSectionButton_Click(sender, e);
    private void ToggleFlashcardDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleFlashcardDashboardGroupsSectionButton_Click(sender, e);
    private void ToggleFlashcardDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleFlashcardDashboardUngroupedSectionButton_Click(sender, e);
    private void ToggleFlashcardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleFlashcardGroupsSectionButton_Click(sender, e);
    private void ToggleFlashcardSetPinMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleFlashcardSetPinMenuItem_Click(sender, e);
    private void ToggleRecentSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleRecentSectionButton_Click(sender, e);
}
