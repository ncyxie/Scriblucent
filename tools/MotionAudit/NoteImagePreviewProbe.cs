using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Rendering;
using Scriblucent.Services;
using SkiaSharp;
using Scriblucent.Models;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeNoteImagePreview(bool baseline)
    {
        var cases = new List<(string Name, string Text, string Format, double Font, double Top, int Photos, int Expected)>
        {
            ("plain-near", "A short note with a picture beside its opening.", "plain", 14, 0, 1, 1),
            ("plain-two", "Two pictures beside this note.", "plain", 14, 0, 2, 2),
            ("plain-far", "A short opening.", "plain", 14, 5000, 1, 0),
            ("rtf-near", "The opening of a formatted note.", "rtf", 14, 0, 1, 1),
            ("rtf-two", "Two pictures beside this note.", "rtf", 14, 0, 2, 2),
            ("package-near", "The opening of a formatted note.", "package", 14, 0, 1, 1),
            ("large-font", "A large heading with a picture alongside it.", "package", 48, 32, 1, 1),
            ("paragraphs", "First line.\nSecond line.\nThird line.", "package", 14, 35, 1, 1),
            ("photo-only", "", "plain", 14, 5000, 3, 3),
            ("long-opening", string.Join(" ", Enumerable.Repeat("Opening words", 150)), "package", 14, 5000, 1, 0),
            ("inline-package", "Before \uFFFC after the image.", "package", 14, 0, 1, 1),
            ("mixed-inline-floating", "Before \uFFFC after the image.", "package", 14, 120, 2, 2),
            ("legacy-marker", "Before [[ScriblucentImage:abc]] after.", "plain", 14, 0, 0, 1),
            ("missing-image-file", "A note whose picture file is unavailable.", "plain", 14, 0, 1, 1),
            ("invalid-dimensions", "Invalid image geometry must not create a chip.", "plain", 14, 0, 1, 0),
        };
        var report = new List<object>();
        foreach (var fixture in cases)
        {
            string Encode()
            {
                if (fixture.Format == "plain") return fixture.Text;
                var flow = new FlowDocument { FontSize = fixture.Font };
                foreach (var line in fixture.Text.Split('\n')) flow.Blocks.Add(new Paragraph(new Run(line)));
                using var stream = new MemoryStream();
                new TextRange(flow.ContentStart, flow.ContentEnd).Save(stream,
                    fixture.Format == "rtf" ? DataFormats.Rtf : DataFormats.XamlPackage);
                return Convert.ToBase64String(stream.ToArray());
            }
            var content = Encode();
            NoteCardViewModel Create() => new(new NoteDocument
            {
                Title = fixture.Name, Content = content, FontSize = fixture.Font,
                Images = Enumerable.Range(0, fixture.Photos).Select(i => new NoteImageAttachment
                {
                    WrapMode = fixture.Name == "inline-package" || fixture.Name == "mixed-inline-floating" && i == 0
                        ? NoteImageWrapMode.Inline : NoteImageWrapMode.None,
                    Left = i * 90, Top = fixture.Top, Width = fixture.Name == "invalid-dimensions" ? 0 : 80,
                    Height = fixture.Name == "mixed-inline-floating" && i == 0 ? 200 : 60,
                    DataFilePath = fixture.Name == "missing-image-file" ? "unavailable.png" : ""
                }).ToList()
            }, _ => { });
            // Prime JIT/font/package machinery separately from the measured iterations.
            _ = Create().Content;
            var cold = new List<double>(); var allocated = new List<long>();
            var detected = 0;
            for (var i = 0; i < 25; i++)
            {
                var vm = Create(); var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp(); var preview = vm.Content;
                cold.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                allocated.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                detected = preview.Count(c => c == '\uFFFC');
            }
            var warmVm = Create(); _ = warmVm.Content;
            var warmBefore = GC.GetAllocatedBytesForCurrentThread(); var warmStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < 10000; i++) _ = warmVm.Content;
            var warmUs = Stopwatch.GetElapsedTime(warmStart).TotalMicroseconds / 10000;
            var warmBytes = GC.GetAllocatedBytesForCurrentThread() - warmBefore;
            cold.Sort(); allocated.Sort();
            report.Add(new { fixture.Name, fixture.Expected, Detected = detected,
                ColdMedianMs = cold[12], ColdP95Ms = cold[23], MedianAllocatedBytes = allocated[12],
                WarmMicroseconds = warmUs, WarmTotalAllocatedBytes = warmBytes });
            Console.WriteLine($"{fixture.Name}: expected={fixture.Expected} detected={detected}; cold median={cold[12]:F3}ms p95={cold[23]:F3}ms; warm={warmUs:F3}us");
            if (!baseline) Require(detected == fixture.Expected, $"Incorrect image detection: {fixture.Name}");
        }
        File.WriteAllText(Path.Combine(_output, baseline ? "preview-baseline.json" : "preview-benchmark.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!baseline) { BenchmarkPreviewLibraries(); CaptureNotePreviewScenes(); }
    }

    private static void BenchmarkPreviewLibraries()
    {
        var reports = new List<object>();
        foreach (int size in new[] { 1000, 20000 })
        foreach (bool stored in new[] { false, true })
        {
            var text = "A note with an image beside its opening, followed by more text for the preview.";
            var seed = new NoteDocument { Content = text,
                Images = [new NoteImageAttachment { Width = 120, Height = 80, WrapMode = NoteImageWrapMode.Square }] };
            var seedPreview = NotePreviewService.Build(seed);
            var start = Stopwatch.GetTimestamp();
            var cards = Enumerable.Range(0, size).Select(i => new NoteCardViewModel(new NoteDocument
            {
                Title = "Note " + i, Content = text, Images = seed.Images,
                CardPreview = stored ? seedPreview : null
            }, _ => { })).ToArray();
            var hydrationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Require(cards.All(c => !c.IsContentPreviewCached), "Library construction eagerly prepared previews.");
            var durations = new List<double>(); var operations = new Dictionary<DispatcherOperation, long>();
            DispatcherHookEventHandler began = (_, args) =>
            { if (args.Operation.Priority == DispatcherPriority.ApplicationIdle) operations[args.Operation] = Stopwatch.GetTimestamp(); };
            DispatcherHookEventHandler ended = (_, args) =>
            { if (operations.Remove(args.Operation, out var time)) durations.Add(Stopwatch.GetElapsedTime(time).TotalMilliseconds); };
            _app.Dispatcher.Hooks.OperationStarted += began; _app.Dispatcher.Hooks.OperationCompleted += ended;
            var leases = new List<CardTileRequestLease>(); int completed = 0;
            start = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < 48; i++)
                {
                    var lease = new CardTileRequestLease(); leases.Add(lease);
                    NotePreviewWarmQueue.Enqueue(cards[i], () => completed++, lease: lease);
                }
                // Leaving a viewport before the idle work begins must cancel extraction.
                for (int i = 100; i < 116; i++)
                {
                    var abandoned = new CardTileRequestLease();
                    NotePreviewWarmQueue.Enqueue(cards[i], () => throw new InvalidOperationException("Abandoned preview callback"), lease: abandoned);
                    abandoned.Dispose();
                }
                var deadline = Stopwatch.StartNew();
                while (completed < 48 && deadline.ElapsedMilliseconds < 10000) Pump(5);
                Require(completed == 48, "Viewport previews did not finish.");
            }
            finally
            {
                foreach (var lease in leases) lease.Dispose();
                _app.Dispatcher.Hooks.OperationStarted -= began; _app.Dispatcher.Hooks.OperationCompleted -= ended;
            }
            var viewportMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var prepared = cards.Count(c => c.IsContentPreviewCached);
            Require(prepared == 48 && NotePreviewWarmQueue.PendingCount == 0, "Offscreen previews were prepared or retained.");
            var before = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 100000; i++) _ = cards[i % 48].Content;
            var warmUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 100000;
            var warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            durations.Sort();
            reports.Add(new { Notes = size, StoredSnapshot = stored, HydrationMs = hydrationMs, ViewportMs = viewportMs,
                Prepared = prepared, Offscreen = size - prepared, IdleP95Ms = durations.ElementAtOrDefault((int)(durations.Count * .95)),
                IdleMaxMs = durations.LastOrDefault(), WarmMicroseconds = warmUs, WarmAllocatedBytes = warmBytes });
            Console.WriteLine($"library {size} stored={stored}: prepared={prepared}, hydration={hydrationMs:F2}ms, viewport={viewportMs:F2}ms, idle max={durations.LastOrDefault():F2}ms, warm allocations={warmBytes} bytes");
        }
        File.WriteAllText(Path.Combine(_output, "library-benchmark.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CaptureNotePreviewScenes()
    {
        var report = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            _app.Resources["NoteCardContextMenu"] = new ContextMenu();
            NoteImageAttachment Photo(NoteImageWrapMode mode, double top = 0) => new()
                { Width = 120, Height = 90, Top = top, WrapMode = mode };
            NoteCardViewModel Card(string title, string text, bool tags, params NoteImageAttachment[] photos)
                => new(new NoteDocument { Title = title, Content = text, Images = photos.ToList(), Tags = tags ? ["reference"] : [] }, _ => { });
            var models = new[]
            {
                Card("Images in context", "Before \uFFFC, then an explanation of the image and the next idea.", false, Photo(NoteImageWrapMode.Inline)),
                Card("Nearby pictures", "Two floating pictures beside the opening of this note.", true, Photo(NoteImageWrapMode.None), Photo(NoteImageWrapMode.Square)),
                Card("Just pictures", "", false, Photo(NoteImageWrapMode.None, 5000), Photo(NoteImageWrapMode.Square, 5000), Photo(NoteImageWrapMode.Inline)),
                Card("Outside the excerpt", "A text preview with an image much further down. That picture should not add a chip here.", false, Photo(NoteImageWrapMode.None, 5000)),
                Card("Separate positions", "First \uFFFC, some text, then \uFFFC and a short conclusion.", true, Photo(NoteImageWrapMode.Inline), Photo(NoteImageWrapMode.Inline)),
                Card("Boundary clipping", "Long words approaching the end of the available line before \uFFFC and more text after the picture. Extra text continues here.", false, Photo(NoteImageWrapMode.Inline))
            };
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(18) };
            var controls = models.Select(vm => new NoteCardControl { DataContext = vm, Height = 140 }).ToArray();
            foreach (var control in controls) grid.Children.Add(control);
            var root = new Grid { Background = (Brush)_app.FindResource("WindowBackground"), Children = { grid } };
            var window = new Window { Content = root, Width = 790, Height = 420, ShowInTaskbar = false, UseLayoutRounding = true };
            window.Show(); Pump(150);
            try
            {
                SaveRecentTintCapture(root, $"notes-{theme}-{language}-live");
                foreach (var control in controls)
                {
                    var preview = (NotePreviewPresenter)control.FindName("ContentPreview");
                    Require(preview.ActualWidth is >= 189 and <= 192, $"Preview width drifted: {preview.ActualWidth}");
                    var layout = NotePreviewRaster.Layout(preview.Preview!, (float)preview.ActualWidth, preview.HasTags ? 2 : 3, 1);
                    Require(Math.Abs(layout.Height - preview.ActualHeight) <= 1, "Preview clips a partial line.");
                }
                foreach (var dpi in new[] { 1d, 1.5d, 2d })
                {
                    using var sheet = new SKBitmap((int)(244 * 3 * dpi), (int)(164 * 2 * dpi), SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var canvas = new SKCanvas(sheet); canvas.Clear(theme == "Dark" ? new SKColor(24, 24, 24) : new SKColor(245, 245, 245));
                    var timings = new List<double>();
                    for (int i = 0; i < models.Length; i++)
                    {
                        var time = Stopwatch.GetTimestamp();
                        using var tile = SkiaTileRenderers.Notes.Render(models[i], dpi, 0, out var height);
                        timings.Add(Stopwatch.GetElapsedTime(time).TotalMilliseconds);
                        Require(Math.Abs(height - 164) < 1, "Preview changed the scrolling cell height.");
                        canvas.DrawBitmap(tile, (float)((i % 3) * 244 * dpi), (float)((i / 3) * 164 * dpi), SkiaBitmapUtil.LinearSampling);
                    }
                    using var image = SKImage.FromBitmap(sheet); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var file = File.Create(Path.Combine(_output, $"notes-{theme}-{language}-tiles-{dpi * 100:0}.png")); data.SaveTo(file);
                    report.Add($"{theme}/{language}/{dpi * 100:0}%: tile max {timings.Max():F3}ms, fixed 164 DIP cells, atomic chips");
                }
                foreach (var control in controls) control.IsBitmapMode = true;
                var wait = Stopwatch.StartNew();
                var sceneDpi = VisualTreeHelper.GetDpi(root).PixelsPerDip;
                while (models.Any(vm => SkiaCardTileCache.GetReady(vm, sceneDpi, SkiaTileRenderers.Notes, 0) is null) && wait.ElapsedMilliseconds < 3000) Pump(10);
                Require(models.All(vm => SkiaCardTileCache.GetReady(vm, sceneDpi, SkiaTileRenderers.Notes, 0) is not null), "Scrolling displayed a placeholder instead of the rendered preview.");
                Pump(150); SaveRecentTintCapture(root, $"notes-{theme}-{language}-scroll");
                foreach (var control in controls) control.IsBitmapMode = false;
                Pump(50);
                Require(controls.All(c => ((NotePreviewPresenter)c.FindName("ContentPreview")).Preview is not null), "Preview disappeared after scrolling.");
                controls[0].DataContext = models[2]; Pump(30);
                Require(ReferenceEquals(((NotePreviewPresenter)controls[0].FindName("ContentPreview")).Preview, models[2].Preview), "Recycled card retained a different note's preview.");
            }
            finally { window.Close(); Pump(50); SkiaCardTileCache.Clear(); }
        }
        File.WriteAllLines(Path.Combine(_output, "visual-checks.txt"), report);
    }
}
