using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Scriblucent.Services;

namespace Scriblucent.Rendering;

/// <summary>
/// The dashboard card tile cache with a background render queue. Cards rasterize on the thread
/// pool via Skia (off the UI thread - the property WPF lacks and the root of the measured
/// scroll stalls), then marshal to the UI thread as frozen BitmapSources (a pixel copy, µs).
/// Scroll-time first draws therefore become texture references, not UI-thread rasterization.
/// One cache serves every dashboard card kind through its <see cref="SkiaTileRenderer"/>.
/// </summary>
internal static class SkiaCardTileCache
{
    private const int MaxEntries = 320;

    private sealed class Entry
    {
        public required string Signature;
        public required SKBitmap Bitmap;
        public required BitmapSource Source;
        public required double HeightDip;
    }

    private sealed class Request
    {
        public required object ViewModel;
        public required double Dpi;
        public required float WidthDip;
        public required SkiaTileRenderer Renderer;
        public required Action<BitmapSource>? Completed;
    }

    private static readonly Dictionary<(object Vm, double Dpi, float WidthDip, SkiaTileRenderer Renderer), Entry> Cache = new();
    private static readonly Queue<(object Vm, double Dpi, float WidthDip, SkiaTileRenderer Renderer)> Order = new();
    private static readonly ConcurrentQueue<Request> Pending = new();
    private static readonly ConcurrentDictionary<(object Vm, SkiaTileRenderer Renderer), byte> InFlight = new();
    private static int _workersStarted;

    static SkiaCardTileCache()
    {
        ThemeManager.ThemeChanged += (_, _) => Clear();
        AiFeatureGate.Changed += (_, _) => Clear();
    }

    public static void Clear()
    {
        foreach (var (_, entry) in Cache)
            entry.Bitmap.Dispose();

        Cache.Clear();
        Order.Clear();
    }

    /// <summary>
    /// Returns the cached tile for the card, or the themed placeholder and queues an off-thread
    /// render. When the render lands, <paramref name="completed"/> runs on the UI thread with
    /// the frozen BitmapSource (only if the caller is still relevant).
    /// </summary>
    public static BitmapSource GetOrRequest(object vm, double dpi, SkiaTileRenderer renderer, Action<BitmapSource>? completed, float widthDip = 0)
    {
        var signature = renderer.BuildSignature(vm);
        var key = (vm, dpi, widthDip, renderer);

        if (Cache.TryGetValue(key, out var entry) && entry.Signature == signature)
        {
            Touch(key);
            return entry.Source;
        }

        if (completed is not null)
        {
            // De-dupe in-flight renders per view model + renderer.
            if (InFlight.TryAdd((vm, renderer), 0))
            {
                Pending.Enqueue(new Request { ViewModel = vm, Dpi = dpi, WidthDip = widthDip, Renderer = renderer, Completed = completed });
                EnsureWorkers();
            }
        }

        return ToBitmapSource(renderer.RenderPlaceholder(dpi), dpi);
    }

    public static double? GetHeightDip(object vm, double dpi, SkiaTileRenderer renderer)
    {
        if (Cache.TryGetValue((vm, dpi, 0f, renderer), out var entry))
            return entry.HeightDip;

        return null;
    }

    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) == 1)
            return;

        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(RenderWorker)
            {
                IsBackground = true,
                Name = $"SkiaTileWorker-{i}",
                Priority = ThreadPriority.BelowNormal
            };
            worker.Start();
        }
    }

    private static void RenderWorker()
    {
        while (true)
        {
            if (!Pending.TryDequeue(out var request))
            {
                Thread.Sleep(4);
                continue;
            }

            SKBitmap bitmap;
            double heightDip;
            try
            {
                bitmap = request.Renderer.Render(request.ViewModel, request.Dpi, request.WidthDip, out heightDip);
            }
            catch
            {
                InFlight.TryRemove((request.ViewModel, request.Renderer), out _);
                continue;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                bitmap.Dispose();
                InFlight.TryRemove((request.ViewModel, request.Renderer), out _);
                continue;
            }

            var viewModel = request.ViewModel;
            var dpi = request.Dpi;
            var renderer = request.Renderer;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    var source = ToBitmapSource(bitmap, dpi);
                    var key = (viewModel, dpi, request.WidthDip, renderer);
                    if (Cache.TryGetValue(key, out var existing))
                        existing.Bitmap.Dispose();
                    else
                        Order.Enqueue(key);

                    Cache[key] = new Entry
                    {
                        Signature = renderer.BuildSignature(viewModel),
                        Bitmap = bitmap,
                        Source = source,
                        HeightDip = heightDip
                    };

                    while (Order.Count > MaxEntries)
                    {
                        var oldest = Order.Dequeue();
                        if (Cache.Remove(oldest, out var evicted))
                            evicted.Bitmap.Dispose();
                    }
                }
                catch
                {
                    bitmap.Dispose();
                }
                finally
                {
                    InFlight.TryRemove((viewModel, renderer), out _);
                }

                if (GetCached(viewModel, dpi, renderer, request.WidthDip) is { } cached)
                    request.Completed?.Invoke(cached);
            }));
        }
    }

    private static BitmapSource? GetCached(object vm, double dpi, SkiaTileRenderer renderer, float widthDip)
    {
        return Cache.TryGetValue((vm, dpi, widthDip, renderer), out var entry) ? entry.Source : null;
    }

    /// <summary>
    /// <paramref name="dpiScale"/> is WPF's PixelsPerDip (a scale factor, e.g. 1.0/1.5/2.0), not
    /// a raw DPI value - BitmapSource.Create wants the latter, so it's converted here.
    /// </summary>
    private static BitmapSource ToBitmapSource(SKBitmap bitmap, double dpiScale)
    {
        var dpi = 96.0 * dpiScale;
        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            bitmap.Bytes,
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static void Touch((object Vm, double Dpi, float WidthDip, SkiaTileRenderer Renderer) key)
    {
        // Re-arm the LRU order by moving the key to the tail (bounded, cheap).
        if (Order.Count <= 1)
            return;

        // Only re-queue if not already at the tail.
        var last = Order.ToArray();
        if (last.Length > 0 && last[^1].Vm == key.Vm && last[^1].Dpi == key.Dpi)
            return;

        var list = new List<(object, double, float, SkiaTileRenderer)>(Order.Count);
        while (Order.Count > 0)
        {
            var item = Order.Dequeue();
            if (item.Vm != key.Vm || item.Dpi != key.Dpi)
                list.Add(item);
        }

        list.Add(key);
        foreach (var item in list)
            Order.Enqueue(item);
    }
}

