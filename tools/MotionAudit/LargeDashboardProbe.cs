using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeLargeDashboard()
    {
        var report = new List<string>();
        void Mark(string value) { report.Add(value); Console.WriteLine(value); File.WriteAllLines(Path.Combine(_output, "large-dashboard.txt"), report); }
        foreach (var count in (Environment.GetEnvironmentVariable("SCRIBLUCENT_BULK_COUNTS") ?? "1000,20000").Split(',').Select(int.Parse))
        foreach (var grouped in new[] { false, true })
        {
            var store = Path.Combine(_output, $"{count}-{grouped}"); Directory.CreateDirectory(store);
            AppSettingsService.SettingsDirectoryOverride = store;
            var settings = new AppSettings { Theme = "Light", LastView = "Notes", EnableAutoSave = false, EnableAiFeatures = false, EnableDiagnosticLogging = true };
            typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
            var groups = Enumerable.Range(0, grouped ? 12 : 0).Select(i => new NoteGroupData { GroupId = Guid.NewGuid(), Name = $"Group {i}", SortOrder = i }).ToList();
            File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData
            {
                Notes = Enumerable.Range(0, count).Select(i => new NoteDocument { Title = $"Planning {i:D6}", Content = "A short note for repeatable performance measurements.", SearchablePreview = "A short note for repeatable performance measurements.", GroupId = grouped ? groups[i % groups.Count].GroupId : null }).ToList(),
                Groups = groups
            }));
            LoadNativeControlResources(settings.Theme);
            var window = new MainWindow(settings) { Width = 1280, Height = 960, ShowInTaskbar = false };
            _app.MainWindow = window;
            var vm = (MainViewModel)window.DataContext; vm.PersistenceDirectoryOverride = store;
            try
            {
                var startup = Stopwatch.StartNew();
                window.Show(); window.Activate(); window.WindowState = WindowState.Maximized;
                var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
                WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed, 30000); Pump(500);
                Mark($"{count}/{grouped}: loaded={startup.ElapsedMilliseconds}ms realized={ControlTree(window).OfType<NoteCardControl>().Count(c => c.IsVisible)}");
                MeasureLargeAction(window, $"{count}/{grouped}/sidebar-initial-0", () => vm.ToggleSidebarCommand.Execute(null), Mark);
                MeasureLargeAction(window, $"{count}/{grouped}/sidebar-initial-1", () => vm.ToggleSidebarCommand.Execute(null), Mark);
                MeasureLargeAction(window, $"{count}/{grouped}/select-all", () => { vm.EnterMassSelect(vm.Notes[0]); vm.SelectAllVisibleNotesCommand.Execute(null); }, Mark);
                Pump(350);
                for (int round = 0; round < 2; round++)
                {
                    var before = vm.Notes.Count;
                    MeasureLargeAction(window, $"{count}/{grouped}/duplicate-{round}", () => vm.DuplicateSelectedNotesCommand.Execute(null), Mark);
                    WaitLoading(() => !LargeField<bool>(vm, "_notesWriteInFlight") && !LargeField<bool>(vm, "_saveNotesQueued"), 30000);
                    Require(vm.Notes.Count == before + count, "Bulk duplication lost or added unexpected notes.");
                    Require(ControlTree(window).OfType<NoteCardControl>().Count(c => c.IsVisible) < 200, "Bulk duplication realized the entire library.");
                }
                vm.ExitMassSelectCommand.Execute(null); Pump(350);
                for (int cycle = 0; cycle < 4; cycle++)
                    MeasureLargeAction(window, $"{count}/{grouped}/sidebar-{cycle}", () => vm.ToggleSidebarCommand.Execute(null), Mark);
                var page = (FrameworkElement)window.FindName("NotesDashboardView");
                var viewer = (ScrollViewer)page.FindName("NotesScrollViewer");
                viewer.ScrollToBottom(); Pump(500);
                var bottomCards = ControlTree(page).OfType<NoteCardControl>().Where(c => c.IsVisible).ToArray();
                Require(bottomCards.Length > 0 && bottomCards.Length < 200, "The bottom viewport failed to realize a bounded card set.");
                if (grouped) Require(bottomCards.Any(c => c.DataContext is NoteCardViewModel n && n.Document.GroupId == groups[^1].GroupId), "The last group failed to resume after scrolling.");
                viewer.ScrollToTop(); Pump(500);
                var persisted = JsonSerializer.Deserialize<NotesStoreData>(File.ReadAllText(Path.Combine(store, "notes.json")))!;
                Require(persisted.Notes.Count == count * 3 && persisted.Notes.Select(n => n.Id).Distinct().Count() == count * 3, "Bulk persistence is incomplete or IDs collide.");
            }
            finally { CloseLoadingWindow(window); }
        }
    }
    private static T LargeField<T>(MainViewModel model, string name) => (T)typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
    private static void MeasureLargeAction(Window window, string name, Action action, Action<string> mark)
    {
        var starts = new Dictionary<DispatcherOperation, long>();
        var operations = new List<(DispatcherPriority Priority, double Time)>();
        DispatcherHookEventHandler begin = (_, e) => starts[e.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler end = (_, e) => { if (starts.Remove(e.Operation, out var start)) operations.Add((e.Operation.Priority, Stopwatch.GetElapsedTime(start).TotalMilliseconds)); };
        window.Dispatcher.Hooks.OperationStarted += begin; window.Dispatcher.Hooks.OperationCompleted += end;
        var clock = Stopwatch.StartNew(); var allocated = GC.GetAllocatedBytesForCurrentThread();
        double commandMs; long bytes;
        try { action(); commandMs = clock.Elapsed.TotalMilliseconds; bytes = GC.GetAllocatedBytesForCurrentThread() - allocated; Pump(750); }
        finally { window.Dispatcher.Hooks.OperationStarted -= begin; window.Dispatcher.Hooks.OperationCompleted -= end; }
        mark(FormattableString.Invariant($"{name}: command={commandMs:F1}ms uiAllocated={bytes / 1024d:F0}KB worstOperations={string.Join(';', operations.OrderByDescending(o => o.Time).Take(6).Select(o => FormattableString.Invariant($"{o.Priority}:{o.Time:F1}ms")))}"));
    }
}
