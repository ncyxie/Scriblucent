using System.Windows;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

namespace Scriblucent;

public partial class MainWindow
{
    internal void CreateNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanCreateDashboardItem: true }) return;
        CloseNotesDashboardChrome();
        EditorWorkspace.Create(EditorDocumentKind.Note);
    }

    private void DashboardQuickAddFailed(object? sender, EventArgs e)
    {
        CloseNotesDashboardChrome();
        ModernDialog.ShowError(this, LocalizationService.GetString("Error"), LocalizationService.GetString("QuickAddFailed"));
    }
}
