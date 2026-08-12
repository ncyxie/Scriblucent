using System.Collections.Concurrent;
using SkiaSharp;

namespace Scriblucent.Rendering;

/// <summary>
/// Zoom-session snapshot cache for the mind-map canvas: one whole-canvas bitmap per (map
/// revision, zoom, dpi, theme), rendered off the UI thread via <see cref="SkiaMindMapRenderer"/>.
/// During a zoom session the snapshot scales with the same LayoutTransform as the live canvas
/// (continuous gesture, no per-level swaps); a fresh snapshot renders at settle and the live
/// canvas returns.
/// </summary>
internal static class SkiaMindMapSnapshotCache
{
    private const int MaxEntries = 48;

    private sealed class TileRequest
    {
        public required MindMapTileSnapshot Snapshot;
        public required float Zoom;
        public required float Dpi;
        public required Action<SnapshotResult>? Completed;
    }

    public sealed class SnapshotResult
    {
        public required SKBitmap Bitmap;
        public required float RenderScale;
    }

    private static readonly Dictionary<(int Revision, string ZoomKey, string DpiKey, string Theme), (SKBitmap Bitmap, float RenderScale)> Cache = new();
    private static readonly Queue<(int, string, string, string)> Order = new();
    private static readonly ConcurrentQueue<TileRequest> Pending = new();
    private static readonly ConcurrentDictionary<(int Revision, string ZoomKey, string Theme), byte> InFlight = new();
    private static int _workersStarted;

    public static void Clear()
    {
        foreach (var (bitmap, _) in Cache.Values)
            bitmap.Dispose();
        Cache.Clear();
        Order.Clear();
    }

    public static void Request(MindMapTileSnapshot snapshot, float zoom, float dpi, Action<SnapshotResult>? completed)
    {
        var zoomKey = zoom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var dpiKey = dpi.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var theme = ThemeManager.CurrentTheme ?? string.Empty;
        var key = (snapshot.Revision, zoomKey, dpiKey, theme);

        if (Cache.TryGetValue(key, out var cached))
        {
            completed?.Invoke(new SnapshotResult { Bitmap = cached.Bitmap, RenderScale = cached.RenderScale });
            return;
        }

        // De-dupe in-flight renders; a null callback still warms the cache off-thread.
        if (InFlight.TryAdd((snapshot.Revision, zoomKey, theme), 0))
        {
            Pending.Enqueue(new TileRequest
            {
                Snapshot = snapshot,
                Zoom = zoom,
                Dpi = dpi,
                Completed = completed,
            });
            EnsureWorkers();
        }
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
                Name = $"SkiaMindMapWorker-{i}",
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
            float renderScale;
            try
            {
                bitmap = SkiaMindMapRenderer.RenderFull(request.Snapshot, request.Zoom, request.Dpi, out renderScale);
            }
            catch
            {
                InFlight.TryRemove(KeyFor(request), out _);
                continue;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                bitmap.Dispose();
                InFlight.TryRemove(KeyFor(request), out _);
                continue;
            }

            var zoomKey = request.Zoom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var dpiKey = request.Dpi.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var theme = ThemeManager.CurrentTheme ?? string.Empty;
            var revision = request.Snapshot.Revision;

            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    var key = (revision, zoomKey, dpiKey, theme);
                    if (Cache.TryGetValue(key, out var existing))
                        existing.Bitmap.Dispose();
                    else
                        Order.Enqueue(key);

                    Cache[key] = (bitmap, renderScale);

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
                    InFlight.TryRemove(KeyFor(request), out _);
                }

                request.Completed?.Invoke(new SnapshotResult { Bitmap = bitmap, RenderScale = renderScale });
            }));
        }
    }

    private static (int, string, string) KeyFor(TileRequest request)
    {
        var zoomKey = request.Zoom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var theme = ThemeManager.CurrentTheme ?? string.Empty;
        return (request.Snapshot.Revision, zoomKey, theme);
    }

    /// <summary>Marshal an SKBitmap (premultiplied BGRA) into a frozen WPF BitmapSource on the
    /// UI thread. <paramref name="dpiScale"/> is WPF's PixelsPerDip.</summary>
    public static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(SKBitmap bitmap, float dpiScale)
    {
        var dpi = 96.0 * dpiScale;
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            dpi,
            dpi,
            System.Windows.Media.PixelFormats.Pbgra32,
            null,
            bitmap.Bytes,
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}
