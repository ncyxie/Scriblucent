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
        _frozenSliceOffset = _sliceOffset;
        _frozenSliceHeight = Math.Max(_sliceHeight, 1);
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
        InvalidateExtentForMutation();
    }

    internal void NotifySmoothScrollStarted()
    {
        _smoothScrollDepth++;
    }

    internal void NotifySmoothScrollEnded()
    {
        if (_smoothScrollDepth <= 0)
            return;

        _smoothScrollDepth--;
        if (_smoothScrollDepth != 0)
            return;

        _lastSmoothScrollSyncUtc = DateTime.MinValue;
        ForceViewportSync();
        Grid?.FlushDeferredInteractiveSweep();
    }

    internal void NotifyVisualScrollOffsetChanged()
    {
        if (!IsSmoothScrollActive || _motionViewportFrozen || !IsScrollViewportViable())
            return;

        ScheduleViewportSync();
    }

    private bool IsSmoothScrollActive => _smoothScrollDepth > 0;

    private double ResolveSmoothScrollSyncIntervalMs()
    {
        var viewport = _pageScrollViewer?.ViewportHeight ?? BootstrapViewportHeight;
        return Math.Clamp(18 + viewport * 0.022, MinSmoothScrollSyncIntervalMs, MaxSmoothScrollSyncIntervalMs);
    }

    // Rows realize (and get bitmap-cached) while still off screen, so their first-render
    // cost is not paid on the frame they scroll into view. The margin is sized to a roughly
    // constant CARD count, not a constant pixel/row count: a wrap grid on a maximized window
    // packs far more columns per row than the same grid on a narrower window, so a fixed
    // pixel margin realizes (and every scroll frame composites) proportionally more cards as
    // the window widens. Scaling the margin down as columns increase keeps the extra-card
    // budget — and the per-frame compositing cost during scroll — roughly window-size-invariant.
    private double ResolveViewportCachePixels()
    {
        if (!IsSmoothScrollActive)
            return ViewportCachePixels;

        return ResolveCardBudgetPixels(TargetExtraCardsAhead, ViewportCachePixels);
    }

    private double ResolveViewportCacheAbovePixels()
    {
        if (!IsSmoothScrollActive)
            return ViewportCacheAbovePixels;

        return ResolveCardBudgetPixels(TargetExtraCardsBehind, ViewportCacheAbovePixels);
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

    private void InvalidateExtentForMutation()
    {
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
        AttachScrollViewer(ScrollViewerElement ?? DashboardMotionViewport.FindScrollViewer(this));

        InvalidateMeasure();

        Dispatcher.BeginInvoke(() =>
        {
            InvalidateMeasure();
            Grid?.EnsureRealizedCardsInteractive();
        }, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DashboardShadowSuspender.TryResume(this);
        DetachScrollViewer();
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
        if (_motionViewportFrozen || _isInternalLayoutPass || !IsScrollViewportViable())
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

        if (!_motionViewportFrozen)
            ScheduleViewportSync();
    }

    private void ScheduleViewportSync()
    {
        if (_motionViewportFrozen || _viewportSyncScheduled)
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
        }, priority);
    }

    private void ForceViewportSync()
    {
        _viewportSyncScheduled = false;
        ApplyViewportScroll(force: true);
    }

    private void ApplyViewportScroll(bool force = false)
    {
        if (_motionViewportFrozen || !IsScrollViewportViable())
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
        if (measureHeight <= 0 && contentExtent > 0)
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

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = MeasureHostContent(availableSize, grid);
            ScrollPerfDiag.Log($"hostMeasure\t{stopwatch.Elapsed.TotalMilliseconds:F2}\titems={grid.Items.Count}");
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
            if (!_motionViewportFrozen || addedItems)
            {
                BeginExtentRemeasure();
                if (!_motionViewportFrozen)
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
        if (measureHeight <= 0 && contentExtent > 0)
            measureHeight = Math.Max(1, Math.Min(contentExtent, ResolveViewportMeasureCap()));

        if (_motionViewportFrozen && _frozenSliceHeight > 0)
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
                $"measureSlice\tsliceOffset={sliceOffset:F0}\theight={measureHeight:F0}\textent={_extentHeight:F0}\tcontentExtent={contentExtent:F0}\tchildren={diagPrevChildCount}->{diagChildCount}");
        }

        if (grid.HasItems && measureHeight > 0 && !grid.HasRealizedContainers())
            RemeasureGridForRealization(grid, width, measureHeight, sliceOffset);

        var measuredExtent = grid.GetExtentHeight();
        if (measuredExtent <= 0)
            measuredExtent = contentExtent;

        measuredExtent = Math.Max(measuredExtent, contentExtent);

        var cappedHeight = CapGridMeasureHeight(grid, sliceOffset, measureHeight, measuredExtent, contentExtent);
        if (Math.Abs(cappedHeight - measureHeight) > 0.5)
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
            grid.ApplyPageViewport(sliceOffset, measureHeight);
            grid.Measure(new Size(width, measureHeight));
            measuredExtent = Math.Max(measuredExtent, grid.GetExtentHeight());
            measuredExtent = Math.Max(measuredExtent, contentExtent);
        }
        else
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
        }

        CommitMeasuredExtent(measuredExtent);

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
        if (measureHeight <= 0 && contentExtent > 0)
            measureHeight = Math.Max(1, Math.Min(contentExtent, ResolveViewportMeasureCap()));
        else if (measureHeight <= 0)
            measureHeight = Math.Max(contentExtent, 1);

        _sliceOffset = sliceOffset;
        _sliceHeight = measureHeight;
        _lastSyncedSliceOffset = sliceOffset;
        _lastSyncedSliceHeight = measureHeight;

        grid.ApplyPageViewport(sliceOffset, measureHeight);
        grid.Measure(new Size(width, measureHeight));

        if (grid.HasItems && measureHeight > 0 && !grid.HasRealizedContainers())
            RemeasureGridForRealization(grid, width, measureHeight, sliceOffset);

        var measuredExtent = grid.GetExtentHeight();
        if (measuredExtent <= 0)
            measuredExtent = contentExtent;

        measuredExtent = Math.Max(measuredExtent, contentExtent);

        var cappedHeight = CapGridMeasureHeight(grid, sliceOffset, measureHeight, measuredExtent, contentExtent);
        if (Math.Abs(cappedHeight - measureHeight) > 0.5)
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
            grid.ApplyPageViewport(sliceOffset, measureHeight);
            grid.Measure(new Size(width, measureHeight));
            measuredExtent = Math.Max(measuredExtent, grid.GetExtentHeight());
        }
        else
        {
            measureHeight = cappedHeight;
            _sliceHeight = measureHeight;
            _lastSyncedSliceHeight = measureHeight;
        }

        CommitMeasuredExtent(measuredExtent);

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
        InvalidateExtentForMutation();
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
