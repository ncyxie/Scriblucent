using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Services;

namespace Scriblucent.Controls;

public enum DashboardVirtualizationHostMode
{
    /// <summary>Page scroll drives viewport slicing — used for large ungrouped listings.</summary>
    PageVirtualized,

    /// <summary>Full static layout — cards move only with their parent group block.</summary>
    Static
}

/// <summary>
/// Reserves the full virtualized content height for the page <see cref="ScrollViewer"/>
/// while measuring the child grid only for the currently visible viewport slice.
/// Uses <see cref="Decorator"/> so the grid is a direct visual child (ContentControl breaks rendering).
/// </summary>
public sealed class DashboardVirtualizationHost : Decorator
{
    public static readonly DependencyProperty ScrollViewerElementProperty = DependencyProperty.Register(
        nameof(ScrollViewerElement),
        typeof(ScrollViewer),
        typeof(DashboardVirtualizationHost),
        new PropertyMetadata(null, OnScrollViewerElementChanged));

    public static readonly DependencyProperty HostModeProperty = DependencyProperty.Register(
        nameof(HostMode),
        typeof(DashboardVirtualizationHostMode),
        typeof(DashboardVirtualizationHost),
        new PropertyMetadata(DashboardVirtualizationHostMode.PageVirtualized, OnHostModeChanged));

    public static readonly DependencyProperty MotionScopeProperty = DependencyProperty.Register(
        nameof(MotionScope),
        typeof(DashboardMotionScope),
        typeof(DashboardVirtualizationHost),
        new PropertyMetadata(default(DashboardMotionScope)));

    private ScrollViewer? _pageScrollViewer;
    private double _sectionTop;
    private double _extentHeight;
    private double _sliceOffset;
    private double _sliceHeight;
    private int _lastItemCount = -1;
    private bool _viewportSyncScheduled;
    private bool _mutationReapplyDeferred;
    private bool _motionViewportFrozen;
    private long _motionViewportFrozenSinceMs;
    private double _frozenSliceOffset;
    private double _frozenSliceHeight;
    private bool _isInternalLayoutPass;
    private double _lastSyncedSliceOffset = double.NaN;
    private double _lastSyncedSliceHeight = double.NaN;
    private double _lastMeasureWidth = double.NaN;
    private bool _sectionTopIsCurrent;
    private double _extentSnapshotBeforeMutation;
    private double _pendingExtentScrollCompensation;
    private int _smoothScrollDepth;
    private DateTime _lastSmoothScrollSyncUtc = DateTime.MinValue;

    // Signed EMA of visual scroll speed (positive = scrolling down), sourced from
    // SmoothScrollBehavior.Tick's own frame-to-frame position delta - see the comment on
    // RaiseForVelocity for why this exists and why it's read from there rather than sampled
    // from this host's own view of the ScrollViewer.
    private double _smoothScrollVelocityPxPerMs;

    private const double ViewportCachePixels = 320;
    private const double ViewportCacheAbovePixels = 240;
    private const double TargetExtraCardsAhead = 10;
    private const double TargetExtraCardsBehind = 7;
    private const double MinSmoothScrollSyncIntervalMs = 22;
    private const double MaxSmoothScrollSyncIntervalMs = 68;
    private const double SmoothScrollSliceEquivalencePixels = 88;
    private const double SmoothScrollHostMeasureStridePixels = 128;
    private const double BootstrapViewportHeight = 720;
    private const double ExtentBottomPadding = 12;
    private const double SliceEquivalencePixels = 1;
    private const double UniformSliceEquivalencePixels = 32;
    private const double UniformHostMeasureStridePixels = 48;
    private const double MinViableViewportHeight = 64;

    // Rise fast (~3 frames to reach ~90% of a step change) so the enlarged cache margin is in
    // place well before a wheel impulse settles; decay slow (~5 frames to halve) so one
    // jittery/skipped frame mid-fling can't collapse the margin right when it's still needed.
    private const double VelocityRiseAlpha = 0.55;
    private const double VelocityDecayAlpha = 0.12;

    // Covers DispatcherPriority.Background scheduling latency, the measure pass itself, and
    // SmoothScrollSliceEquivalencePixels (a sync that fires but moves less than 88px is
    // discarded) - see the comment on RaiseForVelocity for the full mechanism this closes.
    private const double VelocityLeadSafetyFactor = 1.6;

    // The gap only opens at the leading edge of scroll direction; the trailing edge only needs
    // enough margin to survive an immediate direction reversal, not the full lead.
    private const double TrailingVelocityLeadShare = 0.5;

    // Hard ceiling on how far velocity can raise the cache margin, relative to the existing
    // ViewportCachePixels/ViewportCacheAbovePixels fallback. Past this, a truly extreme fling
    // degrades to a smaller residual gap rather than removing the compositing-cost bound this
    // margin exists to enforce - an explicit, bounded trade-off.
    private const double MaxVelocityCacheMultiplier = 3.0;

    // Defense in depth: DashboardMotionEngine is expected to always call UnfreezeMotionViewport
    // once its reflow completes. If a card's enter animation gets cancelled without notice while
    // it's in flight (e.g. its container is recycled by an overlapping mutation - see
    // DashboardMotionEngine's _pendingEnterCompletions comment for the full mechanism), that
    // unfreeze can be missed, and this host would otherwise stop responding to scroll forever -
    // the "scroll a tiny bit and every card vanishes" bug. Auto-clearing the freeze after a
    // generous timeout means that failure mode is bounded here too, independently of whether the
    // engine-side fix covers every case.
    private const long MaxMotionViewportFreezeMs = 3000;

    public ScrollViewer? ScrollViewerElement
    {
        get => (ScrollViewer?)GetValue(ScrollViewerElementProperty);
        set => SetValue(ScrollViewerElementProperty, value);
    }

    public DashboardVirtualizationHostMode HostMode
    {
        get => (DashboardVirtualizationHostMode)GetValue(HostModeProperty);
        set => SetValue(HostModeProperty, value);
    }

    public DashboardMotionScope MotionScope
    {
        get => (DashboardMotionScope)GetValue(MotionScopeProperty);
        set => SetValue(MotionScopeProperty, value);
    }

    private bool UsesPageVirtualization => HostMode == DashboardVirtualizationHostMode.PageVirtualized;

