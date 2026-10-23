using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeFlashcardScroll(bool quick)
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var reports = new List<string>();
        var slowOperations = new List<string>();
        var started = new Dictionary<System.Windows.Threading.DispatcherOperation, long>();
        void OperationStarted(object? sender, System.Windows.Threading.DispatcherHookEventArgs e)
            => started[e.Operation] = Stopwatch.GetTimestamp();
        void OperationCompleted(object? sender, System.Windows.Threading.DispatcherHookEventArgs e)
        {
            if (!started.Remove(e.Operation, out var ticks)) return;
            var duration = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
            if (duration < 20) return;
            var names = new List<string>();
            for (var type = e.Operation.GetType(); type is not null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.GetValue(e.Operation) is Delegate callback)
                        names.Add(callback.Method.DeclaringType?.FullName + "." + callback.Method.Name);
            slowOperations.Add($"{duration:F1}ms; {e.Operation.Priority}; {string.Join(", ", names)}");
        }
        _app.Dispatcher.Hooks.OperationStarted += OperationStarted;
        _app.Dispatcher.Hooks.OperationCompleted += OperationCompleted;
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var variant in quick ? new[] { "current" } : new[] { "current", "direct", "without-shadows", "uncached-shadows" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Full, false);
            var window = new FlashcardsPreviewWindow(Enumerable.Range(0, 500).Select(i => new FlashcardItem
                { Question = $"Question {i}: a card to exercise the recycled grid", Answer = $"Answer {i}", Category = "Practice", Hint = "A clue" }));
            if (variant == "without-shadows")
                window.Resources[typeof(CardShadowLayer)] = new Style(typeof(CardShadowLayer))
                    { Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Hidden) } };
            window.Show(); Pump(600);
            var grid = (FlashcardGridItemsControl)window.FindName("FlashcardsItemsControl");
            var viewer = ControlTree(grid).OfType<ScrollViewer>().First();
            if (variant == "direct") SmoothScrollCompositor.SetDirectOffsetMode(viewer, true);
            void ClearShadowCaches()
            {
                foreach (var shadow in ControlTree(grid).OfType<CardShadowLayer>())
                    ((FrameworkElement)shadow.FindName("ShadowRoot")).CacheMode = null;
            }
            if (variant == "uncached-shadows") ClearShadowCaches();
            Require(ControlTree(grid).OfType<System.Windows.UIElement>().All(e => e.Effect is not DropShadowEffect),
                "The scrolling grid has a live shadow.");
            var samples = new List<double>(); var updateSamples = new List<double>(); var maxDrift = 0d; var maxCards = 0;
            var content = (FrameworkElement)viewer.Content;
            var originalTransform = content.RenderTransform;
            var watch = Stopwatch.StartNew();
            SmoothScrollCompositor.Begin(viewer, Array.Empty<DashboardVirtualizationHost>());
            var beginMs = watch.Elapsed.TotalMilliseconds;
            for (var index = 1; index <= 120; index++)
            {
                watch.Restart();
                var target = index * 24d;
                SmoothScrollCompositor.TryApply(viewer, 0, target, false, out _, out var committed);
                viewer.UpdateLayout(); updateSamples.Add(watch.Elapsed.TotalMilliseconds);
                if (variant == "uncached-shadows") ClearShadowCaches();
                maxDrift = Math.Max(maxDrift, Math.Abs(target - viewer.VerticalOffset));
                if (variant == "direct" || quick)
                {
                    Require(Math.Abs(target - viewer.VerticalOffset) < 1, "The virtualized viewport lagged its eased offset.");
                    Require(ReferenceEquals(content.RenderTransform, originalTransform), "Direct scrolling translated the recycled viewport.");
                }
                maxCards = Math.Max(maxCards, ControlTree(grid).OfType<FlashcardFlipHost>().Count());
                Pump(16); samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            SmoothScrollCompositor.End(viewer, 0, 2880); Pump(250);
            Require(viewer.VerticalOffset > 2800 && maxCards < 100, "Grid scrolling failed or realized the whole library.");
            if (quick)
            {
                var flip = ControlTree(grid).OfType<FlashcardFlipHost>().First(host => host.IsVisible);
                var wasFlipped = flip.IsFlipped;
                flip.AnimateToggle(240); Pump(500);
                Require(flip.IsFlipped != wasFlipped && !flip.IsAnimating, "A recycled card stopped flipping after scrolling.");
                foreach (var preference in new[] { MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(preference, false);
                    var before = viewer.VerticalOffset;
                    viewer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount, 120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent, Source = viewer });
                    Pump(50);
                    Require(viewer.VerticalOffset < before && (preference != MotionPreference.Off || !SmoothScrollCompositor.IsActive(viewer)), "The motion set did not scroll."); Pump(500); Require(!SmoothScrollCompositor.IsActive(viewer), "Scrolling did not settle.");
                }
                MotionPolicy.Configure(MotionPreference.Full, false);
            }
            samples.Sort(); updateSamples.Sort();
            var report = $"{theme}/{variant}: begin={beginMs:F1}ms; step median={samples[60]:F1}ms; p95={samples[114]:F1}ms; max={samples[^1]:F1}ms; layout p95={updateSamples[114]:F1}ms; maximum offset drift={maxDrift:F1}; maximum realized cards={maxCards}; live shadows=0";
            reports.Add(report); Console.WriteLine(report);
            if (variant == "direct" || quick) SaveControlCapture((FrameworkElement)window.Content, "flashcard-grid-" + theme);
            window.DisposeWorkspaceEditor(); Pump(150);
        }
        File.WriteAllLines(Path.Combine(_output, "flashcard-scroll-checks.txt"), reports);
        _app.Dispatcher.Hooks.OperationStarted -= OperationStarted;
        _app.Dispatcher.Hooks.OperationCompleted -= OperationCompleted;
        File.WriteAllLines(Path.Combine(_output, "slow-dispatcher-operations.txt"), slowOperations);
    }
}
