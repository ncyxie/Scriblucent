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

    private const long MaxBytes = 64 * 1024 * 1024;
    private readonly record struct CacheKey(Guid MapId, int Revision, float Zoom, float Dpi,
        string Theme, double Width, double Height);
    private sealed class TileRequest
    {
        public required CacheKey Key;
        public required MindMapTileSnapshot Snapshot;
        public required System.Windows.Threading.Dispatcher Dispatcher;
        public readonly List<Action<SnapshotResult?>> Callbacks = new();
        public volatile bool Superseded;
    }
    public sealed class SnapshotResult
    {
        public required System.Windows.Media.Imaging.BitmapSource Image;
        public required float RenderScale;
        internal long Bytes => (long)Image.PixelWidth * Image.PixelHeight * 4;
    }
    // Cache and subscribers belong to the requesting UI dispatcher. Workers receive immutable data.
    private static readonly Dictionary<CacheKey, SnapshotResult> Cache = new();
    private static readonly Queue<CacheKey> Order = new();
    private static readonly Dictionary<CacheKey, TileRequest> InFlight = new();
    private static readonly Dictionary<Guid, TileRequest> Latest = new();
    private static readonly BlockingCollection<TileRequest> Pending = new();
    private static long _cacheBytes;
    private static int _workersStarted;

    public static void Clear()
    {
        Cache.Clear(); Order.Clear(); _cacheBytes = 0;
        var requests = InFlight.Values.ToArray();
        InFlight.Clear(); Latest.Clear();
        foreach (var request in requests) Cancel(request);
    }

    internal static void Release(Guid mapId)
    {
        if (Latest.Remove(mapId, out var request))
        { InFlight.Remove(request.Key); Cancel(request); }
        foreach (var key in Cache.Keys.Where(key => key.MapId == mapId).ToArray())
            if (Cache.Remove(key, out var image)) _cacheBytes -= image.Bytes;
        var retained = Order.Where(Cache.ContainsKey).ToArray();
        Order.Clear(); foreach (var key in retained) Order.Enqueue(key);
    }

    public static void Request(MindMapTileSnapshot snapshot, float zoom, float dpi, Action<SnapshotResult?>? completed)
    {
        var key = new CacheKey(snapshot.MapId, snapshot.Revision, zoom, dpi,
            ThemeManager.CurrentTheme ?? string.Empty, snapshot.CanvasWidthDip, snapshot.CanvasHeightDip);
        if (Latest.TryGetValue(snapshot.MapId, out var previous) && previous.Key != key)
        {
            InFlight.Remove(previous.Key); Latest.Remove(snapshot.MapId); Cancel(previous);
        }
        if (Cache.TryGetValue(key, out var cached)) { completed?.Invoke(cached); return; }
        if (!InFlight.TryGetValue(key, out var request))
        {
            request = new TileRequest { Key = key, Snapshot = snapshot,
                Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher };
            InFlight.Add(key, request); Latest[snapshot.MapId] = request;
            Pending.Add(request);
            EnsureWorkers();
        }
        if (completed is not null) request.Callbacks.Add(completed);
    }

    private static void Cancel(TileRequest request)
    {
        request.Superseded = true;
        Notify(request, null);
    }
    private static void Notify(TileRequest request, SnapshotResult? result)
    {
        var callbacks = request.Callbacks.ToArray(); request.Callbacks.Clear();
        foreach (var callback in callbacks)
        {
            try { callback(result); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
    }
    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) == 1) return;
        for (var i = 0; i < 2; i++)
            new Thread(RenderWorker) { IsBackground = true, Name = $"SkiaMindMapWorker-{i}",
                Priority = ThreadPriority.BelowNormal }.Start();
    }
    private static void RenderWorker()
    {
        // Blocking consumption sleeps between requests; there is no idle polling loop.
        foreach (var request in Pending.GetConsumingEnumerable())
        {
            if (request.Superseded) continue;
            SnapshotResult? result = null;
            try
            {
                using var bitmap = SkiaMindMapRenderer.RenderFull(request.Snapshot, request.Key.Zoom,
                    request.Key.Dpi, out var scale);
                if (!request.Superseded)
                    result = new SnapshotResult { Image = ToBitmapSource(bitmap, request.Key.Dpi), RenderScale = scale };
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            if (!request.Dispatcher.HasShutdownStarted)
                request.Dispatcher.BeginInvoke(() => Complete(request, result));
        }
    }
    private static void Complete(TileRequest request, SnapshotResult? result)
    {
        if (request.Superseded) return;
        InFlight.Remove(request.Key);
        if (Latest.TryGetValue(request.Key.MapId, out var latest) && ReferenceEquals(latest, request))
            Latest.Remove(request.Key.MapId);
        if (result is not null)
        {
            Cache[request.Key] = result; Order.Enqueue(request.Key); _cacheBytes += result.Bytes;
            while (Order.Count > MaxEntries || _cacheBytes > MaxBytes && Order.Count > 1)
                if (Cache.Remove(Order.Dequeue(), out var old)) _cacheBytes -= old.Bytes;
        }
        Notify(request, result);
    }

    /// <summary>Marshal an SKBitmap (premultiplied BGRA) into a frozen WPF BitmapSource on the
    /// requesting thread and freeze it for WPF. <paramref name="dpiScale"/> is PixelsPerDip.</summary>
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
            bitmap.GetPixels(),
            checked(bitmap.RowBytes * bitmap.Height),
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}
