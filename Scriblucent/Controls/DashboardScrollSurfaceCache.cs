using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
namespace Scriblucent.Controls;

/// <summary>
/// On large viewports, rasterizes card surfaces to bitmaps during compositor scroll so
/// per-frame translate repaints stay cheap. Work is spread across frames to avoid start hitches,
/// and caches survive brief pauses between wheel bursts (see SmoothScrollCompositor's surface hold)
/// so cards are not re-rasterized on every burst. Idle <see cref="Prewarm"/> fills the same
/// caches before the first maximized fling so warm-up and glide are not paid together.
/// </summary>
internal static class DashboardScrollSurfaceCache
{
    private const int CardsPerFrame = 8;
    private const int ReleasesPerFrame = 8;
    private const double LargeViewportHeightThreshold = 760;
    private const double LargeViewportWidthThreshold = 1160;
    /// <summary>Hard bound on how many surfaces stay rasterized at once - the cache scales with
    /// the realized (viewport) set, never the item count, and can never exceed this many.</summary>
    private const int MaxAppliedSurfaces = 64;
    /// <summary>How long prewarmed/hold caches may persist without any scroll activity before
    /// they are released - memory returns during normal use; a scroll inside the window stays
    /// smooth because the caches are still warm.</summary>
    private const double IdleReleaseDelaySeconds = 10;

    private static readonly Queue<FrameworkElement> Pending = new();
    private static readonly HashSet<FrameworkElement> Queued = new();
    private sealed record CacheEntry(CacheMode? Original, BitmapCache Owned);
    private static readonly Dictionary<FrameworkElement, CacheEntry> Applied = new();
    private static Queue<FrameworkElement>? _releaseQueue;
    private static bool _sessionActive;
    private static bool _prewarmActive;
    private static bool _largeViewport;
    private static bool _flushScheduled;
    private static int _flushGeneration;
    private static DispatcherTimer? _idleReleaseTimer;

    public static int AppliedCount => Applied.Count;
    public static int PendingCount => Queued.Count;
    internal readonly record struct StateSnapshot(int Applied, int Pending, bool ScrollActive, bool WarmupActive, bool IdleReleaseArmed);
    internal static StateSnapshot Snapshot => new(Applied.Count, Queued.Count, _sessionActive, _prewarmActive, _idleReleaseTimer?.IsEnabled == true);

    private static void CancelPending()
    {
        Pending.Clear(); Queued.Clear();
        _flushScheduled = false;
        _flushGeneration++;
    }

    public static void BeginSession(ScrollViewer scrollViewer)
    {
        // Keep any idle-prewarmed Applied caches; only abandon pending release and pending queue.
        CancelPending();
        _releaseQueue = null;
        _prewarmActive = false;
        DisarmIdleRelease();

        _largeViewport = IsLargeViewport(scrollViewer);
        _sessionActive = _largeViewport;

        if (!_sessionActive)
            EndSession();
    }

    /// <summary>Reactivates a session whose caches were kept alive through a surface hold.</summary>
    public static void ResumeSession(ScrollViewer scrollViewer)
    {
        _releaseQueue = null;
        _prewarmActive = false;
        DisarmIdleRelease();
        _largeViewport = IsLargeViewport(scrollViewer);
        _sessionActive = _largeViewport;

        if (!_sessionActive)
            EndSession();
    }

    public static void TryQueue(FrameworkElement surface)
    {
        if (!_sessionActive || !_largeViewport)
            return;

        if (FindScrollViewer(surface) is not { } scrollViewer || !SmoothScrollCompositor.IsActive(scrollViewer))
            return;

        EnqueueSurface(surface);
    }

    /// <summary>
    /// Idle path: queue realized card surfaces for bitmap cache without an active compositor
    /// session. Only runs on large viewports; mid-prewarm scroll sessions reclaim Applied as usual.
    /// </summary>
    public static void Prewarm(ScrollViewer scrollViewer, IEnumerable<FrameworkElement> surfaces)
    {
        if (!IsLargeViewport(scrollViewer))
            return;

        // Reclaim any staggered release so prewarm does not fight EndSessionStaggered.
        _releaseQueue = null;
        _largeViewport = true;
        _prewarmActive = true;
        DisarmIdleRelease();

        foreach (var surface in surfaces)
            EnqueueSurface(surface);

        // A resize often warms cards that are all ready already. No flush is queued
        // then, so this path must finish itself and re-arm their idle expiration.
        FinishPrewarmIfReady();
    }

    public static void EndSession()
    {
        CancelPending();
        _releaseQueue = null;
        _sessionActive = false;
        _prewarmActive = false;
        _largeViewport = false;
        DisarmIdleRelease();

        foreach (var surface in Applied.Keys.ToArray())
            ReleaseSurface(surface);
    }

    /// <summary>
    /// Restores cache modes a few surfaces per dispatcher pass so ending a scroll burst
    /// does not re-rasterize the whole dashboard in a single frame.
    /// </summary>
    public static void EndSessionStaggered()
    {
        CancelPending();
        _sessionActive = false;
        _prewarmActive = false;
        _largeViewport = false;
        DisarmIdleRelease();

        if (Applied.Count == 0)
        {
            _releaseQueue = null;
            return;
        }

        _releaseQueue = new Queue<FrameworkElement>(Applied.Keys);
        ScheduleRelease();
    }

