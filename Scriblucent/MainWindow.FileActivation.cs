using System.ComponentModel;
using System.IO;
using System.Windows;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent;

public partial class MainWindow
{
    private bool _processingLaunchImports;

    internal void ProcessLaunchImports(LaunchImportQueue queue)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!_processingLaunchImports) _ = DrainLaunchImportsAsync(queue);
    }

    private async Task DrainLaunchImportsAsync(LaunchImportQueue queue)
    {
        if (DataContext is not MainViewModel vm) return;
        _processingLaunchImports = true;
        try
        {
            await Task.Yield();
            while (queue.Peek() is { } path && IsLoaded)
            {
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(LocalizationService.GetString("LaunchFileMissing"));
                    var dashboard = LaunchImportQueue.DashboardFor(path)
                        ?? throw new InvalidOperationException(LocalizationService.GetString("LaunchFileUnsupported"));
                    switch (dashboard)
                    {
                        case "Notes": vm.ShowNotesCommand.Execute(null); break;
                        case "Flashcards": vm.ShowFlashcardsCommand.Execute(null); break;
                        case "MindMaps": vm.ShowMindMapsCommand.Execute(null); break;
                        case "Quizzes": vm.ShowQuizzesCommand.Execute(null); break;
                    }
                    if (!await WaitForImportDashboardAsync(vm, dashboard))
                        throw new InvalidOperationException(LocalizationService.GetString("FailedToOpenFile"));
                    switch (dashboard)
                    {
                        case "Notes": ImportNativeNote(path, vm); break;
                        case "Flashcards": ImportFlashcardsFiles([path], openEditor: true); break;
                        case "MindMaps": ImportMindMapFiles([path], openEditor: true); break;
                        case "Quizzes": ImportQuizFiles([path], openEditor: true); break;
                    }
                }
                catch (Exception ex)
                {
                    if (IsLoaded) ModernMessageBox.Show(
                        $"{LocalizationService.GetString("ImportError")}\n{Path.GetFileName(path)}\n\n{ex.Message}",
                        LocalizationService.GetString("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { queue.Complete(); }
            }
        }
        finally { _processingLaunchImports = false; }
    }

    private async Task<bool> WaitForImportDashboardAsync(MainViewModel vm, string dashboard)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (vm.IsDashboardReadyForImport(dashboard)) completion.TrySetResult(true);
            else if (vm.HasDashboardImportLoadFailed(dashboard)) completion.TrySetResult(false);
        }
        void Changed(object? sender, PropertyChangedEventArgs e) => Check();
        void Closing(object? sender, EventArgs e) => completion.TrySetResult(false);
        vm.PropertyChanged += Changed;
        Closed += Closing;
        try { Check(); return await completion.Task; }
        finally { vm.PropertyChanged -= Changed; Closed -= Closing; }
    }

    private void ImportNativeNote(string path, MainViewModel vm)
        => OpenNoteEditor(vm.AddNoteFromDocument(NoteFileService.LoadNotePackage(path)));
}
