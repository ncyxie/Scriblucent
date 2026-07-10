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
/// so cards are not re-rasterized on every burst.
/// </summary>
internal static class DashboardScrollSurfaceCache
{
    private const int CardsPerFrame = 8;
    private const int ReleasesPerFrame = 8;
    private const double LargeViewportHeightThreshold = 760;
    private const double LargeViewportWidthThreshold = 1160;

    private static readonly Queue<FrameworkElement> Pending = new();
    private static readonly Dictionary<FrameworkElement, CacheMode?> Applied = new();
    private static Queue<FrameworkElement>? _releaseQueue;
    private static bool _sessionActive;
    private static bool _largeViewport;
    private static bool _flushScheduled;

    public static int AppliedCount => Applied.Count;
    public static int PendingCount => Pending.Count;

    public static void BeginSession(ScrollViewer scrollViewer)
    {
        EndSession();

        _largeViewport = IsLargeViewport(scrollViewer);
        _sessionActive = _largeViewport;
    }

    /// <summary>Reactivates a session whose caches were kept alive through a surface hold.</summary>
    public static void ResumeSession(ScrollViewer scrollViewer)
    {
        _releaseQueue = null;
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

        if (surface.CacheMode is BitmapCache || Applied.ContainsKey(surface))
            return;

        Pending.Enqueue(surface);
        ScheduleFlush();
    }

    public static void EndSession()
    {
        Pending.Clear();
        _releaseQueue = null;
        _flushScheduled = false;
        _sessionActive = false;
        _largeViewport = false;

        foreach (var (surface, cacheMode) in Applied.ToList())
            surface.CacheMode = cacheMode;

        Applied.Clear();
    }

    /// <summary>
    /// Restores cache modes a few surfaces per dispatcher pass so ending a scroll burst
    /// does not re-rasterize the whole dashboard in a single frame.
    /// </summary>
    public static void EndSessionStaggered()
    {
        Pending.Clear();
        _flushScheduled = false;
        _sessionActive = false;
        _largeViewport = false;

        if (Applied.Count == 0)
        {
            _releaseQueue = null;
            return;
        }

        _releaseQueue = new Queue<FrameworkElement>(Applied.Keys);
        ScheduleRelease();
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
        // A resumed session reclaims the caches; abandon the pending release.
        if (_sessionActive || _releaseQueue is not { } queue)
            return;

        var released = 0;
        while (released < ReleasesPerFrame && queue.Count > 0)
        {
            var surface = queue.Dequeue();
            if (!Applied.TryGetValue(surface, out var cacheMode))
                continue;

            Applied.Remove(surface);
            surface.CacheMode = cacheMode;
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
        dispatcher.BeginInvoke(FlushBatch, DispatcherPriority.Background);
    }

    private static void FlushBatch()
    {
        _flushScheduled = false;
        if (!_sessionActive)
        {
            Pending.Clear();
            return;
        }

        var applied = 0;
        while (applied < CardsPerFrame && Pending.Count > 0)
        {
            var surface = Pending.Dequeue();
            if (!surface.IsVisible || Applied.ContainsKey(surface))
                continue;

            Applied[surface] = surface.CacheMode;
            surface.CacheMode = DpiAwareBitmapCache.Create(surface, enableClearType: false);
            applied++;
        }

        if (applied > 0 && Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"cacheApply\t{applied}");

        if (Pending.Count > 0)
            ScheduleFlush();
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
