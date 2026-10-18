using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeFlashcardFrames()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var reports = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var variant in new[] { "legacy", "current", "legacy-repeat", "current-repeat" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Automatic, false);
            var window = new FlashcardsPreviewWindow(Enumerable.Range(0, 24).Select(i => new FlashcardItem
                { Question = $"Question {i}: plain text in a small set", Answer = $"Answer {i}", Category = "Practice", Hint = "A clue" }));
            var grid = (FlashcardGridItemsControl)window.FindName("FlashcardsItemsControl");
            var legacy = variant.StartsWith("legacy");
            // Recreate only the former opacity/cache policy, including newly recycled cards.
            if (legacy)
            {
                var style = new Style(typeof(CardShadowLayer));
                style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
                {
                    var root = (Grid)((CardShadowLayer)sender).FindName("ShadowRoot");
                    root.Opacity = .58; foreach (var part in root.Children.OfType<Image>()) part.Opacity = 1;
                    root.CacheMode = DpiAwareBitmapCache.Create(root, enableClearType:false, snapsToDevicePixels:false);
                })));
                window.Resources[typeof(CardShadowLayer)] = style;
            }
            window.Show(); window.Activate(); Pump(1000);
            var viewer = ControlTree(grid).OfType<ScrollViewer>().First();
            var frameGaps = new List<double>(); var clock = Stopwatch.StartNew(); double last = -1;
            var renderTime = TimeSpan.MinValue;
            EventHandler frame = (_, args) =>
            {
                if (args is RenderingEventArgs re && re.RenderingTime == renderTime) return;
                if (args is RenderingEventArgs rendered) renderTime = rendered.RenderingTime;
                var now = clock.Elapsed.TotalMilliseconds;
                if (last >= 0) frameGaps.Add(now - last); last = now;
            };
            var ticks = 0; double highestOffset = 0;
            var input = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
            input.Tick += (_, _) =>
            {
                var cycle = ticks++ % 40;
                viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, cycle < 20 ? -120 : 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
                highestOffset = Math.Max(highestOffset, viewer.VerticalOffset);
            };
            CompositionTarget.Rendering += frame; input.Start(); Pump(4500);
            input.Stop(); CompositionTarget.Rendering -= frame;
            var measuredSeconds = clock.Elapsed.TotalSeconds;
            Pump(500);
            Require(frameGaps.Count > 20 && highestOffset > 500, "The 24-card wheel probe did not scroll.");
            frameGaps.Sort();
            var report = $"{theme}/{variant}; elapsed={measuredSeconds:F1}s; frames={frameGaps.Count}; median={frameGaps[frameGaps.Count / 2]:F1}ms; p95={frameGaps[(int)(frameGaps.Count * .95)]:F1}ms; max={frameGaps[^1]:F1}ms; gaps>33ms={frameGaps.Count(x => x > 33)}; highest offset={highestOffset:F0}";
            reports.Add(report); Console.WriteLine(report);
            if (variant == "current")
            {
                VerifyShadowAlpha(grid, theme, reports);
                SaveControlCapture((FrameworkElement)window.Content, "flashcard-grid-" + theme);
            }
            window.DisposeWorkspaceEditor(); Pump(200);
        }
        File.WriteAllLines(Path.Combine(_output, "flashcard-frame-checks.txt"), reports);
    }

    private static void VerifyShadowAlpha(FrameworkElement grid, string theme, List<string> reports)
    {
        var shadow = ControlTree(grid).OfType<CardShadowLayer>().First(s => s.IsVisible);
        var root = (Grid)shadow.FindName("ShadowRoot");
        byte[] Raster(double scale)
        {
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
        }
        Require(root.CacheMode is null && root.Opacity == 1, "The per-card shadow surface remains.");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            root.Opacity = .58; foreach (var part in root.Children.OfType<Image>()) part.Opacity = 1;
            var before = Raster(scale);
            root.Opacity = 1; foreach (var part in root.Children.OfType<Image>()) part.Opacity = .58;
            var after = Raster(scale);
            double error = 0; var peak = 0;
            for (var i = 3; i < before.Length; i += 4)
            { var difference = Math.Abs(before[i] - after[i]); error += difference; peak = Math.Max(peak,difference); }
            var mean = error / (before.Length / 4d);
            Require(mean < .5 && peak <= 3, "The optimized shadow changed its opacity/contour.");
            var result = $"{theme}/shadow-alpha @{scale:P0}: mean difference={mean:F4}/255; peak={peak}/255";
            reports.Add(result); Console.WriteLine(result);
        }
    }
}
