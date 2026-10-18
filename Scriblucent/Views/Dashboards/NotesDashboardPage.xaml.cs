using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Views.Dashboards;

public partial class NotesDashboardPage : UserControl
{
    public NotesDashboardPage()
    {
        InitializeComponent();
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void DisbandGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DisbandGroupMenuItem_Click(sender, e);
    private void GroupBorder_DragLeave(object sender, DragEventArgs e)
        => Host?.GroupBorder_DragLeave(sender, e);
    private void GroupBorder_DragOver(object sender, DragEventArgs e)
        => Host?.GroupBorder_DragOver(sender, e);
    private void GroupBorder_Drop(object sender, DragEventArgs e)
        => Host?.GroupBorder_Drop(sender, e);
    private void GroupColorButton_Click(object sender, RoutedEventArgs e)
        => Host?.GroupColorButton_Click(sender, e);
    private void GroupColorMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.GroupColorMenuItem_Click(sender, e);
    private void GroupMenuButton_Click(object sender, RoutedEventArgs e)
        => Host?.GroupMenuButton_Click(sender, e);
    private void CreateNoteButton_Click(object sender, RoutedEventArgs e)
        => Host?.CreateNoteButton_Click(sender, e);
    private void ImportButton_Click(object sender, RoutedEventArgs e)
        => Host?.ImportButton_Click(sender, e);
    private void MoveCalendarDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarDownButton_Click(sender, e);
    private void MoveCalendarUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarUpButton_Click(sender, e);
    private void MoveDashboardContentDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentDownButton_Click(sender, e);
    private void MoveDashboardContentUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentUpButton_Click(sender, e);
    private void MoveGroupsDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveGroupsDownButton_Click(sender, e);
    private void MoveGroupsUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveGroupsUpButton_Click(sender, e);
    private void MoveUngroupedDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveUngroupedDownButton_Click(sender, e);
    private void MoveUngroupedUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveUngroupedUpButton_Click(sender, e);
    private void NotesScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.NotesScrollViewer_PreviewDragLeave(sender, e);
    private void NotesScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.NotesScrollViewer_PreviewDragOver(sender, e);
    private void NotesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => Host?.NotesScrollViewer_PreviewMouseWheel(sender, e);
    private void RecentNoteButton_Click(object sender, RoutedEventArgs e)
        => Host?.RecentNoteButton_Click(sender, e);
    private void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RenameGroupMenuItem_Click(sender, e);
    private void ToggleCalendarSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleCalendarSectionButton_Click(sender, e);
    private void ToggleGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleGroupsSectionButton_Click(sender, e);
    private void ToggleNotesDashboardSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleNotesDashboardSectionButton_Click(sender, e);
    private void ToggleRecentSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleRecentSectionButton_Click(sender, e);
    private void ToggleUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleUngroupedSectionButton_Click(sender, e);
    private void UngroupedDropZone_DragLeave(object sender, DragEventArgs e)
        => Host?.UngroupedDropZone_DragLeave(sender, e);
    private void UngroupedDropZone_DragOver(object sender, DragEventArgs e)
        => Host?.UngroupedDropZone_DragOver(sender, e);
    private void UngroupedDropZone_Drop(object sender, DragEventArgs e)
        => Host?.UngroupedDropZone_Drop(sender, e);
}
