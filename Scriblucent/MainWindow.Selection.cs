using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.ViewModels;
using Scriblucent.Services;
using Scriblucent.Views;

namespace Scriblucent;

public partial class MainWindow
{
    private void InitializeDashboardSelection(MainViewModel vm)
    {
        DashboardSelectionDock.ScrollTarget = ResolveActiveDashboardScrollViewer();
        vm.DashboardSelection.Changed += SyncSelectionScrollTarget;
        void SyncSelectionScrollTarget() => DashboardSelectionDock.ScrollTarget = ResolveActiveDashboardScrollViewer();
        Closed += (_, _) => vm.DashboardSelection.Changed -= SyncSelectionScrollTarget;
        DashboardSelectionDock.Initialize(vm.DashboardSelection,
            () => DeleteSelectedMassButton_Click(this, new RoutedEventArgs()), BuildDashboardSelectionMenu,
            () => { if (vm.DuplicateSelectedNotesCommand.CanExecute(null)) vm.DuplicateSelectedNotesCommand.Execute(null); });
    }
    private void DashboardSelection_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.OriginalSource is not DependencyObject source
            || !ReferenceEquals(CollectionSelectionFrame.GetSelection(source), vm.DashboardSelection)) return;
        if (DashboardSelectionDock.HandleOperationKey(e)) return;
        if (vm.DashboardSelection.IsActive)
            EditorSelectionInput.HandleKey(e, vm.DashboardSelection, () => DeleteSelectedMassButton_Click(this, new RoutedEventArgs()));
    }
    private ContextMenu BuildDashboardSelectionMenu()
    {
        var menu = new ContextMenu();
        if (DataContext is not MainViewModel vm) return menu;
        var rename = new MenuItem { Header = LocalizationService.GetString("RenameSelected") };
        rename.Click += (_, _) => RenameDashboardSelection(); menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        Add("MassSelectGroupSelected", vm.GroupSelectedNotesCommand);
        Add("MassSelectUngroupSelected", vm.RemoveSelectedFromGroupsCommand);
        Add("MassSelectPinSelected", vm.PinSelectedNotesCommand);
        Add("MassSelectUnpinSelected", vm.UnpinSelectedNotesCommand);
        menu.Items.Add(new Separator());
        var tags = new MenuItem { Header = LocalizationService.GetString("MassSelectAddTags"), IsEnabled = vm.ActiveMassSelectedCount > 0 };
        tags.Click += (_, _) => AddTagsToSelectedButton_Click(this, new RoutedEventArgs()); menu.Items.Add(tags);
        menu.Items.Add(new Separator());
        var export = new MenuItem { Header = LocalizationService.GetString("SelectionExportSelected") };
        export.Click += async (_, _) => await ExportDashboardSelectionAsync(); menu.Items.Add(export);
        return menu;
        void Add(string key, ICommand command) => menu.Items.Add(new MenuItem { Header = LocalizationService.GetString(key), Command = command });
    }
    private void RenameDashboardSelection()
    {
        if (DataContext is not MainViewModel vm) return;
        var keys = vm.SelectedDashboardExportKeys();
        var kind = keys.FirstOrDefault().Kind;
        var targets = vm.SelectedDashboardRenameTargets().Select(target => target with { Name = EditorWorkspace.CurrentTitle(new(kind, target.Id), target.Name) }).ToArray();
        if (targets.Length == 0) return;
        var dialog = new BulkRenameDialog(targets) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!vm.TryRenameDashboardSelection(dialog.Changes))
        {
            ModernDialog.ShowError(this, LocalizationService.GetString("Error"), LocalizationService.GetString("RenameSaveFailed")); return;
        }
        EditorWorkspace.ApplyRenamedTitles(kind, dialog.Changes);
    }
    private async Task ExportDashboardSelectionAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var keys = vm.SelectedDashboardExportKeys(); if (keys.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = LocalizationService.GetString("SelectionExportSelected"),
            Filter = "ZIP (*.zip)|*.zip", DefaultExt = ".zip", FileName = "Scriblucent - " + LocalizationService.GetString("SelectionExportSuffix"), OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await DashboardSelectionDock.RunOperationAsync(async (token, progress) =>
        {
            var snapshots = await EditorWorkspace.CaptureExportSnapshotsAsync(keys, token);
            await SelectionExportService.ExportArchiveAsync(snapshots, dialog.FileName, token, progress);
        });
    }
}
