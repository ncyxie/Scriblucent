using System.Windows;
using Scriblucent.ViewModels;

namespace Scriblucent;

public partial class MainWindow
{
    private void DashboardCombineButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.ActiveDashboard == DashboardFlashcards) OpenFlashcardCombineMenuItem_Click(sender, e);
        else if (vm.ActiveDashboard == DashboardQuizzes) OpenQuizLibraryButton_Click(sender, e);
    }
    private void DashboardHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.ActiveDashboard == DashboardFlashcards) OpenFlashcardHistoryMenuItem_Click(sender, e);
        else if (vm.ActiveDashboard == DashboardQuizzes) OpenAttemptHistoryButton_Click(sender, e);
    }
    private void DashboardStatisticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { ActiveDashboard: DashboardFlashcards }) OpenFlashcardStatisticsMenuItem_Click(sender, e);
    }
}
