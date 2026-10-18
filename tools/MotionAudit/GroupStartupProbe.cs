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
    private static void ProbeGroupStartup(bool checkSectionOrdering = false, string? theme = null, bool checkCreationMenus = false)
    {
        // Exercise the real shell/loading sequence; fixture pages do not cover this path.
        // Copy input files and redirect every preference/document write to this run's output.
        var store = Path.Combine(_output, "store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        var source = _rapidAddLibrarySource ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        foreach (var name in new[] { "settings.json", "notes.json", "flashcard-sets.json", "mind-maps.json", "quizzes.json" })
            if (File.Exists(Path.Combine(source, name))) File.Copy(Path.Combine(source, name), Path.Combine(store, name));
        AppSettingsService.SettingsDirectoryOverride = store;
        var settings = File.Exists(Path.Combine(store, "settings.json"))
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(store, "settings.json")))!
            : new AppSettings();
        settings.EnableAutoSave = false;
        settings.EnableAiFeatures = false;
        // Select the initial page only after the copied store is assigned to the view model.
        settings.LastView = "Notes";
        if (theme is not null) settings.Theme = theme;
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
        var clock = Stopwatch.StartNew();
        var report = new List<string>();
        void Mark(string stage)
        {
            var line = $"{clock.ElapsedMilliseconds}ms {stage}";
            Console.WriteLine(line);
            report.Add(line);
        }
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("Startup probe timed out: UI dispatcher did not complete loading.");
            Environment.Exit(124);
        }, null, checkCreationMenus ? 90000 : 40000, System.Threading.Timeout.Infinite);
        ThemeManager.SetTheme(settings.Theme);
        LoadNativeControlResources(ThemeManager.CurrentTheme);
        Mark("resources");
        var window = new MainWindow(settings) { Width = 1280, Height = 960, ShowInTaskbar = false, ShowActivated = false };
        _app.MainWindow = window;
        typeof(MainWindow).GetField("_startupDataInitialized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        var vm = (MainViewModel)window.DataContext;
        vm.PersistenceDirectoryOverride = store;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsActiveDashboardReady)) Mark($"ready={vm.IsActiveDashboardReady}");
        };
        Mark("constructed");
        try
        {
            window.Show();
            Mark("shown");
            window.Dispatcher.BeginInvoke(() => { Mark("load started"); vm.LoadPersistedContent(); Mark("load scheduled"); }, DispatcherPriority.Background);
            var loading = (FrameworkElement)window.FindName("DashboardLoadingLayer");
            while ((!vm.IsActiveDashboardReady || loading.Visibility == Visibility.Visible) && clock.ElapsedMilliseconds < 15000) Pump(50);
            Require(vm.IsActiveDashboardReady && loading.Visibility != Visibility.Visible, "Startup did not dismiss the loading skeleton.");
            Mark("skeleton dismissed");
            Pump(400);
            var pulses = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => pulses++;
            timer.Start(); Pump(300); timer.Stop();
            Require(pulses >= 3, "UI dispatcher is starved after startup.");
            Mark($"responsive; {vm.NoteGroups.Count} note groups; {pulses} background pulses");
            foreach (var command in new[] { vm.ShowNotesCommand, vm.ShowFlashcardsCommand, vm.ShowMindMapsCommand, vm.ShowQuizzesCommand })
            {
                command.Execute(null);
                var deadline = clock.ElapsedMilliseconds + 7000;
                while ((!vm.IsActiveDashboardReady || loading.Visibility == Visibility.Visible) && clock.ElapsedMilliseconds < deadline) Pump(50);
                Pump(650);
                Require(vm.IsActiveDashboardReady && loading.Visibility != Visibility.Visible, $"{vm.ActiveDashboard} stayed behind its loading skeleton.");
                if (checkSectionOrdering)
                    ProbeDashboardSectionOrdering((FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView"), vm.ActiveDashboard, Mark);
                if (checkCreationMenus)
                    ProbeDashboardCreationMenu(window, (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView"), vm, settings.Theme, Mark);
                var headers = ControlTree(window).OfType<DashboardGroupHeader>().Where(h => h.IsVisible).ToArray();
                foreach (var header in headers)
                {
                    var toggle = FindGroupDisclosure(header);
                    Require(toggle.Style is not null && toggle.Template is not null && toggle.ActualWidth == 24,
                        $"{vm.ActiveDashboard} disclosure did not resolve the window-owned style.");
                    var expanded = header.IsExpanded;
                    DashboardGroupHeader.ToggleExpansionCommand.Execute(null, toggle); Pump(400);
                    Require(header.IsExpanded != expanded, "Real-shell group toggle did not respond.");
                    DashboardGroupHeader.ToggleExpansionCommand.Execute(null, toggle); Pump(400);
                    Require(header.IsExpanded == expanded, "Real-shell group toggle did not restore its state.");
                }
                if (vm.ActiveDashboard == "Notes")
                    foreach (var card in ControlTree(window).OfType<NoteCardControl>().Where(card => card.IsVisible && !card.IsBitmapMode))
                        if (card.DataContext is NoteCardViewModel note && note.IsContentPreviewCached && !string.IsNullOrWhiteSpace(note.Content))
                            Require(((TextBlock)card.FindName("ContentPreview")).Inlines.Count > 0,
                                "A returned note card retained an empty preview after reloading.");
                Mark($"PASS {vm.ActiveDashboard}: loaded, {headers.Length} visible group styles/toggles checked");
            }
            if (checkCreationMenus) ProbeCreationMenuLifecycle(settings.Theme, Mark);
            File.WriteAllLines(Path.Combine(_output, "startup-checks.txt"), report);
        }
        finally
        {
            window.Close();
            AppSettingsService.Flush();
        }
    }
}
