using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Views.Dashboards;

public partial class QuizzesDashboardPage : UserControl
{
    public QuizzesDashboardPage()
    {
        InitializeComponent();
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void CreateQuizButton_Click(object sender, RoutedEventArgs e)
        => Host?.CreateQuizButton_Click(sender, e);
    private void ImportButton_Click(object sender, RoutedEventArgs e)
        => Host?.ImportQuizButton_Click(sender, e);
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
    private void DeleteQuizMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DeleteQuizMenuItem_Click(sender, e);
    private void DisbandDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DisbandDashboardGroupMenuItem_Click(sender, e);
    private void DuplicateQuizMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DuplicateQuizMenuItem_Click(sender, e);
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
    private void MoveQuizDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveQuizDashboardGroupsDownButton_Click(sender, e);
    private void MoveQuizDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveQuizDashboardGroupsUpButton_Click(sender, e);
    private void MoveQuizDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveQuizDashboardUngroupedDownButton_Click(sender, e);
    private void MoveQuizDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveQuizDashboardUngroupedUpButton_Click(sender, e);
    private void OpenQuizMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenQuizMenuItem_Click(sender, e);
    private void QuizInfoMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.QuizInfoMenuItem_Click(sender, e);
    private void RecentQuizButton_Click(object sender, RoutedEventArgs e)
        => Host?.RecentQuizButton_Click(sender, e);
    private void RemoveQuizFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RemoveQuizFromGroupMenuItem_Click(sender, e);
    private void RenameDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RenameDashboardGroupMenuItem_Click(sender, e);
    private void ScheduleQuizMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ScheduleQuizMenuItem_Click(sender, e);
    private void StartDashboardMassSelectMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.StartDashboardMassSelectMenuItem_Click(sender, e);
    private void ToggleCalendarSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleCalendarSectionButton_Click(sender, e);
    private void ToggleQuizDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleQuizDashboardGroupsSectionButton_Click(sender, e);
    private void ToggleQuizDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleQuizDashboardUngroupedSectionButton_Click(sender, e);
    private void ToggleQuizGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleQuizGroupsSectionButton_Click(sender, e);
    private void ToggleQuizPinMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleQuizPinMenuItem_Click(sender, e);
    private void ToggleRecentSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleRecentSectionButton_Click(sender, e);
}