    private DashboardVirtualizedCardGrid? Grid => Child as DashboardVirtualizedCardGrid;

    public DashboardVirtualizationHost()
    {
        ClipToBounds = false;
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnScrollViewerElementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardVirtualizationHost host)
            host.AttachScrollViewer((ScrollViewer?)e.NewValue);
    }

    private static void OnHostModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DashboardVirtualizationHost host)
            return;

        if (host.ScrollViewerElement is { } scrollViewer)
            host.AttachScrollViewer(scrollViewer);

        host.InvalidateSectionGeometry();
    }

    public void RequestViewportRefresh()
    {
        ScrollPerfDiag.Log($"requestViewportRefresh\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}\tdeferred={_mutationReapplyDeferred}");

        // This host's own position on the page may have shifted even though its item count
        // didn't change - e.g. a sibling section above it just grew from a duplicate/add.
        // Only the mutated host's own measure pass clears its cached _sectionTop (gated on
        // itemCountChanged in MeasureHostContent/MeasureStaticHostContent); every other host
        // in the scope only gets here, via the post-reflow RefreshVirtualizationViewportForScope
        // sweep. Without clearing it too, TryUpdateSectionTop() keeps returning the stale
        // pre-mutation position (_sectionTopIsCurrent stays true), so this host's visible-slice
        // math stays offset from where WPF actually arranged it - a boundary gap at whichever
        // edge the offset lands on. Confirmed via measureSliceStatic showing a downstream
        // section's sectionTop never updating across a mutation that visibly grew the page above it.
        _sectionTop = double.NaN;
        _sectionTopIsCurrent = false;
        _lastSyncedSliceOffset = double.NaN;
        _lastSyncedSliceHeight = double.NaN;

        if (_mutationReapplyDeferred)
        {
            ApplyMutationRefresh();
            return;
        }

        InvalidateMeasure();
    }

    public void RequestMutationRefresh()
    {
        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(MotionScope))
        {
            _mutationReapplyDeferred = true;
            return;
        }

        ApplyMutationRefresh();
    }

    public void FlushDeferredMutationRefresh()
    {
        if (!_mutationReapplyDeferred)
            return;

        ApplyMutationRefresh();
    }

    public void FreezeMotionViewport()
    {
        _motionViewportFrozen = true;
        _motionViewportFrozenSinceMs = Environment.TickCount64;
        _frozenSliceOffset = _sliceOffset;
        _frozenSliceHeight = Math.Max(_sliceHeight, 1);
    }

    /// <summary>
    /// True if still frozen after clearing it if the freeze has outlived
    /// <see cref="MaxMotionViewportFreezeMs"/> - see the comment on that constant.
    /// </summary>
    private bool IsMotionViewportStillFrozen()
    {
        if (!_motionViewportFrozen)
            return false;

        if (Environment.TickCount64 - _motionViewportFrozenSinceMs < MaxMotionViewportFreezeMs)
            return true;

        UnfreezeMotionViewport();
        return false;
    }

    public void UnfreezeMotionViewport()
    {
        if (!_motionViewportFrozen)
            return;

        _sliceOffset = _frozenSliceOffset;
        _sliceHeight = _frozenSliceHeight;
        _lastSyncedSliceOffset = _frozenSliceOffset;
        _lastSyncedSliceHeight = _frozenSliceHeight;
        _motionViewportFrozen = false;
        _frozenSliceOffset = 0;
        _frozenSliceHeight = 0;
        InvalidateExtentForMutation(clearGridLayoutCache: true);
    }

    internal void NotifySmoothScrollStarted()
    {
        _smoothScrollDepth++;
        if (_smoothScrollDepth == 1)
            _smoothScrollVelocityPxPerMs = 0;
    }

    internal void NotifySmoothScrollEnded()
    {
        if (_smoothScrollDepth <= 0)
            return;

        _smoothScrollDepth--;
        if (_smoothScrollDepth != 0)
            return;

        _smoothScrollVelocityPxPerMs = 0;
        _lastSmoothScrollSyncUtc = DateTime.MinValue;
        ForceViewportSync();
        Grid?.FlushDeferredInteractiveSweep();
    }

    internal void NotifyVisualScrollOffsetChanged(double velocityPxPerMs = 0)
    {
        if (!IsSmoothScrollActive || IsMotionViewportStillFrozen() || !IsScrollViewportViable())
            return;

        UpdateSmoothScrollVelocity(velocityPxPerMs);
        ScheduleViewportSync();
    }

    private void UpdateSmoothScrollVelocity(double instantaneousPxPerMs)
    {
        var alpha = Math.Abs(instantaneousPxPerMs) > Math.Abs(_smoothScrollVelocityPxPerMs)
            ? VelocityRiseAlpha
            : VelocityDecayAlpha;
        _smoothScrollVelocityPxPerMs += (instantaneousPxPerMs - _smoothScrollVelocityPxPerMs) * alpha;
    }

    private bool IsSmoothScrollActive => _smoothScrollDepth > 0;

    private double ResolveSmoothScrollSyncIntervalMs()
    {
        var viewport = _pageScrollViewer?.ViewportHeight ?? BootstrapViewportHeight;
        return Math.Clamp(18 + viewport * 0.022, MinSmoothScrollSyncIntervalMs, MaxSmoothScrollSyncIntervalMs);
    }

    // Rows realize (and get bitmap-cached) while still off screen, so first-render cost isn't
    // paid on the frame they scroll into view. Margin is sized to a roughly constant CARD count,
    // not pixel/row count: a maximized window packs more columns per row, so a fixed pixel
    // margin would realize proportionally more cards as the window widens. Scaling the margin
    // down as columns increase keeps the compositing cost roughly window-size-invariant.
    private double ResolveViewportCachePixels()
    {
        if (!IsSmoothScrollActive)
            return ViewportCachePixels;

        var budget = ResolveCardBudgetPixels(TargetExtraCardsAhead, ViewportCachePixels);
        return RaiseForVelocity(budget, ViewportCachePixels, isAheadMargin: true);
    }

    private double ResolveViewportCacheAbovePixels()
    {
        if (!IsSmoothScrollActive)
            return ViewportCacheAbovePixels;

        var budget = ResolveCardBudgetPixels(TargetExtraCardsBehind, ViewportCacheAbovePixels);
        return RaiseForVelocity(budget, ViewportCacheAbovePixels, isAheadMargin: false);
    }

    /// <summary>
    /// The visual scroll position advances every animation frame with no speed cap (a fling can
    /// hold several thousand px/s), but the actual re-slice is throttled to
    /// ResolveSmoothScrollSyncIntervalMs() (22-68ms) and even then only applies once the slice
    /// has moved past SmoothScrollSliceEquivalencePixels. At fling speeds within normal
    /// mouse-wheel use, the visual position can outrun the static card-count cache margin
    /// between two throttled syncs - the realized region's leading edge falls behind where
    /// content is actually drawn, a real (if brief) blank strip that heals on the next sync.
    /// Raising the margin to cover the distance travelable in one sync interval at the current
    /// velocity closes that window. Quantized to SmoothScrollHostMeasureStridePixels so the
    /// margin doesn't change on every single frame (which would otherwise flip heightChanged in
    /// ApplyViewportSlice and force a full host remeasure each frame instead of only when the
    /// slice genuinely needs to grow), and capped at MaxVelocityCacheMultiplier so a truly
    /// extreme fling degrades to a smaller residual gap rather than removing the compositing-cost
    /// bound this margin exists to enforce in the first place.
    /// </summary>
    private double RaiseForVelocity(double budgetPixels, double fallbackPixels, bool isAheadMargin)
    {
        var speed = Math.Abs(_smoothScrollVelocityPxPerMs);
        if (speed <= 0)
            return budgetPixels;

        // Positive velocity = scrolling down = the "ahead" (below) margin is the leading edge;
        // negative velocity = scrolling up = the "above" margin is the leading edge instead. The
        // trailing margin only needs enough to survive an immediate direction reversal, not the
        // full lead.
        var isLeadingEdge = isAheadMargin ? _smoothScrollVelocityPxPerMs >= 0 : _smoothScrollVelocityPxPerMs <= 0;
        var share = isLeadingEdge ? 1.0 : TrailingVelocityLeadShare;

        var lead = speed * ResolveSmoothScrollSyncIntervalMs() * VelocityLeadSafetyFactor * share;
        lead = Math.Ceiling(lead / SmoothScrollHostMeasureStridePixels) * SmoothScrollHostMeasureStridePixels;

        var cap = fallbackPixels * MaxVelocityCacheMultiplier;
        return Math.Max(budgetPixels, Math.Min(lead, cap));
    }

    private double ResolveCardBudgetPixels(double targetExtraCards, double fallbackPixels)
    {
        if (Grid is not { } grid)
            return fallbackPixels;

        // DashboardVirtualizedNoteCardGrid never sets ItemCellSize (it drives the wrap panel's
        // ItemSize from its own private constants instead), so without this special case Notes -
        // the dashboard with the narrowest cards and therefore the most columns - silently never
        // gets a column-aware budget. Mirrors the same idiom used in
        // DashboardVirtualizedCardGrid.ResolveEffectiveCellWidth/Height and DashboardMotionEngine.
        double cellWidth, cellHeight;
        if (grid is DashboardVirtualizedNoteCardGrid)
        {
            cellWidth = 244;
            cellHeight = 164;
        }
        else if (grid.ItemCellSize is { Width: > 0, Height: > 0 } cell)
        {
            cellWidth = cell.Width;
            cellHeight = cell.Height;
        }
        else
        {
            return fallbackPixels;
        }

        var width = _lastMeasureWidth > 0 ? _lastMeasureWidth : grid.ActualWidth;
        if (width <= 0)
            return fallbackPixels;

        var columns = Math.Max(1, Math.Floor(width / cellWidth));
        var rows = Math.Max(1, targetExtraCards / columns);
        var budgeted = rows * cellHeight;
        return Math.Clamp(budgeted, fallbackPixels * 0.4, fallbackPixels * 2.0);
    }

    private double ResolveEffectiveVerticalOffset()
    {
        if (_pageScrollViewer is null)
            return 0;

        var offset = _pageScrollViewer.VerticalOffset;
        if (IsSmoothScrollActive
            && SmoothScrollCompositor.TryGetVerticalDrift(_pageScrollViewer, out var drift))
            offset += drift;

        return Math.Max(0, offset);
    }

    private double ResolveEffectiveHorizontalOffset()
    {
        if (_pageScrollViewer is null)
            return 0;

        var offset = _pageScrollViewer.HorizontalOffset;
        if (IsSmoothScrollActive
            && SmoothScrollCompositor.TryGetHorizontalDrift(_pageScrollViewer, out var drift))
            offset += drift;

        return Math.Max(0, offset);
    }

    /// <summary>Updates reserved extent for an add while the motion viewport stays frozen.</summary>
    internal void SyncLayoutForAddMotion()
    {
        ApplyMutationRefresh();
        UpdateLayout();
    }

    internal double ResolveSectionRelativeViewportTop()
    {
        if (_pageScrollViewer is null)
            return 0;

        TryUpdateSectionTop();
        if (double.IsNaN(_sectionTop))
            return Math.Max(0, _pageScrollViewer.VerticalOffset);

        return Math.Max(0, ResolveEffectiveVerticalOffset() - _sectionTop);
    }

    private void InvalidateExtentForMutation(bool clearGridLayoutCache = false)
    {
        if (clearGridLayoutCache)
        {
            Grid?.ResetVirtualizationLayoutCache();
            ScrollPerfDiag.Log($"resetVirtualizationLayoutCache\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}\tscope={MotionScope}");
        }

        BeginExtentRemeasure();
        InvalidateMeasure();
    }

    public void InvalidateSectionGeometry()
    {
        _sectionTopIsCurrent = false;
        _sectionTop = double.NaN;
        _lastSyncedSliceOffset = double.NaN;
        _lastSyncedSliceHeight = double.NaN;
        InvalidateMeasure();
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        RequestViewportRefresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (ShouldPreserveLayoutSnapshot())
            return;

        if (sizeInfo.WidthChanged)
            RequestViewportRefresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A host freshly entering the visual tree has no legitimate reason to already be
        // motion-frozen - any such state is stale, left over from a previous lifecycle (see the
        // comment on MaxMotionViewportFreezeMs). Clearing it here means a host can never come
        // back from being unloaded/reloaded already broken.
        _motionViewportFrozen = false;
        _motionViewportFrozenSinceMs = 0;

        AttachScrollViewer(ScrollViewerElement ?? DashboardMotionViewport.FindScrollViewer(this));

        // A host freshly entering the tree may already hold a _sectionTop latched from a
        // pre-arrange, pre-AttachScrollViewer measure - stale in the same way a sibling's cached
        // position can go stale when a new section is still settling (see the comment on
        // ScheduleSectionGeometryCorrection in MainWindow.DashboardMotion.cs). Invalidate the
        // cached geometry, not just the measure, so this host is guaranteed to re-derive its true
        // position on its first real pass.
        InvalidateSectionGeometry();

        Dispatcher.BeginInvoke(() =>
        {
            InvalidateSectionGeometry();
            Grid?.EnsureRealizedCardsInteractive();
        }, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DashboardShadowSuspender.TryResume(this);
        DetachScrollViewer();
        _motionViewportFrozen = false;
        _motionViewportFrozenSinceMs = 0;
    }

    private void AttachScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(_pageScrollViewer, scrollViewer))
            return;

        DetachScrollViewer();
        _pageScrollViewer = scrollViewer;
        if (_pageScrollViewer is null)
            return;

        _pageScrollViewer.ScrollChanged += OnPageScrollChanged;
        _pageScrollViewer.SizeChanged += OnPageScrollViewerSizeChanged;
    }

    private void DetachScrollViewer()
    {
        if (_pageScrollViewer is null)
            return;

        _pageScrollViewer.ScrollChanged -= OnPageScrollChanged;
        _pageScrollViewer.SizeChanged -= OnPageScrollViewerSizeChanged;
        _pageScrollViewer = null;
    }

    private void OnPageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (IsMotionViewportStillFrozen() || _isInternalLayoutPass || !IsScrollViewportViable())
            return;

        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.ViewportHeightChange) < 0.01)
            return;

        if (CanSkipScrollSyncForOffScreenSection())
            return;

        ScheduleViewportSync();
    }

    private bool CanSkipScrollSyncForOffScreenSection()
    {
        if (_pageScrollViewer is null || _extentHeight <= ExtentBottomPadding)
            return false;

        if (!TryUpdateSectionTop())
            return false;

        return !IsSectionPotentiallyVisible(_extentHeight - ExtentBottomPadding);
    }

    private void OnPageScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsScrollViewportViable())
        {
            DashboardShadowSuspender.TrySuspend(this, this);
            return;
        }

        DashboardShadowSuspender.TryResume(this);

        if (e.WidthChanged)
        {
            _lastMeasureWidth = double.NaN;
            _sectionTopIsCurrent = false;
            _sectionTop = double.NaN;
            InvalidateMeasure();
        }

        if (!IsMotionViewportStillFrozen())
            ScheduleViewportSync();
    }

    private void ScheduleViewportSync()
    {
        if (IsMotionViewportStillFrozen() || _viewportSyncScheduled)
            return;

        if (IsSmoothScrollActive)
        {
            var elapsedMs = (DateTime.UtcNow - _lastSmoothScrollSyncUtc).TotalMilliseconds;
            if (elapsedMs < ResolveSmoothScrollSyncIntervalMs())
                return;
        }

        _viewportSyncScheduled = true;
        var priority = IsSmoothScrollActive
            ? DispatcherPriority.Background
            : DispatcherPriority.Render;

        Dispatcher.BeginInvoke(() =>
        {
            _viewportSyncScheduled = false;
            if (IsSmoothScrollActive)
                _lastSmoothScrollSyncUtc = DateTime.UtcNow;

            ApplyViewportScroll();

            if (ScrollPerfDiag.Enabled && IsSmoothScrollActive)
            {
                ScrollPerfDiag.Log(
                    $"sliceSync\tviewportTop={ResolveEffectiveVerticalOffset():F0}\tsliceOffset={_lastSyncedSliceOffset:F0}\tcoverBottom={_sectionTop + _lastSyncedSliceOffset + _lastSyncedSliceHeight:F0}\tvel={_smoothScrollVelocityPxPerMs:F3}\tahead={ResolveViewportCachePixels():F0}\tabove={ResolveViewportCacheAbovePixels():F0}\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}");
            }
        }, priority);
    }

    private void ForceViewportSync()
    {
        _viewportSyncScheduled = false;
        ApplyViewportScroll(force: true);
    }

    private void ApplyViewportScroll(bool force = false)
    {
        if (IsMotionViewportStillFrozen() || !IsScrollViewportViable())
            return;

        if (Grid is not { } grid)
            return;

        if (!UsesPageVirtualization)
        {
            ApplyStaticViewportScroll(force);
            return;
        }

        TryUpdateSectionTop();

        var width = ResolveMeasureWidth(new Size(double.PositiveInfinity, double.PositiveInfinity), grid);
        if (width <= 0)
            return;

        var contentExtent = EstimateContentExtent(grid, width);
        var extentHint = ResolveEffectiveExtent(grid, width, contentExtent);
        ComputeVisibleSlice(width, extentHint, out var sliceOffset, out var sliceHeight);
        var measureHeight = ResolveMeasureHeight(sliceHeight, extentHint);
        // Only pad up to a minimum-viable slice when visibility is genuinely uncertain (viewport
        // not yet known). If ResolveMeasureHeight/IsSectionPotentiallyVisible already determined
        // this section is definitively off-screen, forcing a slice here would apply the wrong
        // (stale, offset-0) region as if it were visible - see the comment on this same pattern in
        // MeasureStaticHostContent for the full mechanism and how it was diagnosed.
        if (measureHeight <= 0 && contentExtent > 0 && IsSectionPotentiallyVisible(extentHint))
            measureHeight = Math.Max(1, Math.Min(contentExtent, ResolveViewportMeasureCap()));

        if (measureHeight <= 0)
            return;

        if (!force && SlicesAreEquivalent(grid, sliceOffset, measureHeight))
            return;

        ApplyViewportSlice(grid, sliceOffset, measureHeight);
    }

    private void ApplyStaticViewportScroll(bool force)
    {
        if (Grid is not { } grid)
            return;

        TryUpdateSectionTop();

        var width = ResolveMeasureWidth(new Size(double.PositiveInfinity, double.PositiveInfinity), grid);
        if (width <= 0)
            return;

        var contentExtent = EstimateContentExtent(grid, width);
        ComputeVisibleSlice(width, contentExtent, out var sliceOffset, out var sliceHeight);
        var measureHeight = CapGridMeasureHeight(
            grid,
            sliceOffset,
            ResolveMeasureHeight(sliceHeight, contentExtent),
            contentExtent,
            contentExtent);

        if (measureHeight <= 0)
            return;

        if (!force && SlicesAreEquivalent(grid, sliceOffset, measureHeight))
            return;

        ApplyViewportSlice(grid, sliceOffset, measureHeight);
    }

    private void ApplyViewportSlice(DashboardVirtualizedCardGrid grid, double sliceOffset, double measureHeight)
    {
        var previousOffset = _lastSyncedSliceOffset;
        var previousHeight = _lastSyncedSliceHeight;
        // Height uses the same drift tolerance as offset (not the raw 1px SliceEquivalencePixels):
        // GetExtentHeight() shifts by a few px between passes as specific realized cards' real
        // heights replace the uniform estimate, and a 1px-exact threshold turned that convergence
        // jitter into a long chain of full realize/derealize remeasures (10+ passes observed,
        // each 40-300ms) before finally landing within 1px.
        var equivalenceTolerance = ResolveSliceEquivalencePixels(grid);
        var offsetChanged = double.IsNaN(previousOffset)
            || Math.Abs(sliceOffset - previousOffset) >= SliceEquivalencePixels;
        var heightChanged = double.IsNaN(previousHeight)
            || Math.Abs(measureHeight - previousHeight) >= equivalenceTolerance;
        var hostMeasureStride = ResolveHostMeasureStride(grid);
        var requiresHostMeasure = double.IsNaN(previousOffset)
            || Math.Abs(sliceOffset - previousOffset) >= hostMeasureStride
            || heightChanged;

        _sliceOffset = sliceOffset;
        _sliceHeight = measureHeight;
        _lastSyncedSliceOffset = sliceOffset;
        _lastSyncedSliceHeight = measureHeight;

        grid.ApplyPageViewport(sliceOffset, measureHeight);

        if (!IsSmoothScrollActive || heightChanged || requiresHostMeasure)
            grid.InvalidateMeasure();

        if (requiresHostMeasure)
            InvalidateMeasure();

        if (offsetChanged || heightChanged)
            InvalidateArrange();
    }

    private double ResolveHostMeasureStride(DashboardVirtualizedCardGrid grid)
    {
        if (IsSmoothScrollActive)
            return SmoothScrollHostMeasureStridePixels;

        return grid.UsesUniformPanelLayout()
            ? UniformHostMeasureStridePixels
            : SliceEquivalencePixels;
    }

    private double ResolveSliceEquivalencePixels(DashboardVirtualizedCardGrid grid)
    {
        if (IsSmoothScrollActive)
            return SmoothScrollSliceEquivalencePixels;

        return grid.UsesUniformPanelLayout()
            ? UniformSliceEquivalencePixels
            : SliceEquivalencePixels;
    }

    private bool SlicesAreEquivalent(DashboardVirtualizedCardGrid grid, double sliceOffset, double measureHeight)
    {
        var tolerance = ResolveSliceEquivalencePixels(grid);

        return !double.IsNaN(_lastSyncedSliceOffset)
            && Math.Abs(sliceOffset - _lastSyncedSliceOffset) < tolerance
            && Math.Abs(measureHeight - _lastSyncedSliceHeight) < tolerance;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Grid is not { } grid)
            return new Size(0, 0);

        _isInternalLayoutPass = true;
        try
        {
            if (!ScrollPerfDiag.Enabled)
                return MeasureHostContent(availableSize, grid);

            var viewportHeight = _pageScrollViewer?.ViewportHeight ?? -1;
            var owningWindowMinimized = IsOwningWindowMinimized(this);
            var viewportViable = !owningWindowMinimized
                && (_pageScrollViewer is null || viewportHeight >= MinViableViewportHeight);
            var preservesSnapshot = !viewportViable && _extentHeight > ExtentBottomPadding;
            ScrollPerfDiag.Log(
                $"hostMeasureState\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}\tscope={MotionScope}\tmode={HostMode}\titems={grid.Items.Count}\textent={_extentHeight:F0}\tviewportHeight={viewportHeight:F0}\tminimized={owningWindowMinimized}\tviable={viewportViable}\tpreserve={preservesSnapshot}\tmotionFrozen={_motionViewportFrozen}");

            var hadRealizedBefore = grid.HasRealizedContainers();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = MeasureHostContent(availableSize, grid);
            ScrollPerfDiag.Log($"hostMeasure\t{stopwatch.Elapsed.TotalMilliseconds:F2}\titems={grid.Items.Count}\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}\tscope={MotionScope}\thadRealized={hadRealizedBefore}");
            return result;
        }
        finally
        {
            _isInternalLayoutPass = false;
        }
    }

    private Size MeasureHostContent(Size availableSize, DashboardVirtualizedCardGrid grid)
    {
        var previousItemCount = _lastItemCount;
        var itemCountChanged = grid.Items.Count != previousItemCount;

        if (grid.Items.Count == 0)
        {
            ResetHostLayoutState(grid);
            return new Size(width: ResolveMeasureWidth(availableSize), height: 0);
        }

        if (itemCountChanged)
        {
            var addedItems = grid.Items.Count > previousItemCount;
            _lastItemCount = grid.Items.Count;
            var stillFrozen = IsMotionViewportStillFrozen();
            if (!stillFrozen || addedItems)
            {
                BeginExtentRemeasure();
                if (!stillFrozen)
                    ClearSliceSyncState();
            }
        }

        if (ShouldPreserveLayoutSnapshot())
        {
            var cachedWidth = ResolveMeasureWidth(availableSize, grid);
            return cachedWidth > 0
                ? new Size(cachedWidth, _extentHeight)
                : new Size(0, Math.Max(_extentHeight, 0));
        }

        if (!UsesPageVirtualization)
            return MeasureStaticHostContent(availableSize, grid);

        TryUpdateSectionTop();

        var width = ResolveMeasureWidth(availableSize, grid);
        if (width <= 0)
            return new Size(0, 0);

        if (!double.IsNaN(_lastMeasureWidth) && Math.Abs(width - _lastMeasureWidth) > 0.5)
        {
            _extentHeight = 0;
            ClearSliceSyncState();
        }

        _lastMeasureWidth = width;

        var contentExtent = EstimateContentExtent(grid, width);
        var extentHint = ResolveEffectiveExtent(grid, width, contentExtent);
        ComputeVisibleSlice(width, extentHint, out var sliceOffset, out var sliceHeight);
        var measureHeight = ResolveMeasureHeight(sliceHeight, extentHint);
        // See the matching comment in ApplyViewportScroll/MeasureStaticHostContent: only pad up to
        // a minimum-viable slice when visibility is genuinely uncertain, not when it's already been
        // determined this section is definitively scrolled out of view.
        if (measureHeight <= 0 && contentExtent > 0 && IsSectionPotentiallyVisible(extentHint))
            measureHeight = Math.Max(1, Math.Min(contentExtent, ResolveViewportMeasureCap()));

        if (IsMotionViewportStillFrozen() && _frozenSliceHeight > 0)
        {
            sliceOffset = _frozenSliceOffset;
            sliceHeight = _frozenSliceHeight;
            measureHeight = _frozenSliceHeight;
        }

        var diagPrevChildCount = ScrollPerfDiag.Enabled ? grid.ResolveItemsPanel()?.Children.Count ?? -1 : -1;

        _sliceOffset = sliceOffset;
        _sliceHeight = measureHeight;
        _lastSyncedSliceOffset = sliceOffset;
        _lastSyncedSliceHeight = measureHeight;

        grid.ApplyPageViewport(sliceOffset, measureHeight);
        grid.Measure(new Size(width, measureHeight));

        if (ScrollPerfDiag.Enabled)
        {
            var diagChildCount = grid.ResolveItemsPanel()?.Children.Count ?? -1;
            ScrollPerfDiag.Log(
                $"measureSlice\tsliceOffset={sliceOffset:F0}\theight={measureHeight:F0}\textent={_extentHeight:F0}\tcontentExtent={contentExtent:F0}\tchildren={diagPrevChildCount}->{diagChildCount}\tsectionTop={_sectionTop:F0}\tviewportTop={ResolveEffectiveVerticalOffset():F0}\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}");
        }

        if (grid.HasItems && measureHeight > 0 && !grid.HasRealizedContainers())
            RemeasureGridForRealization(grid, width, measureHeight, sliceOffset);

        var measuredExtent = ResolveMeasuredExtent(
            grid,
            contentExtent,
            out var panelExtent,
            out var extentSource);

        var cappedHeight = CapGridMeasureHeight(grid, sliceOffset, measureHeight, measuredExtent, contentExtent);
        if (Math.Abs(cappedHeight - measureHeight) > 0.5)
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
            grid.ApplyPageViewport(sliceOffset, measureHeight);
            grid.Measure(new Size(width, measureHeight));
            measuredExtent = ResolveMeasuredExtent(
                grid,
                contentExtent,
                out panelExtent,
                out extentSource);
        }
        else
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
        }

        CommitMeasuredExtent(measuredExtent);
        LogMeasuredExtent(grid, contentExtent, panelExtent, measuredExtent, extentSource);

        return new Size(width, Math.Max(_extentHeight, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Grid is { } grid)
        {
            var width = finalSize.Width;
            var height = Math.Max(_sliceHeight, 0);
            grid.ApplyPageViewport(_sliceOffset, height);
            grid.Arrange(new Rect(0, _sliceOffset, width, height));
        }

        ApplyPendingExtentScrollCompensation();
        return finalSize;
    }

    private Size MeasureStaticHostContent(Size availableSize, DashboardVirtualizedCardGrid grid)
    {
        var width = ResolveMeasureWidth(availableSize, grid);
        if (width <= 0)
            return new Size(0, 0);

        _lastMeasureWidth = width;
        TryUpdateSectionTop();

        var contentExtent = EstimateContentExtent(grid, width);
        ComputeVisibleSlice(width, contentExtent, out var sliceOffset, out var sliceHeight);
        var measureHeight = ResolveMeasureHeight(sliceHeight, contentExtent);
        // ResolveMeasureHeight already returns >0 whenever IsSectionPotentiallyVisible was true
        // (falling back to a viewport-sized slice); it only returns 0 when that check explicitly
        // determined this section is scrolled out of view. Padding it back up here regardless of
        // that - as this used to do unconditionally - forces the panel to realize its content
        // starting at sliceOffset 0 (this section's own top) even when the real scroll position is
        // deep inside or past it, leaving everything beyond that first forced slice unrealized: a
        // blank gap where the rest of this group's cards should be. Confirmed via
        // measureSliceStatic diagnostic logging showing sliceOffset stuck at 0 with viewportTop far
        // past sectionTop + contentExtent. Only pad when visibility is genuinely uncertain (e.g.
        // the page ScrollViewer isn't attached yet), matching IsSectionPotentiallyVisible's own
        // "assume visible" default for that case.
        if (measureHeight <= 0 && IsSectionPotentiallyVisible(contentExtent))
        {
            measureHeight = contentExtent > 0
                ? Math.Max(1, Math.Min(contentExtent, ResolveViewportMeasureCap()))
                : Math.Max(contentExtent, 1);
        }

        if (ScrollPerfDiag.Enabled)
        {
            ScrollPerfDiag.Log(
                $"measureSliceStatic\tsliceOffset={sliceOffset:F0}\theight={measureHeight:F0}\tcontentExtent={contentExtent:F0}\tsectionTop={_sectionTop:F0}\tviewportTop={ResolveEffectiveVerticalOffset():F0}\titems={grid.Items.Count}\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}");
        }

        _sliceOffset = sliceOffset;
        _sliceHeight = measureHeight;
        _lastSyncedSliceOffset = sliceOffset;
        _lastSyncedSliceHeight = measureHeight;

        grid.ApplyPageViewport(sliceOffset, measureHeight);
        grid.Measure(new Size(width, measureHeight));

        if (grid.HasItems && measureHeight > 0 && !grid.HasRealizedContainers())
            RemeasureGridForRealization(grid, width, measureHeight, sliceOffset);

        var measuredExtent = ResolveMeasuredExtent(
            grid,
            contentExtent,
            out var panelExtent,
            out var extentSource);

        var cappedHeight = CapGridMeasureHeight(grid, sliceOffset, measureHeight, measuredExtent, contentExtent);
        if (Math.Abs(cappedHeight - measureHeight) > 0.5)
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
            grid.ApplyPageViewport(sliceOffset, measureHeight);
            grid.Measure(new Size(width, measureHeight));
            measuredExtent = ResolveMeasuredExtent(
                grid,
                contentExtent,
                out panelExtent,
                out extentSource);
        }
        else
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
        }

        CommitMeasuredExtent(measuredExtent);
        LogMeasuredExtent(grid, contentExtent, panelExtent, measuredExtent, extentSource);

        return new Size(width, Math.Max(_extentHeight, 0));
    }

    private double CapGridMeasureHeight(
        DashboardVirtualizedCardGrid grid,
        double sliceOffset,
        double measureHeight,
        double measuredExtent,
        double contentExtent)
    {
        var viewportCap = ResolveViewportMeasureCap();
        var effectiveExtent = Math.Max(measuredExtent, contentExtent);

        if (sliceOffset < 1
            && effectiveExtent > measureHeight + 0.5
            && (grid.Items.Count <= 12 || effectiveExtent <= viewportCap))
            return Math.Min(effectiveExtent, viewportCap);

        return Math.Min(Math.Max(measureHeight, 1), viewportCap);
    }

    private double ResolveViewportMeasureCap()
    {
        var cache = ResolveViewportCachePixels();
        if (_pageScrollViewer is { ViewportHeight: > 0 })
            return _pageScrollViewer.ViewportHeight + cache * 2;

        return BootstrapViewportHeight + cache;
    }

    private double ResolveMeasureWidth(Size availableSize, DashboardVirtualizedCardGrid? grid = null)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
            return availableSize.Width;

        if (ActualWidth > 0)
            return ActualWidth;

        if (grid is { ActualWidth: > 0 })
            return grid.ActualWidth;

        if (_pageScrollViewer is { } scrollViewer)
        {
            var width = scrollViewer.ViewportWidth > 0
                ? scrollViewer.ViewportWidth
                : scrollViewer.ActualWidth;

            width -= scrollViewer.Padding.Left + scrollViewer.Padding.Right;
            return Math.Max(0, width);
        }

        return 0;
    }

    private double ResolveEffectiveExtent(DashboardVirtualizedCardGrid grid, double width, double contentExtent)
    {
        if (_extentHeight > ExtentBottomPadding)
            return Math.Max(contentExtent, _extentHeight - ExtentBottomPadding);

        return contentExtent;
    }

    private static double ResolveMeasuredExtent(
        DashboardVirtualizedCardGrid grid,
        double contentExtent,
        out double panelExtent,
        out string extentSource)
    {
        panelExtent = grid.GetPanelExtentHeight();
        if (contentExtent > 0 && grid.UsesUniformPanelLayout())
        {
            extentSource = "estimate";
            return contentExtent;
        }

        extentSource = "grid";
        var gridExtent = grid.GetExtentHeight();
        return gridExtent > 0 ? Math.Max(gridExtent, contentExtent) : contentExtent;
    }

    private void LogMeasuredExtent(
        DashboardVirtualizedCardGrid grid,
        double contentExtent,
        double panelExtent,
        double measuredExtent,
        string extentSource)
    {
        if (!ScrollPerfDiag.Enabled)
            return;

        ScrollPerfDiag.Log(
            $"measureExtent\tid={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}\tscope={MotionScope}\tmode={HostMode}\titems={grid.Items.Count}\tcontentExtent={contentExtent:F0}\tpanelExtent={panelExtent:F0}\tcommittedExtent={measuredExtent:F0}\tsource={extentSource}");
    }

    private static double EstimateContentExtent(DashboardVirtualizedCardGrid grid, double width)
    {
        if (grid.Items.Count == 0 || width <= 0)
            return 0;

        if (grid is DashboardVirtualizedNoteCardGrid noteGrid)
            return noteGrid.EstimateLayoutExtent(width);

        if (grid.ItemCellSize is { Width: > 0, Height: > 0 } cellSize)
        {
            var perRow = Math.Max(1, (int)Math.Floor(width / cellSize.Width));
            var rows = (int)Math.Ceiling(grid.Items.Count / (double)perRow);
            return rows * cellSize.Height;
        }

        return 0;
    }

    private void ResetHostLayoutState(DashboardVirtualizedCardGrid grid)
    {
        _extentHeight = 0;
        _extentSnapshotBeforeMutation = 0;
        _pendingExtentScrollCompensation = 0;
        _sectionTop = double.NaN;
        _sectionTopIsCurrent = false;
        _sliceOffset = 0;
        _sliceHeight = 0;
        _lastItemCount = 0;
        _lastMeasureWidth = double.NaN;
        _lastSyncedSliceOffset = double.NaN;
        _lastSyncedSliceHeight = double.NaN;
        grid.ResetPageViewport();
    }

    private void ApplyMutationRefresh()
    {
        _mutationReapplyDeferred = false;
        InvalidateExtentForMutation(clearGridLayoutCache: true);
    }

    private void BeginExtentRemeasure()
    {
        CaptureExtentSnapshot();
        _extentHeight = 0;
    }

    private void ClearSliceSyncState()
    {
        _sectionTop = double.NaN;
        _sectionTopIsCurrent = false;
        _lastSyncedSliceOffset = double.NaN;
        _lastSyncedSliceHeight = double.NaN;
    }

    private void CaptureExtentSnapshot()
    {
        if (_extentHeight > ExtentBottomPadding)
            _extentSnapshotBeforeMutation = _extentHeight;
    }

    private void CommitMeasuredExtent(double measuredExtent)
    {
        var newExtentHeight = measuredExtent + ExtentBottomPadding;
        QueueExtentScrollCompensation(newExtentHeight);
        _extentHeight = newExtentHeight;
    }

    /// <summary>
    /// When this listing grows above the viewport, keep sections below (e.g. groups) anchored.
    /// </summary>
    private void QueueExtentScrollCompensation(double newExtentHeight)
    {
        var snapshot = _extentSnapshotBeforeMutation;
        _extentSnapshotBeforeMutation = 0;

        if (snapshot <= ExtentBottomPadding
            || newExtentHeight <= snapshot + 0.5
            || _pageScrollViewer is null
            || !TryUpdateSectionTop())
            return;

        var sectionBottom = _sectionTop + (snapshot - ExtentBottomPadding);
        if (_pageScrollViewer.VerticalOffset < sectionBottom - 1)
            return;

        _pendingExtentScrollCompensation = newExtentHeight - snapshot;
    }

    private void ApplyPendingExtentScrollCompensation()
    {
        if (_pendingExtentScrollCompensation <= 0.5 || _pageScrollViewer is null)
        {
            _pendingExtentScrollCompensation = 0;
            return;
        }

        var delta = _pendingExtentScrollCompensation;
        _pendingExtentScrollCompensation = 0;
        var target = Math.Clamp(
            _pageScrollViewer.VerticalOffset + delta,
            0,
            Math.Max(_pageScrollViewer.ScrollableHeight, _pageScrollViewer.VerticalOffset + delta));

        if (Math.Abs(target - _pageScrollViewer.VerticalOffset) < 0.5)
            return;

        _pageScrollViewer.ScrollToVerticalOffset(target);
    }

    private double ResolveMeasureHeight(double sliceHeight, double extentHint = 0)
    {
        if (sliceHeight > 0)
            return sliceHeight;

        if (!IsSectionPotentiallyVisible(extentHint))
            return 0;

        var viewportSlice = _pageScrollViewer is { ViewportHeight: > 0 }
            ? _pageScrollViewer.ViewportHeight + ResolveViewportCachePixels()
            : BootstrapViewportHeight;

        if (extentHint > 0)
            return Math.Min(extentHint, viewportSlice);

        return viewportSlice;
    }

    private bool IsSectionPotentiallyVisible(double extentHint = 0)
    {
        if (_pageScrollViewer is null)
            return true;

        if (double.IsNaN(_sectionTop))
            return true;

        var effectiveExtent = extentHint > 0 ? extentHint : _extentHeight;
        var viewportTop = ResolveEffectiveVerticalOffset();
        var viewportBottom = viewportTop + _pageScrollViewer.ViewportHeight;
        if (_sectionTop >= viewportBottom)
            return false;

        return effectiveExtent <= 0 || _sectionTop + effectiveExtent > viewportTop;
    }

    private bool TryUpdateSectionTop()
    {
        if (_sectionTopIsCurrent && !double.IsNaN(_sectionTop))
            return true;

        if (_pageScrollViewer?.Content is not UIElement scrollContent)
            return false;

        try
        {
            var point = TranslatePoint(new Point(0, 0), scrollContent);
            if (double.IsNaN(point.Y) || double.IsInfinity(point.Y))
                return false;

            _sectionTop = Math.Max(0, point.Y);
            _sectionTopIsCurrent = true;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ComputeVisibleSlice(double width, double extentHint, out double sliceOffset, out double sliceHeight)
    {
        sliceOffset = 0;
        sliceHeight = 0;

        if (width <= 0)
            return;

        if (_pageScrollViewer is null)
        {
            sliceHeight = BootstrapViewportHeight;
            return;
        }

        if (!TryUpdateSectionTop())
            return;

        var effectiveExtent = extentHint > 0 ? extentHint : _extentHeight;
        var viewportTop = ResolveEffectiveVerticalOffset();
        var viewportBottom = viewportTop + _pageScrollViewer.ViewportHeight;

        if (_sectionTop >= viewportBottom)
            return;

        if (effectiveExtent > 0 && _sectionTop + effectiveExtent <= viewportTop)
            return;

        sliceOffset = Math.Max(0, viewportTop - _sectionTop);
        var visibleBottom = viewportBottom - _sectionTop;
        if (effectiveExtent > 0)
            visibleBottom = Math.Min(visibleBottom, effectiveExtent);

        sliceHeight = Math.Max(0, visibleBottom - sliceOffset);

        if (sliceHeight <= 0 && IsSectionPotentiallyVisible(effectiveExtent))
        {
            sliceOffset = Math.Max(0, viewportTop - _sectionTop);
            var fallbackHeight = _pageScrollViewer.ViewportHeight + ResolveViewportCachePixels();
            if (effectiveExtent > 0)
                fallbackHeight = Math.Min(fallbackHeight, Math.Max(effectiveExtent - sliceOffset, 1));

            sliceHeight = fallbackHeight;
        }

        if (sliceHeight <= 0)
            return;

        var cachePixels = ResolveViewportCachePixels();
        if (effectiveExtent > 0)
            sliceHeight = Math.Min(sliceHeight + cachePixels, Math.Max(effectiveExtent - sliceOffset, sliceHeight));
        else
            sliceHeight += cachePixels;

        var cacheAbove = ResolveViewportCacheAbovePixels();
        if (cacheAbove > 0 && sliceOffset > 0)
        {
            var extendedOffset = Math.Max(0, sliceOffset - cacheAbove);
            sliceHeight += sliceOffset - extendedOffset;
            sliceOffset = extendedOffset;
        }

        if (sliceOffset < 1 && effectiveExtent > 0)
        {
            var available = viewportBottom - _sectionTop;
            if (effectiveExtent <= available + 1)
                sliceHeight = Math.Max(sliceHeight, effectiveExtent);
        }
    }

    private static void RemeasureGridForRealization(
        DashboardVirtualizedCardGrid grid,
        double width,
        double measureHeight,
        double sliceOffset)
    {
        var offset = sliceOffset < 1 ? 0 : sliceOffset;
        grid.ApplyPageViewport(offset, measureHeight);
        grid.Measure(new Size(width, measureHeight));
    }

    private bool IsScrollViewportViable()
    {
        if (IsOwningWindowMinimized(this))
            return false;

        return _pageScrollViewer is null
            || _pageScrollViewer.ViewportHeight >= MinViableViewportHeight;
    }

    private bool ShouldPreserveLayoutSnapshot()
        => !IsScrollViewportViable() && _extentHeight > ExtentBottomPadding;

    private static bool IsOwningWindowMinimized(DependencyObject element)
        => Window.GetWindow(element) is { WindowState: WindowState.Minimized };
}
