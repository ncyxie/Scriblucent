using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Rendering;

/// <summary>
/// Shared card tile cache with two background rasterization workers. Workers produce frozen
/// BitmapSources; the requesting dispatcher checks revision and generation before publishing.
/// Rich editor cards supply frozen native paint and retain their live input tree.
/// </summary>
internal static class SkiaCardTileCache
{
    private const int MaxEntries = 320;

    private readonly record struct CacheKey(object Vm, double Dpi, float WidthDip, SkiaTileRenderer Renderer);

    private sealed class Entry
    {
        public required string Signature;
        public required BitmapSource Source;
    }

    private sealed class Request
    {
        public required object ViewModel;
        public required double Dpi;
        public required float WidthDip;
        public required SkiaTileRenderer Renderer;
        public required object Input;
        public required string Signature;
        public required int Generation;
        public required Dispatcher Dispatcher;
        public readonly Dictionary<CardTileRequestLease, Action<BitmapSource>?> Completed = new();
        public LinkedListNode<Request>? QueueNode;
        public DispatcherOperation? Publication;
        public volatile bool Cancelled;
    }

    private static readonly Dictionary<CacheKey, Entry> Cache = new();
    private static readonly LinkedList<CacheKey> Order = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheKey>> Nodes = new();
    private static readonly object QueueGate = new();
    private static readonly LinkedList<Request> Pending = new();
    internal static int PendingCount { get { lock (QueueGate) return Pending.Count; } }
    internal static event Action? Invalidated;
    private static readonly ConcurrentDictionary<CacheKey, Request> InFlight = new();
    private static int _workersStarted;
    private static int _generation;
    private static int _failedRenders;
    internal static int FailedRenders => Volatile.Read(ref _failedRenders);
    internal static long FlashcardBytes => Cache.Where(p => ReferenceEquals(p.Key.Renderer, SkiaTileRenderers.FlashcardGridCard))
        .Sum(p => (long)p.Value.Source.PixelWidth * p.Value.Source.PixelHeight * 4);

    static SkiaCardTileCache()
    {
        ThemeManager.ThemeChanged += (_, _) => Clear();
        AiFeatureGate.Changed += (_, _) => Clear();
        Scriblucent.Localization.LocalizationService.CultureChanged += (_, _) => Clear();
    }

    public static void Clear()
    {
        Interlocked.Increment(ref _generation);
        foreach (var pair in InFlight.ToArray()) CancelRequest(pair.Key, pair.Value);
        Cache.Clear();
        Order.Clear();
        Nodes.Clear();
        Invalidated?.Invoke();
    }

    /// <summary>
    /// Returns the cached tile for the card, or the themed placeholder and queues an off-thread
    /// render. When the render lands, <paramref name="completed"/> runs on the UI thread with
    /// the frozen BitmapSource (only if the caller is still relevant).
    /// Note cards whose preview is not cached yet stay on the placeholder until an idle slice
    /// decodes the preview; the worker never builds a FlowDocument.
    /// </summary>
    public static BitmapSource GetOrRequest(object vm, double dpi, SkiaTileRenderer renderer, Action<BitmapSource>? completed, float widthDip = 0, CardTileRequestLease? lease = null)
    {
        lease ??= new CardTileRequestLease();
        if (lease.IsDisposed) return CreatePlaceholder(renderer, dpi);
        if (vm is NoteCardViewModel note
            && ReferenceEquals(renderer, SkiaTileRenderers.Notes)
            && !note.IsContentPreviewCached)
        {
            if (completed is not null)
            {
                NotePreviewWarmQueue.Enqueue(note, () =>
                {
                    var ready = GetOrRequest(note, dpi, renderer, completed, widthDip, lease);
                    if (Contains(note, dpi, widthDip, renderer))
                        completed(ready);
                }, lease: lease);
            }

            return CreatePlaceholder(renderer, dpi);
        }

        return GetReadyOrRequest(vm, dpi, renderer, completed, widthDip, () => completed is null ? null : vm, lease) ?? CreatePlaceholder(renderer, dpi);
    }

    /// <summary>Returns matching cached paint without capturing or scheduling a render.</summary>
    internal static BitmapSource? GetReady(object vm, double dpi, SkiaTileRenderer renderer, float widthDip)
        => GetReady(new CacheKey(vm, dpi, widthDip, renderer), renderer.BuildSignature(vm));

    private static BitmapSource? GetReady(CacheKey key, string signature)
    {
        if (!Cache.TryGetValue(key, out var entry) || entry.Signature != signature) return null;
        Touch(key);
        return entry.Source;
    }

    // Rich editors submit completed idle snapshots here. Workers receive frozen paint;
    // scrolling hosts use GetReady and never enter this request path.
    internal static BitmapSource? GetReadyOrRequest(object vm, double dpi, SkiaTileRenderer renderer,
        Action<BitmapSource>? completed, float widthDip, Func<object?> capture, CardTileRequestLease? lease = null)
    {
        lease ??= new CardTileRequestLease();
        if (lease.IsDisposed) return null;
        var signature = renderer.BuildSignature(vm);
        var key = new CacheKey(vm, dpi, widthDip, renderer);
        if (GetReady(key, signature) is { } ready) return ready;
        if (InFlight.TryGetValue(key, out var pending) && pending.Signature == signature && pending.Generation == _generation)
        {
            Subscribe(key, pending, lease, completed);
            return null;
        }
        if (pending is not null) CancelRequest(key, pending);
        if (ReferenceEquals(renderer, SkiaTileRenderers.FlashcardGridCard)
            && InFlight.Keys.Count(k => ReferenceEquals(k.Renderer, renderer)) >= 32) return null;
        if (capture() is not { } input) return null;
        var request = new Request { ViewModel = vm, Dpi = dpi, WidthDip = widthDip, Renderer = renderer,
            Input = input, Signature = signature, Generation = _generation, Dispatcher = Dispatcher.CurrentDispatcher };
        InFlight[key] = request;
        Subscribe(key, request, lease, completed);
        lock (QueueGate)
        {
            request.QueueNode = Pending.AddLast(request);
            Monitor.Pulse(QueueGate);
        }
        EnsureWorkers(); return null;
    }

