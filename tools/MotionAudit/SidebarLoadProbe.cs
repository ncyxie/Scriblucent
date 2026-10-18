using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static int _sidebarLoadedCards;
    // All document/settings writes are isolated. Callback gaps are not presented-frame FPS.
    private static void ProbeSidebarLoad()
    {
        EventManager.RegisterClassHandler(typeof(NoteCardControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((_, _) => _sidebarLoadedCards++));
        var report = new List<string>();
        void Mark(string value) { Console.WriteLine(value); report.Add(value); File.WriteAllLines(Path.Combine(_output, "sidebar-load.txt"), report); }
        var cases = (Environment.GetEnvironmentVariable("SCRIBLUCENT_SIDEBAR_CASES") ?? "ungrouped,grouped,library").Split(',');
        foreach (var name in cases)
        {
            _sidebarLoadedCards = 0;
            var store = Path.Combine(_output, name); Directory.CreateDirectory(store);
            var settings = new AppSettings { Theme = "Light", LastView = "Notes", EnableAutoSave = false, EnableAiFeatures = false };
            if (name == "library")
            {
                var source = _rapidAddLibrarySource ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
                File.Copy(Path.Combine(source, "notes.json"), Path.Combine(store, "notes.json"), true);
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(source, "settings.json")))!;
                settings.LastView = "Notes"; settings.EnableAutoSave = false; settings.EnableAiFeatures = false;
            }
            else
            {
                var groups = Enumerable.Range(0, name.StartsWith("grouped", StringComparison.Ordinal) ? 100 : 0).Select(i => new NoteGroupData { GroupId = Guid.NewGuid(), Name = $"Group {i}", SortOrder = i }).ToList();
                var count = name switch { "small" => 100, "grouped-variable" => 19900, _ => 20000 };
                var preview = name == "grouped-variable" ? new string('x', 72) + "\r\n" + new string('y', 72) : "Repeatable sidebar layout workload.";
                File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData
                {
                    Notes = Enumerable.Range(0, count).Select(i => new NoteDocument { Title = $"Planning {i:D6}", Content = preview, SearchablePreview = preview, GroupId = groups.Count > 0 ? groups[i % groups.Count].GroupId : null }).ToList(),
                    Groups = groups
                }));
            }
            AppSettingsService.SettingsDirectoryOverride = store;
            typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
            ThemeManager.SetTheme(settings.Theme);
            LoadNativeControlResources(ThemeManager.CurrentTheme);
            var window = new MainWindow(settings) { Width = 1280, Height = 960, ShowInTaskbar = false };
            _app.MainWindow = window;
            var vm = (MainViewModel)window.DataContext; vm.PersistenceDirectoryOverride = store;
            try
            {
                var loadWatch = Stopwatch.StartNew();
                window.Show(); window.Activate(); window.WindowState = WindowState.Maximized;
                var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
                WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed, 90000); Pump(1500);
                Mark($"{name}: notes={vm.Notes.Count} loadedCards={_sidebarLoadedCards} visibleCards={ControlTree(window).OfType<NoteCardControl>().Count(c => c.IsVisible)} activeGroups={ControlTree(window).OfType<DashboardGroupCard>().Count(c => c.IsVisible)} size={window.ActualWidth:F0}x{window.ActualHeight:F0} loadAndSettle={loadWatch.ElapsedMilliseconds}ms");
                Require(_sidebarLoadedCards < 160, "Startup loaded distant cards rather than only the viewport/cache band.");
                var sidebar = (FrameworkElement)window.FindName("SidebarChrome");
                var states = typeof(SidebarResizeMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object?[] stateArgs = [sidebar, null]; states.GetType().GetMethod("TryGetValue")!.Invoke(states, stateArgs);
                var sidebarState = stateArgs[1]!;
                if (Environment.GetEnvironmentVariable("SCRIBLUCENT_SIDEBAR_PROFILE_WAIT") == "1")
                {
                    File.WriteAllText(Path.Combine(_output, "ready-pid.txt"), Environment.ProcessId.ToString());
                    WaitLoading(() => File.Exists(Path.Combine(_output, "profile-start.txt")), 60000);
                }
                foreach (var width in new[] { 0, 1280 })
                {
                    if (width > 0) { window.WindowState = WindowState.Normal; window.Width = width; Pump(600); }
                    for (int cycle = 0; cycle < 6; cycle++)
                    {
                        var points = new List<(double Ms, double Width)>();
                        var widths = new List<(double Ms, double Width, int Gen0, int Gen1, int Gen2, double PauseMs)>();
                        var watch = Stopwatch.StartNew();
                        var column = (ColumnDefinition)window.FindName("SidebarColumn");
                        void SampleWidth(double ms) => widths.Add((ms, column.Width.Value,
                            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds));
                        SampleWidth(0);
                        var descriptor = DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition));
                        EventHandler changed = (_, _) => SampleWidth(watch.Elapsed.TotalMilliseconds);
                        descriptor.AddValueChanged(column, changed);
                        EventHandler frame = (_, _) => points.Add((watch.Elapsed.TotalMilliseconds, ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth));
                        CompositionTarget.Rendering += frame;
                        try { MeasureLargeAction(window, $"{name}/{width}/sidebar-{cycle}", () => vm.ToggleSidebarCommand.Execute(null), Mark); }
                        finally { CompositionTarget.Rendering -= frame; descriptor.RemoveValueChanged(column, changed); }
                        Require(Math.Abs(((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth - vm.SidebarWidth) < 1,
                            "The sidebar did not reach its requested width.");
                        var wake = (DispatcherTimer)sidebarState.GetType().GetField("_frameWake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sidebarState)!;
                        Require(!wake.IsEnabled, "The sidebar retained an idle wakeup after settling.");
                        File.WriteAllLines(Path.Combine(store, $"frames-{width}-{cycle}.csv"), new[] { "elapsedMs,width" }.Concat(points.Select(p => FormattableString.Invariant($"{p.Ms:F3},{p.Width:F3}"))));
                        File.WriteAllLines(Path.Combine(store, $"width-{width}-{cycle}.csv"), new[] { "elapsedMs,width,gen0,gen1,gen2,totalGcPauseMs" }.Concat(widths.Select(p => FormattableString.Invariant($"{p.Ms:F3},{p.Width:F3},{p.Gen0},{p.Gen1},{p.Gen2},{p.PauseMs:F3}"))));
                        Mark(FormattableString.Invariant($"  widthUpdates={widths.Count - 1} maxWidthUpdateGap={widths.Zip(widths.Skip(1), (a, b) => b.Ms - a.Ms).DefaultIfEmpty().Max():F1}ms"));
                        var moving = points.Where(p => p.Width > 64.5 && p.Width < 231.5).ToArray();
                        Mark(FormattableString.Invariant($"  movingCallbacks={moving.Length} maxMovingGap={moving.Zip(moving.Skip(1), (a, b) => b.Ms - a.Ms).DefaultIfEmpty().Max():F1}ms"));
                    }
                }
                var page = (FrameworkElement)window.FindName("NotesDashboardView");
                var viewer = (ScrollViewer)page.FindName("NotesScrollViewer");
                foreach (var fraction in new[] { .2, .4, .6, .8, 1, 0 })
                {
                    viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * fraction); Pump(400);
                    var live = ControlTree(window).OfType<NoteCardControl>().ToArray();
                    Require(live.Length < 160, "Visited off-screen groups retained live card controls.");
                    Require(live.Any(c => c.IsVisible), "The destination viewport failed to realize cards.");
                    Mark($"{name}/jump-{fraction}: liveCards={live.Length} visibleCards={live.Count(c => c.IsVisible)}");
                }
            }
            finally { CloseLoadingWindow(window); }
        }
    }
}
