using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>
/// Keeps scroll motion visually smooth via <see cref="TranslateTransform"/> while throttling
/// <see cref="ScrollViewer.ScrollToVerticalOffset"/> so dashboard virtualization remeasures less often.
/// </summary>
internal static class SmoothScrollCompositor
{
    private const double MinViewportForCompositor = 640;
    private const double MinCommitIntervalMs = 56;
    private const double MaxCommitIntervalMs = 120;
    private const double MinCommitThresholdPixels = 96;
    private const double MaxCommitThresholdPixels = 200;
    private const double MinCommitDriftPixels = 12;

    /// <summary>
    /// How long stripped shadows and card bitmap caches outlive the scroll animation.
    /// Reading pauses between wheel bursts routinely exceed a second; tearing the surface
    /// state down inside that window re-rasterizes every card on the next burst, which is
    /// pure render-thread cost with no UI-thread signature.
    /// </summary>
    private const double SurfaceHoldReleaseDelayMs = 2600;

    private static ScrollViewer? _surfaceHoldScrollViewer;
    private static DispatcherTimer? _surfaceHoldTimer;

    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "SmoothScrollCompositorSession",
        typeof(Session),
        typeof(SmoothScrollCompositor),
        new PropertyMetadata(null));

    private sealed class Session
    {
        public bool Active;
        public double CommittedX;
        public double CommittedY;
        public double VisualX;
        public double VisualY;
        public DateTime LastCommitUtc = DateTime.MinValue;
        public FrameworkElement? ContentElement;
        public TranslateTransform? Translate;
        public double BaselineTranslateX;
        public double BaselineTranslateY;
        public Transform? OriginalTransform;
        public Point OriginalTransformOrigin;
        public bool AddedSmoothTranslateChild;
        public bool WrappedExistingTransform;
        public bool HitTestSuppressed;
        public bool OriginalContentHitTestVisible = true;
        public double DpiScale = 1.0;
        public IReadOnlyList<DashboardVirtualizationHost>? Hosts;
    }

    public static void Begin(ScrollViewer scrollViewer, IReadOnlyList<DashboardVirtualizationHost> hosts)
    {
        if (!ShouldUseCompositor(scrollViewer, hosts))
            return;

        var session = GetOrCreateSession(scrollViewer);
        if (session.Active)
            return;

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log("sessionBegin");

        session.Active = true;
        session.CommittedX = scrollViewer.HorizontalOffset;
        session.CommittedY = scrollViewer.VerticalOffset;
        session.VisualX = session.CommittedX;
        session.VisualY = session.CommittedY;
        session.LastCommitUtc = DateTime.UtcNow;
        session.ContentElement = ResolveContentElement(scrollViewer);
        session.Translate = null;
        session.DpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(scrollViewer).PixelsPerDip;
        session.Hosts = hosts;

        if (session.ContentElement is { } content)
        {
            session.OriginalTransform = content.RenderTransform;
            session.OriginalTransformOrigin = content.RenderTransformOrigin;
            session.Translate = AcquireSmoothTranslate(content, session);

            // While content glides under the cursor, WPF re-hit-tests the moved tree after
            // every frame and fires MouseEnter/Leave storms across the cards. Suspend
            // content hit-testing for the duration of the motion; the ScrollViewer chrome
            // (template root, scrollbars) stays interactive so wheel input keeps flowing.
            session.OriginalContentHitTestVisible = content.IsHitTestVisible;
            session.HitTestSuppressed = true;
            content.IsHitTestVisible = false;
            DashboardScrollRenderGate.ResetHoveredCards(content);
        }

        var reclaimedHold = TryReclaimSurfaceHold(scrollViewer);
        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"reclaimed\t{reclaimedHold}");
        if (reclaimedHold)
            DashboardScrollSurfaceCache.ResumeSession(scrollViewer);
        else
            DashboardScrollSurfaceCache.BeginSession(scrollViewer);

        DashboardShadowSuspender.BeginScope(DashboardShadowSuspender.AnimatedScrollShadowScope);

        // Live DropShadowEffect leftovers (chrome/popups) are suspended for the scroll
        // session. Dashboard section shells and listing tiles use bake-once CardShadowLayer
        // and do not need this walk for blur cost — the suspender still strips any stray
        // live Effects in the scrolled content.
        if (!reclaimedHold && session.ContentElement is { } shadowRoot)
            DashboardShadowSuspender.TrySuspend(DashboardShadowSuspender.AnimatedScrollShadowScope, shadowRoot);

        DashboardScrollRenderGate.StripRealizedCardShadows(hosts);
        DashboardScrollRenderGate.SetRealizedCardShadowVisibility(hosts, visible: false);
        DashboardScrollRenderGate.SetRealizedCardBitmapMode(hosts, bitmapMode: true);
    }

    public static void End(ScrollViewer scrollViewer, double finalX, double finalY)
    {
        if (scrollViewer.GetValue(SessionProperty) is not Session session || !session.Active)
            return;

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log("sessionEnd");

        Commit(scrollViewer, session, finalX, finalY);

        // Trust Commit's clamped target — re-reading VerticalOffset here can still be
        // behind the visual position when commits were throttled during the glide.
        if (session.Translate is { } translate)
        {
            translate.X = session.BaselineTranslateX;
            translate.Y = session.BaselineTranslateY;
        }

        session.Active = false;

        var contentRoot = session.ContentElement;
        var restoreHitTest = session.HitTestSuppressed;
        var hitTestVisible = session.OriginalContentHitTestVisible;
        var teardown = session;

        // Unwrap the translate wrapper only after layout applies the final commit so
        // removing drift compensation does not flash a sub-pixel gap for one frame.
        scrollViewer.Dispatcher.BeginInvoke(() =>
        {
            scrollViewer.UpdateLayout();

            if (restoreHitTest && contentRoot is { } content)
                content.IsHitTestVisible = hitTestVisible;

            teardown.HitTestSuppressed = false;
            RestoreContentTransform(teardown);
            teardown.Translate = null;
            teardown.ContentElement = null;
            var endHosts = teardown.Hosts;
            teardown.Hosts = null;

            // Cards STAY in tile mode after the session ends: flipping back to the live template
            // per burst re-flowed the grid (live card heights vary; tiles are fixed-cell) - the
            // visible readjusting during scroll. The flip happens once, at the true settle (the
            // surface hold release).
            BeginSurfaceHold(scrollViewer, endHosts);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Cancels a pending surface release. Returns true when the hold belonged to the same
    /// viewer, so its stripped shadows and card caches can be reused as-is.
    /// </summary>
    private static bool TryReclaimSurfaceHold(ScrollViewer scrollViewer)
    {
        var holder = _surfaceHoldScrollViewer;
        if (holder is null)
            return false;

        _surfaceHoldTimer?.Stop();
        _surfaceHoldScrollViewer = null;

        if (ReferenceEquals(holder, scrollViewer))
            return true;

        DashboardScrollSurfaceCache.EndSession();
        DashboardShadowSuspender.TryResume(DashboardShadowSuspender.AnimatedScrollShadowScope);
        return false;
    }

    private static IReadOnlyList<DashboardVirtualizationHost>? _surfaceHoldHosts;

    private static void BeginSurfaceHold(ScrollViewer scrollViewer, IReadOnlyList<DashboardVirtualizationHost>? hosts)
    {
        _surfaceHoldScrollViewer = scrollViewer;
        _surfaceHoldHosts = hosts;

        if (_surfaceHoldTimer is null)
        {
            _surfaceHoldTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(SurfaceHoldReleaseDelayMs)
            };
            _surfaceHoldTimer.Tick += (_, _) => ReleaseSurfaceHold();
        }

        _surfaceHoldTimer.Stop();
        _surfaceHoldTimer.Start();
    }

    private static void ReleaseSurfaceHold()
    {
        _surfaceHoldTimer?.Stop();
        if (_surfaceHoldScrollViewer is null)
            return;

        _surfaceHoldScrollViewer = null;
        if (_surfaceHoldHosts is { } holdHosts)
        {
            // The true settle: cards flip back to the live template (hover/menu/drag/mass-select)
            // and the shadows return - once, not per scroll burst.
            DashboardScrollRenderGate.SetRealizedCardBitmapMode(holdHosts, bitmapMode: false);
            DashboardScrollRenderGate.SetRealizedCardShadowVisibility(holdHosts, visible: true);
            _surfaceHoldHosts = null;
        }

        DashboardScrollSurfaceCache.EndSessionStaggered();
        DashboardShadowSuspender.TryResumeStaggered(DashboardShadowSuspender.AnimatedScrollShadowScope);
    }

    public static bool TryApply(
        ScrollViewer scrollViewer,
        double visualX,
        double visualY,
        bool forceCommit,
        out double committedX,
        out double committedY)
    {
        committedX = visualX;
        committedY = visualY;

        if (scrollViewer.GetValue(SessionProperty) is not Session session || !session.Active)
            return false;

        session.VisualX = visualX;
        session.VisualY = visualY;
        UpdateTransform(session);

        if (forceCommit || ShouldCommit(session, scrollViewer))
            Commit(scrollViewer, session, visualX, visualY);

        committedX = session.CommittedX;
        committedY = session.CommittedY;
        return true;
    }

    public static bool TryGetVerticalDrift(ScrollViewer scrollViewer, out double drift)
    {
        drift = 0;
        if (scrollViewer.GetValue(SessionProperty) is not Session { Active: true } session)
            return false;

        drift = session.VisualY - session.CommittedY;
        return Math.Abs(drift) > 0.01;
    }

    public static bool TryGetHorizontalDrift(ScrollViewer scrollViewer, out double drift)
    {
        drift = 0;
        if (scrollViewer.GetValue(SessionProperty) is not Session { Active: true } session)
            return false;

        drift = session.VisualX - session.CommittedX;
        return Math.Abs(drift) > 0.01;
    }

    public static bool IsActive(ScrollViewer scrollViewer)
        => scrollViewer.GetValue(SessionProperty) is Session { Active: true };

    private static bool ShouldUseCompositor(ScrollViewer scrollViewer, IReadOnlyList<DashboardVirtualizationHost> hosts)
    {
        if (ReducedMotionPreferences.ShouldReduceMotion)
            return false;

        if (scrollViewer.ViewportHeight >= MinViewportForCompositor)
            return true;

        return hosts.Count > 0;
    }

    private static bool ShouldCommit(Session session, ScrollViewer scrollViewer)
    {
        var driftY = Math.Abs(session.VisualY - session.CommittedY);
        var driftX = Math.Abs(session.VisualX - session.CommittedX);
        var threshold = ResolveCommitThresholdPixels(scrollViewer);

        if (driftY >= threshold || driftX >= threshold)
            return true;

        // Interval commits keep the scrollbar tracking, but skip them while drift is
        // negligible (easing tail) — the final commit is forced when the impulse ends.
        if (driftY < MinCommitDriftPixels && driftX < MinCommitDriftPixels)
            return false;

        var elapsedMs = (DateTime.UtcNow - session.LastCommitUtc).TotalMilliseconds;
        return elapsedMs >= ResolveCommitIntervalMs(scrollViewer);
    }

    private static double ResolveCommitIntervalMs(ScrollViewer scrollViewer)
    {
        var viewport = Math.Max(scrollViewer.ViewportHeight, scrollViewer.ViewportWidth);
        if (viewport <= 0)
            viewport = 720;

        return Math.Clamp(40 + viewport * 0.045, MinCommitIntervalMs, MaxCommitIntervalMs);
    }

    private static double ResolveCommitThresholdPixels(ScrollViewer scrollViewer)
    {
        var viewport = Math.Max(scrollViewer.ViewportHeight, scrollViewer.ViewportWidth);
        if (viewport <= 0)
            viewport = 720;

        return Math.Clamp(viewport * 0.10, MinCommitThresholdPixels, MaxCommitThresholdPixels);
    }

    private static void Commit(ScrollViewer scrollViewer, Session session, double visualX, double visualY)
    {
        // Snap the committed offset to whole device pixels: the ScrollViewer content is what the
        // glyphs anchor to once the drift translate clears; committing to fractional offsets
        // re-rasterizes the glyph cache at each commit.
        var scale = session.DpiScale > 0 ? session.DpiScale : 1.0;
        var clampedX = scrollViewer.ScrollableWidth > 0
            ? Math.Clamp(visualX, 0, scrollViewer.ScrollableWidth)
            : 0;
        var clampedY = scrollViewer.ScrollableHeight > 0
            ? Math.Clamp(visualY, 0, scrollViewer.ScrollableHeight)
            : 0;
        clampedX = Snap(clampedX, scale);
        clampedY = Snap(clampedY, scale);

        session.CommittedX = clampedX;
        session.CommittedY = clampedY;
        session.VisualX = clampedX;
        session.VisualY = clampedY;
        session.LastCommitUtc = DateTime.UtcNow;
        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            Scriblucent.Services.ScrollPerfDiag.Log($"commit\ty={clampedY:F0}");

        if (scrollViewer.ScrollableWidth > 0)
            scrollViewer.ScrollToHorizontalOffset(clampedX);
        if (scrollViewer.ScrollableHeight > 0)
            scrollViewer.ScrollToVerticalOffset(clampedY);

        UpdateTransform(session);
    }

    private static void UpdateTransform(Session session)
    {
        if (session.Translate is not { } translate)
            return;

        // Snap the translate to whole device pixels: under a fractional translate, Display-mode
        // text whose glyph origins snap to whole pixels re-rasterizes every frame - a major
        // scroll frame-spike source.
        var scale = session.DpiScale > 0 ? session.DpiScale : 1.0;
        translate.X = Snap(session.BaselineTranslateX - (session.VisualX - session.CommittedX), scale);
        translate.Y = Snap(session.BaselineTranslateY - (session.VisualY - session.CommittedY), scale);
    }

    private static double Snap(double value, double scale)
        => Math.Round(value * scale) / scale;

    private static TranslateTransform AcquireSmoothTranslate(FrameworkElement content, Session session)
    {
        if (content.RenderTransform is TranslateTransform translate && !translate.IsFrozen)
        {
            session.BaselineTranslateX = translate.X;
            session.BaselineTranslateY = translate.Y;
            return translate;
        }

        if (content.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            var existing = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existing is not null && !existing.IsFrozen)
            {
                session.BaselineTranslateX = existing.X;
                session.BaselineTranslateY = existing.Y;
                return existing;
            }

            translate = new TranslateTransform();
            group.Children.Add(translate);
            session.AddedSmoothTranslateChild = true;
            return translate;
        }

        if (content.RenderTransform is { IsFrozen: false } existingTransform)
        {
            var wrapper = new TransformGroup();
            wrapper.Children.Add(existingTransform);
            translate = new TranslateTransform();
            wrapper.Children.Add(translate);
            content.RenderTransform = wrapper;
            session.WrappedExistingTransform = true;
            return translate;
        }

        session.BaselineTranslateX = 0;
        session.BaselineTranslateY = 0;
        translate = new TranslateTransform();
        content.RenderTransform = translate;
        return translate;
    }

    private static void RestoreContentTransform(Session session)
    {
        if (session.ContentElement is not { } content)
            return;

        if (session.Translate is { } translate)
        {
            translate.X = session.BaselineTranslateX;
            translate.Y = session.BaselineTranslateY;

            if (session.AddedSmoothTranslateChild && session.OriginalTransform is TransformGroup group)
                group.Children.Remove(translate);
        }

        if (session.WrappedExistingTransform)
            content.RenderTransform = session.OriginalTransform;

        content.RenderTransformOrigin = session.OriginalTransformOrigin;
    }

    private static FrameworkElement? ResolveContentElement(ScrollViewer scrollViewer)
        => scrollViewer.Content as FrameworkElement;

    private static Session GetOrCreateSession(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(SessionProperty) is Session session)
            return session;

        session = new Session();
        scrollViewer.SetValue(SessionProperty, session);
        return session;
    }
}