    internal static void Cancel(object vm, SkiaTileRenderer renderer)
    {
        foreach (var pair in InFlight)
            if (ReferenceEquals(pair.Key.Vm, vm) && ReferenceEquals(pair.Key.Renderer, renderer)) CancelRequest(pair.Key, pair.Value);
    }

    private static void Subscribe(CacheKey key, Request request, CardTileRequestLease lease, Action<BitmapSource>? callback)
    {
        request.Completed[lease] = callback;
        lease.Attach(() =>
        {
            request.Completed.Remove(lease);
            if (request.Completed.Count == 0) CancelRequest(key, request);
        });
    }

    private static void RemoveFlight(CacheKey key, Request request) =>
        ((ICollection<KeyValuePair<CacheKey, Request>>)InFlight).Remove(new(key, request));

    private static void CancelRequest(CacheKey key, Request request)
    {
        request.Cancelled = true;
        Volatile.Read(ref request.Publication)?.Abort();
        RemoveFlight(key, request);
        lock (QueueGate)
        {
            if (request.QueueNode is { } node) Pending.Remove(node);
            request.QueueNode = null;
        }
        foreach (var lease in request.Completed.Keys) lease.Detach();
        request.Completed.Clear();
    }

    internal static void Forget(object vm, SkiaTileRenderer renderer)
    {
        Cancel(vm, renderer);
        foreach (var key in Cache.Keys.Where(k => ReferenceEquals(k.Vm, vm) && ReferenceEquals(k.Renderer, renderer)).ToArray())
        { Cache.Remove(key); if (Nodes.Remove(key, out var node)) Order.Remove(node); }
    }

    private static BitmapSource CreatePlaceholder(SkiaTileRenderer renderer, double dpi)
    {
        var bitmap = renderer.RenderPlaceholder(dpi);
        try
        {
            return ToBitmapSource(bitmap, dpi);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static bool Contains(object vm, double dpi, float widthDip, SkiaTileRenderer renderer)
        => Cache.ContainsKey(new CacheKey(vm, dpi, widthDip, renderer));

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
            Request request;
            lock (QueueGate)
            {
                while (Pending.Count == 0) Monitor.Wait(QueueGate);
                request = Pending.First!.Value;
                Pending.RemoveFirst(); request.QueueNode = null;
            }
            var key = new CacheKey(request.ViewModel, request.Dpi, request.WidthDip, request.Renderer);
            if (request.Cancelled || request.Generation != _generation) continue;
            BitmapSource? source = null;
            try
            {
                using var bitmap = request.Input is FrozenCardDrawing drawing
                    ? drawing.Render(request.Dpi, out _)
                    : request.Renderer.Render(request.Input, request.Dpi, request.WidthDip, out _);
                if (!request.Cancelled && request.Generation == _generation) source = ToBitmapSource(bitmap, request.Dpi);
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"Card tile render failed: {error}");
                Interlocked.Increment(ref _failedRenders);
            }
            if (request.Cancelled) continue;
            if (request.Dispatcher.HasShutdownStarted) { RemoveFlight(key, request); continue; }
            try
            {
                var publication = request.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => Complete(key, request, source)));
                Volatile.Write(ref request.Publication, publication);
                if (request.Cancelled) publication.Abort();
            }
            catch (InvalidOperationException) { RemoveFlight(key, request); }
        }
    }

    private static void Complete(CacheKey key, Request request, BitmapSource? source)
    {
        request.Publication = null;
        if (request.Cancelled) return;
        if (source is null || request.Generation != _generation
            || !InFlight.TryGetValue(key, out var current) || !ReferenceEquals(current, request)
            || request.Renderer.BuildSignature(request.ViewModel) != request.Signature)
        { CancelRequest(key, request); return; }
        Remember(key, new Entry { Signature = request.Signature, Source = source });
        RemoveFlight(key, request);
        var subscribers = request.Completed.ToArray(); request.Completed.Clear();
        foreach (var pair in subscribers) pair.Key.Detach();
        foreach (var pair in subscribers)
            if (!pair.Key.IsDisposed) pair.Value?.Invoke(source);
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
            bitmap.AlphaType == SKAlphaType.Premul ? PixelFormats.Pbgra32 : PixelFormats.Bgra32,
            null,
            bitmap.Bytes,
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static void Remember(CacheKey key, Entry entry)
    {
        Cache[key] = entry;
        Touch(key);
        while (Order.Count > MaxEntries && Order.First is { } oldest)
        {
            Order.RemoveFirst();
            Nodes.Remove(oldest.Value);
            Cache.Remove(oldest.Value);
        }
        while (FlashcardBytes > 32L * 1024 * 1024)
        {
            var oldest = Order.FirstOrDefault(k => ReferenceEquals(k.Renderer, SkiaTileRenderers.FlashcardGridCard));
            if (!Cache.Remove(oldest)) break;
            if (Nodes.Remove(oldest, out var node)) Order.Remove(node);
        }
    }

    private static void Touch(CacheKey key)
    {
        if (Nodes.TryGetValue(key, out var node))
        {
            if (node == Order.Last)
                return;

            Order.Remove(node);
            Order.AddLast(node);
            return;
        }

        Nodes[key] = Order.AddLast(key);
    }
}