    private static void EnqueueSurface(FrameworkElement surface)
    {
        if (surface.CacheMode is BitmapCache || Applied.ContainsKey(surface) || Queued.Contains(surface))
            return;

        // The cache is bounded: once the realized-set budget is reached, further surfaces are
        // simply left uncached (they repaint per frame) instead of growing the raster memory.
        if (Applied.Count + Queued.Count >= MaxAppliedSurfaces)
            return;

        Queued.Add(surface);
        Pending.Enqueue(surface);
        ScheduleFlush();
    }

    private static void ScheduleRelease()
    {
        if (_releaseQueue is not { Count: > 0 } queue)
        {
            _releaseQueue = null;
            return;
        }

        queue.Peek().Dispatcher.BeginInvoke(ReleaseBatch, DispatcherPriority.Background);
    }

    private static void ReleaseBatch()
    {
        // A resumed session or prewarm reclaims the caches; abandon the pending release.
        if (_sessionActive || _prewarmActive || _releaseQueue is not { } queue)
            return;

        var released = 0;
        while (released < ReleasesPerFrame && queue.Count > 0)
        {
            var surface = queue.Dequeue();
            if (!ReleaseSurface(surface))
                continue;

            released++;
        }

        if (released > 0 && Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"cacheRelease\t{released}");

        if (queue.Count > 0)
            ScheduleRelease();
        else
            _releaseQueue = null;
    }

    private static void ScheduleFlush()
    {
        if (_flushScheduled || Pending.Count == 0)
            return;

        _flushScheduled = true;
        var dispatcher = Pending.Peek().Dispatcher;
        var generation = _flushGeneration;
        dispatcher.BeginInvoke(() =>
        {
            if (generation == _flushGeneration) FlushBatch();
        }, DispatcherPriority.Background);
    }

    private static void FlushBatch()
    {
        _flushScheduled = false;
        if (!_sessionActive && !_prewarmActive)
        {
            CancelPending();
            return;
        }

        var applied = 0;
        while (applied < CardsPerFrame && Pending.Count > 0)
        {
            var surface = Pending.Dequeue();
            Queued.Remove(surface);
            if (!surface.IsVisible || Applied.ContainsKey(surface) || surface.CacheMode is BitmapCache
                || Applied.Count >= MaxAppliedSurfaces)
                continue;

            var cache = DpiAwareBitmapCache.Create(surface, enableClearType: false);
            Applied[surface] = new(surface.CacheMode, cache);
            surface.Unloaded += SurfaceUnloaded;
            surface.IsVisibleChanged += SurfaceVisibilityChanged;
            surface.SetCurrentValue(UIElement.CacheModeProperty, cache);
            applied++;
        }

        if (applied > 0 && Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"cacheApply\t{applied}");

        if (Pending.Count > 0)
            ScheduleFlush();
        else FinishPrewarmIfReady();
    }

    private static void FinishPrewarmIfReady()
    {
        if (_prewarmActive && !_sessionActive && Queued.Count == 0)
        {
            _prewarmActive = false;
            // The prewarmed caches stay for the interaction window, then release on idle so the
            // memory returns during normal use (the next fling re-warms on demand).
            ArmIdleRelease();
        }
    }

    private static bool ReleaseSurface(FrameworkElement surface)
    {
        if (!Applied.Remove(surface, out var entry)) return false;
        surface.Unloaded -= SurfaceUnloaded;
        surface.IsVisibleChanged -= SurfaceVisibilityChanged;
        // A different motion owner may have replaced the cache in the meantime.
        if (ReferenceEquals(surface.CacheMode, entry.Owned))
            surface.SetCurrentValue(UIElement.CacheModeProperty, entry.Original);
        if (Applied.Count == 0 && Queued.Count == 0) DisarmIdleRelease();
        return true;
    }
    private static void SurfaceUnloaded(object sender, RoutedEventArgs e) => ReleaseSurface((FrameworkElement)sender);
    private static void SurfaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) ReleaseSurface((FrameworkElement)sender);
    }

    /// <summary>Arms a one-shot idle timer: with no scroll activity for <see cref="IdleReleaseDelaySeconds"/>,
    /// held (prewarmed/hold) caches are released a few surfaces per frame at Background priority.</summary>
    private static void ArmIdleRelease()
    {
        if (Applied.Count == 0 || _sessionActive)
            return;

        _idleReleaseTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(IdleReleaseDelaySeconds)
        };
        _idleReleaseTimer.Stop();
        _idleReleaseTimer.Tick -= OnIdleRelease;
        _idleReleaseTimer.Tick += OnIdleRelease;
        _idleReleaseTimer.Start();
    }

    private static void DisarmIdleRelease()
    {
        _idleReleaseTimer?.Stop();
    }

    private static void OnIdleRelease(object? sender, EventArgs e)
    {
        if (!_sessionActive && !_prewarmActive && Applied.Count > 0)
            EndSessionStaggered();
    }

    private static bool IsLargeViewport(ScrollViewer scrollViewer)
        => scrollViewer.ViewportHeight >= LargeViewportHeightThreshold
            || scrollViewer.ViewportWidth >= LargeViewportWidthThreshold;

    private static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ScrollViewer scrollViewer)
                return scrollViewer;

            element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
        }

        return null;
    }
}
