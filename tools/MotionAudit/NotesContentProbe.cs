using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    // Copies only: output contains private library data and belongs in artifacts.
    // Dispatcher operation durations measure UI work, not physical display presentation/FPS.
    private static void ProbeNotesContent()
    {
        var source = _rapidAddLibrarySource ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        var original = File.ReadAllText(Path.Combine(source, "notes.json"));
        var settingsJson = File.ReadAllText(Path.Combine(source, "settings.json"));
        var report = new List<string>();
        var variants = Environment.GetEnvironmentVariable("SCRIBLUCENT_CONTENT_VARIANTS")
            ?? "baseline,empty-previews,no-image-marker,no-groups,baseline-repeat";
        foreach (var variant in variants.Split(','))
        {
            var store = Path.Combine(_output, variant); Directory.CreateDirectory(store);
            var data = JsonSerializer.Deserialize<NotesStoreData>(original)!;
            if (variant == "empty-previews")
                foreach (var note in data.Notes) { note.Content = ""; note.SearchablePreview = ""; note.Images.Clear(); }
            if (variant == "no-image-marker")
                foreach (var note in data.Notes) note.SearchablePreview = note.SearchablePreview?.Replace("\uFFFC", "");
            if (variant == "no-groups") { data.Groups.Clear(); foreach (var note in data.Notes) note.GroupId = null; }
            File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(data));
            foreach (var name in new[] { "flashcard-sets.json", "mind-maps.json", "quizzes.json" })
                if (File.Exists(Path.Combine(source, name))) File.Copy(Path.Combine(source, name), Path.Combine(store, name), true);
            var settings = JsonSerializer.Deserialize<AppSettings>(settingsJson)!;
            settings.EnableAutoSave = false; settings.EnableAiFeatures = false;
            AppSettingsService.SettingsDirectoryOverride = store;
            typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
            LoadNativeControlResources(settings.Theme);
            var window = new MainWindow(settings) { Width = 1280, Height = 960, ShowInTaskbar = false };
            _app.MainWindow = window;
            var vm = (MainViewModel)window.DataContext; vm.PersistenceDirectoryOverride = store;
            var clock = Stopwatch.StartNew();
            try
            {
                window.Show(); window.Activate();
                var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
                WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed, 15000);
                var load = clock.ElapsedMilliseconds;
                Pump(1100);
                var operations = new List<(DispatcherPriority Priority, double Ms)>();
                var starts = new Dictionary<DispatcherOperation, long>();
                DispatcherHookEventHandler begin = (_, e) => starts[e.Operation] = Stopwatch.GetTimestamp();
                DispatcherHookEventHandler end = (_, e) =>
                {
                    if (starts.Remove(e.Operation, out var start))
                        operations.Add((e.Operation.Priority, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                };
                window.Dispatcher.Hooks.OperationStarted += begin;
                window.Dispatcher.Hooks.OperationCompleted += end;
                try { for (int i = 0; i < 4; i++) { vm.ToggleSidebarCommand.Execute(null); Pump(500); } }
                finally
                {
                    window.Dispatcher.Hooks.OperationStarted -= begin;
                    window.Dispatcher.Hooks.OperationCompleted -= end;
                }
                var render = operations.Where(o => o.Priority == DispatcherPriority.Render).Select(o => o.Ms).OrderBy(x => x).ToArray();
                var line = FormattableString.Invariant($"{variant}: load={load}ms renderOperations={render.Length} renderP95={render.ElementAtOrDefault((int)(render.Length * .95)):F1}ms renderMax={render.LastOrDefault():F1}ms visibleCards={ControlTree(window).OfType<NoteCardControl>().Count(c => c.IsVisible)}");
                report.Add(line); Console.WriteLine(line);
                var page = (FrameworkElement)window.FindName("NotesDashboardView");
                var viewer = ControlTree(page).OfType<ScrollViewer>().First(v => v.Name == "NotesScrollViewer");
                viewer.ScrollToBottom(); Pump(400);
                Require(ControlTree(page).OfType<NoteCardControl>().Any(c => c.IsVisible && c.ActualHeight > 100), "Scrolling back to the notes did not realize their cards.");
            }
            finally { CloseLoadingWindow(window); }
        }
        File.WriteAllLines(Path.Combine(_output, "content-profile.txt"), report);
    }
}
