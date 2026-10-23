using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeFlashcardWheel(bool hitch = false)
    {
        var reports = new List<string>();
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Full, false);
            var store = Path.Combine(_output, "stores", theme); Directory.CreateDirectory(store);
            var vm = new MainViewModel(settings, loadPersistedContent: false, applyThemeAndCulture: false) { PersistenceDirectoryOverride = store };
            var owner = new Window { Width = 800, Height = 600, Content = new TextBlock { Text = "Isolated dashboard" }, ShowInTaskbar = false };
            _app.MainWindow = owner; owner.Show();
            var workspace = new EditorWorkspaceService(owner, vm);
            workspace.OpenFlashcards(new FlashcardSetDocument { Title = "24 text-only cards", Cards = Enumerable.Range(0, 24)
                .Select(i => new FlashcardItem { Question = $"Question {i}: plain text in a small set", Answer = $"Answer {i}", Category = "Practice" }).ToList() }, draft: true);
            var window = workspace.Window!; window.ShowInTaskbar = false;
            var editor = (FlashcardsPreviewWindow)window.Sessions.Single().Editor;
            var grid = (FlashcardGridItemsControl)editor.FindName("FlashcardsItemsControl");
            Pump(1000); window.UpdateLayout();
            var viewer = ControlTree(grid).OfType<ScrollViewer>().First();
            foreach (var variant in hitch ? new[] { "revised", "revised-repeat" }
                         : new[] { "live", "tiles", "live-repeat", "tiles-repeat" })
            {
                var tiles = hitch || variant.StartsWith("tiles");
                SmoothScrollBehavior.Interrupt(viewer); ScrollTileBehavior.SetIsEnabled(viewer, tiles);
                viewer.ScrollToVerticalOffset(0); Pump(650);
                var frames = new List<double>(); var clock = Stopwatch.StartNew(); double last = -1; var renderTime = TimeSpan.MinValue;
                var gcCounts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                if (hitch) FlashcardScrollTrace.Start();
                var allocations = GC.GetAllocatedBytesForCurrentThread();
                EventHandler rendering = (_, args) =>
                {
                    if (args is RenderingEventArgs time && time.RenderingTime == renderTime) return;
                    if (args is RenderingEventArgs time2) renderTime = time2.RenderingTime;
                    var now = clock.Elapsed.TotalMilliseconds;
                    if (last >= 0) { frames.Add(now - last); FlashcardScrollTrace.Record(FlashcardScrollTrace.Kind.Frame, now - last, viewer.VerticalOffset, viewer); }
                    for (int gen = 0; gen < 3; gen++)
                    {
                        var count = GC.CollectionCount(gen);
                        if (count != gcCounts[gen]) FlashcardScrollTrace.Record(FlashcardScrollTrace.Kind.Gc, count - gcCounts[gen], gen);
                        gcCounts[gen] = count;
                    }
                    last = now;
                };
                var ticks = 0; double highest = 0;
                var input = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
                input.Tick += (_, _) =>
                {
                    var source = (DependencyObject?)ControlTree(grid).OfType<DashboardTileHost>().FirstOrDefault() ?? viewer;
                    viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, ticks++ % (hitch ? 24 : 32) < (hitch ? 12 : 16) ? -120 : 120)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = source });
                    highest = Math.Max(highest, viewer.VerticalOffset);
                };
                void OffsetChanged(object sender, ScrollChangedEventArgs args)
                { if (args.VerticalChange != 0) FlashcardScrollTrace.Record(FlashcardScrollTrace.Kind.Offset, value: viewer.VerticalOffset, identity: viewer); }
                if (hitch) viewer.ScrollChanged += OffsetChanged;
                CompositionTarget.Rendering += rendering; input.Start(); Pump(hitch ? 8500 : 4500);
                input.Stop(); CompositionTarget.Rendering -= rendering;
                if (hitch)
                {
                    viewer.ScrollChanged -= OffsetChanged;
                    var trace = FlashcardScrollTrace.Stop();
                    ExportFlashcardHitchTrace(trace, theme, variant);
                    Require(!trace.Any(e => e.Event == FlashcardScrollTrace.Kind.Capture), "Paint capture ran during continuous wheel input.");
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocations;
                SmoothScrollBehavior.Interrupt(viewer); Pump(200);
                Require(frames.Count > 20 && highest > 500, "Tabbed wheel probe did not scroll."); frames.Sort();
                var report = $"{theme}/{variant}: median={frames[frames.Count/2]:F1}ms; p95={frames[(int)(frames.Count*.95)]:F1}ms; max={frames[^1]:F1}ms; >33ms={frames.Count(f=>f>33)}; UI allocations={allocated/1024d:F0}KiB; cache={SkiaCardTileCache.FlashcardBytes/1024d:F0}KiB; failed renders={SkiaCardTileCache.FailedRenders}";
                reports.Add(report); Console.WriteLine(report);
                Console.WriteLine($"capture={FrozenCardDrawing.CaptureAttempts}; unarranged={FrozenCardDrawing.Unarranged}; unfreezable={FrozenCardDrawing.Unfreezable}; loaded hosts={ControlTree(grid).OfType<DashboardTileHost>().Count(h=>h.IsLoaded)}");
            }
            ScrollTileBehavior.SetIsEnabled(viewer, true); viewer.ScrollToVerticalOffset(0); Pump(600);
            var host = ControlTree(grid).OfType<DashboardTileHost>().First();
            SaveControlCapture(host, $"wheel-live-{theme}");
            var desired = host.DesiredSize;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = host });
            Pump(30);
            Require(host.HasReadyScrollTile, "The ready tile did not paint during the glide.");
            Require(host.DesiredSize == desired, "Scroll tile altered card geometry.");
            SaveControlCapture(host, $"wheel-tile-{theme}");
            SmoothScrollBehavior.Interrupt(viewer); Pump(60);
            Require(!host.HasReadyScrollTile && !ScrollTileBehavior.GetIsScrolling(viewer), "Live rendering was not restored.");
            Require(SkiaCardTileCache.FailedRenders == 0, "Frozen paint replay failed.");
            Require(SkiaCardTileCache.FlashcardBytes <= 32L*1024*1024, "Flashcard tile budget exceeded.");
            if (_pointerControls) ProbeFlashcardWheelPointer(window, editor, viewer, grid);
            var item = (FlashcardsPreviewWindow.FlashcardPreviewItem)host.DataContext;
            item.IsSelected = true; item.IsStarred = true; item.IsKnown = true; Pump(200);
            SaveControlCapture(host, $"wheel-selected-live-{theme}");
            host.QueueWarm(); Pump(600); host.IsBitmapMode = true;
            Require(host.HasReadyScrollTile, "Selected tile did not update.");
            SaveControlCapture(host, $"wheel-selected-tile-{theme}");
            host.IsBitmapMode = false;
            var flip = ControlTree(host).OfType<FlashcardFlipHost>().First(); flip.AnimateToggle(240); Pump(450);
            host.QueueWarm(); Pump(600); SaveControlCapture(host, $"wheel-answer-live-{theme}");
            host.IsBitmapMode = true; Pump(80); Require(host.HasReadyScrollTile, "Answer tile did not update after flipping.");
            SaveControlCapture(host, $"wheel-answer-tile-{theme}"); host.IsBitmapMode = false;
            foreach (var session in window.Sessions) session.Dispose();
            window.CloseAfterPreflight(); Pump(500); owner.Close(); Pump(100);
        }
        File.WriteAllLines(Path.Combine(_output, "flashcard-wheel.txt"), reports);
    }

    private static void ExportFlashcardHitchTrace(FlashcardScrollTrace.Entry[] trace, string theme, string variant)
    {
        if (trace.Length == 0) return;
        var start = trace[0].Timestamp;
        File.WriteAllLines(Path.Combine(_output, $"hitch-{theme}-{variant}.tsv"), new[] { "time_ms\tevent\tduration_ms\tvalue\tidentity" }
            .Concat(trace.Select(e => FormattableString.Invariant($"{(e.Timestamp-start)*1000d/Stopwatch.Frequency:F3}\t{e.Event}\t{e.Duration:F3}\t{e.Value:F3}\t{e.Identity}"))));
        var frames = trace.Where(e=>e.Event == FlashcardScrollTrace.Kind.Frame).OrderByDescending(e=>e.Duration).Take(8);
        var report = frames.Select(frame =>
        {
            var window = trace.Where(e=>e.Timestamp<=frame.Timestamp && e.Timestamp>=frame.Timestamp-(long)(frame.Duration*Stopwatch.Frequency/1000));
            return FormattableString.Invariant($"gap={frame.Duration:F1}ms offset={frame.Value:F1}; ") +
                string.Join("; ", window.Where(e=>e.Event is not (FlashcardScrollTrace.Kind.Frame or FlashcardScrollTrace.Kind.Offset))
                    .GroupBy(e=>e.Event).Select(g=>g.Key == FlashcardScrollTrace.Kind.Gc
                        ? $"GC collections={g.Sum(e=>e.Duration):F0}"
                        : FormattableString.Invariant($"{g.Key}: count={g.Count()}, total={g.Sum(e=>e.Duration):F1}ms, max={g.Max(e=>e.Duration):F1}ms")));
        });
        File.WriteAllLines(Path.Combine(_output, $"hitch-{theme}-{variant}-worst.txt"), report);
        var measures = trace.Where(e=>e.Event == FlashcardScrollTrace.Kind.Measure && e.Duration > 1).Select(e=>e.Duration).Order().ToArray();
        if (measures.Length > 0)
            File.WriteAllText(Path.Combine(_output, $"hitch-{theme}-{variant}-layout.txt"), FormattableString.Invariant(
                $"Nontrivial layout passes (>1ms): count={measures.Length}; median={measures[measures.Length/2]:F2}ms; p95={measures[(int)(measures.Length*.95)]:F2}ms; max={measures[^1]:F2}ms. These are UI layout durations, not frame rates."));
    }

    private static void ProbeFlashcardWheelPointer(Window window, FlashcardsPreviewWindow editor, ScrollViewer viewer, FrameworkElement grid)
    {
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previous);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        try
        {
            SetForegroundWindow(handle); window.Activate(); Pump(80);
            if (GetForegroundWindow() != handle)
            { Console.WriteLine("UNVERIFIED native pointer click: fixture could not acquire foreground on this desktop; no click sent."); return; }
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
            Pump(20);
            var pair = ControlTree(grid).OfType<DashboardTileHost>().Select(h => (Host:h, Button:ControlTree(h).OfType<Button>()
                .FirstOrDefault(b=>b.Style == editor.FindResource("FlashcardStarButtonStyle") && b.IsVisible)))
                .First(p => p.Button is not null && p.Button.TranslatePoint(new Point(0,0), viewer).Y > 8
                    && p.Button.TranslatePoint(new Point(0,p.Button.ActualHeight), viewer).Y < viewer.ActualHeight - 8);
            Require(pair.Host.HasReadyScrollTile && SmoothScrollCompositor.IsActive(viewer), "Native click fixture missed the active glide.");
            var item = (FlashcardsPreviewWindow.FlashcardPreviewItem)pair.Host.DataContext; var before = item.IsStarred;
            var button = pair.Button!; var screen = button.PointToScreen(new Point(button.ActualWidth/2, button.ActualHeight/2));
            var point = new CursorPoint { X = (int)screen.X, Y = (int)screen.Y };
            Require(WindowFromPoint(point) == handle, "Native card target is covered by another window.");
            SetCursorPos(point.X, point.Y); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); Pump(120);
            Require(item.IsStarred != before && !SmoothScrollCompositor.IsActive(viewer) && !pair.Host.HasReadyScrollTile,
                "The first native click during a glide failed to toggle the star or restore input.");
            Console.WriteLine("PASS native first click during glide: original star handler toggled once; scroll interrupted; live card restored.");
        }
        finally
        { mouse_event(4,0,0,0,UIntPtr.Zero); SetCursorPos(previous.X,previous.Y); SetForegroundWindow(previousWindow); }
    }
}
