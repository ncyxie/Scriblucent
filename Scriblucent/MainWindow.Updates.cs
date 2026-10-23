using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

namespace Scriblucent;

public partial class MainWindow
{
    public AppUpdateCoordinator? Updates => (Application.Current as App)?.Updates;
    private MainViewModel? _updateViewModel;
    private bool _updateNotificationsInitialized;

    private void InitializeUpdateNotifications()
    {
        if (Updates is null) return;
        _updateNotificationsInitialized = true;
        Updates.PropertyChanged += UpdateStateChanged;
        LocalizationService.CultureChanged += UpdateCultureChanged;
        Activated += UpdateWindowStateChanged;
        Deactivated += UpdateWindowStateChanged;
        Loaded += UpdateSurfaceVisibilityChanged;
        PreviewMouseUp += UpdateMouseReleased;
        IsEnabledChanged += UpdateSurfaceChanged;
        SettingsPanelControl.IsVisibleChanged += UpdateSurfaceChanged;
        AboutPanelControl.IsVisibleChanged += UpdateSurfaceChanged;
        TrashPanelControl.IsVisibleChanged += UpdateSurfaceChanged;
        NoteSchedulePanelControl.IsVisibleChanged += UpdateSurfaceChanged;
        DataContextChanged += UpdateDataContextChanged;
        UpdateNotice.ViewUpdateRequested += (_, _) => ShowAvailableUpdateDetails();
        UpdateNotice.DismissRequested += (_, _) => Updates?.AcknowledgeAvailableUpdate();
        Closed += (_, _) =>
        {
            if (_updateViewModel is not null) _updateViewModel.PropertyChanged -= UpdateDashboardChanged;
            Updates.PropertyChanged -= UpdateStateChanged;
            LocalizationService.CultureChanged -= UpdateCultureChanged;
            Updates.Dispose();
        };
        ObserveUpdateDashboard();
        RefreshUpdateNotifications();
    }

    private void ObserveUpdateDashboard()
    {
        if (_updateViewModel is not null) _updateViewModel.PropertyChanged -= UpdateDashboardChanged;
        _updateViewModel = DataContext as MainViewModel;
        if (_updateViewModel is not null) _updateViewModel.PropertyChanged += UpdateDashboardChanged;
    }

    private void UpdateDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ObserveUpdateDashboard();
        RefreshUpdateNotifications();
    }

    private void UpdateDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsActiveDashboardReady) or nameof(MainViewModel.ActiveDashboard)
            or nameof(MainViewModel.IsActiveDashboardLoadFailed) or nameof(MainViewModel.IsMassSelectMode))
            RefreshUpdateNotifications();
    }

    private void UpdateStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppUpdateCoordinator.HasPendingNotice)) RefreshUpdateNotifications();
    }

    private void UpdateCultureChanged(object? sender, EventArgs e) => RefreshUpdateNotifications();
    private void UpdateWindowStateChanged(object? sender, EventArgs e) => RefreshUpdateNotifications();
    private void UpdateSurfaceVisibilityChanged(object sender, RoutedEventArgs e) => RefreshUpdateNotifications();
    private void UpdateSurfaceChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshUpdateNotifications();
    private void UpdateMouseReleased(object sender, MouseButtonEventArgs e)
        => Dispatcher.BeginInvoke(RefreshUpdateNotifications, System.Windows.Threading.DispatcherPriority.Background);

    private void RefreshUpdateNotifications()
    {
        if (!_updateNotificationsInitialized || Updates is not { } updates) return;
        SettingsUpdateBadge.Visibility = updates.HasAvailableUpdate ? Visibility.Visible : Visibility.Collapsed;
        var label = updates.HasAvailableUpdate
            ? string.Format(LocalizationService.GetString("SettingsUpdateAvailableFormat"), updates.AvailableVersion)
            : LocalizationService.GetString("Settings");
        SidebarSettingsButton.ToolTip = label;
        AutomationProperties.SetName(SidebarSettingsButton, label);
        UpdateNotice.SetVersion(updates.AvailableVersion);

        if (IsLoaded && _updateViewModel is { } vm
            && ((vm.IsActiveDashboardReady && !IsLoadingDashboardActive) || vm.IsActiveDashboardLoadFailed))
            _ = updates.StartAfterDashboardReadyAsync();

        UpdateNotice.SetShown(updates.HasPendingNotice && IsLoaded && IsActive && IsEnabled
            && WindowState != WindowState.Minimized && _updateViewModel is { IsActiveDashboardReady: true, IsMassSelectMode: false }
            && !IsLoadingDashboardActive && Mouse.LeftButton == MouseButtonState.Released
            && SettingsPanelControl.Visibility != Visibility.Visible && AboutPanelControl.Visibility != Visibility.Visible
            && TrashPanelControl.Visibility != Visibility.Visible && NoteSchedulePanelControl.Visibility != Visibility.Visible);
    }

    internal void ShowAvailableUpdateDetails()
    {
        if (Updates?.LastResult is not { IsUpdateAvailable: true } result) return;
        Updates.AcknowledgeAvailableUpdate();
        new UpdateCheckDialog(result) { Owner = this }.ShowDialog();
    }
}
