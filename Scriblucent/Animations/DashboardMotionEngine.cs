using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Services;

namespace Scriblucent.Animations;

/// <summary>
/// Snapshots dashboard card positions by id and animates reflow, enter, and exit motion.
/// All dashboards share one model: hold the layout slot, fade the motion surface, FLIP siblings.
/// </summary>
public sealed class DashboardMotionEngine
{
    private const int EnterBurstWindowMs = 500;
    private const int MaxBurstAddsBeforeSkip = 5;
    private const int MaxLayoutWaitAttempts = 12;

    private static readonly Dictionary<DashboardMotionScope, DashboardMotionEngine> ActiveByScope = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> CardExitMotionKeys = new();

    private readonly Dispatcher _dispatcher;
    private readonly DashboardMotionScope _scope;
    private readonly object _reflowBatchKey = new();

    private FrameworkElement? _dashboardRoot;
    private FrameworkElement? _ungroupedSection;
    private FrameworkElement? _ungroupedItemsControl;
    private ScrollViewer? _scrollViewer;
    private readonly Dictionary<Guid, Point> _positionSnapshots = new();
    private readonly Dictionary<Guid, Panel> _panelByItemId = new();
    private readonly Dictionary<Guid, FrameworkElement> _layoutRootByItemId = new();
    private readonly Dictionary<Guid, FrameworkElement> _reflowTargetByItemId = new();
    private readonly Dictionary<Guid, int> _indexSnapshots = new();
    private readonly HashSet<FrameworkElement> _activeReflowTargets = new();

    private readonly HashSet<Guid> _enteringItemIds = new();
    private long _lastAddTimestampMs;
    private int _rapidAddCount;
    private bool _deleteBatchCommitted;
    private DispatcherTimer? _deleteBatchSafetyTimer;
    private bool _moveBatchCommitted;
    private DispatcherTimer? _moveBatchSafetyTimer;
    private readonly HashSet<Guid> _relocatingItemIds = new();
    private bool _reflowScheduled;
    private readonly HashSet<Guid> _scheduledEnterIds = new();
    private readonly HashSet<Guid> _deferredEnterIds = new();
    private readonly HashSet<Guid> _playingEnterIds = new();

    // An item can be marked "playing" by a completely untracked, un-watchdogged replay attempt -
    // OnItemContainerRealized or FinishReflow's deferred-retry continuation both call
    // TryPlayEnterForItem with onComplete = null when a container wasn't found on the item's
    // first (tracked) attempt. If that replay's RenderTransitionClock gets Stop()'d before
    // completing (same drop-on-cancel mechanism as _pendingEnterCompletions), nothing ever
    // removes the id from _playingEnterIds, and TryPlayEnterForItem's first guard then silently
    // skips this item's animation forever - it just appears wherever the interrupted animation
    // visually left it. This timestamp bounds how long "playing" is trusted before it's treated
    // as stale and eligible for replay/snap-to-final, mirroring the MaxMotionViewportFreezeMs
    // idiom in DashboardVirtualizationHost.
    private readonly Dictionary<Guid, long> _playingEnterStartedAtMs = new();
    private readonly HashSet<Guid> _instantEnterIds = new();
    private int _layoutAnimationDepth;
    private int _batchMutationDepth;
    private bool _batchMutationInFlight;
    private bool _batchMutationReflowRunning;
    private readonly List<Action> _batchMutationAfterMotion = new();
    private Action? _onReflowComplete;

    // Keyed by item id so a card that leaves the tree mid-animation (its RenderTransitionClock
    // gets Stop()'d by NoteCardControl.OnUnloaded -> CardSurfaceMotionBehavior.Cleanup ->
    // UiMotion.Cleanup, which silently drops the completion callback - RenderTransitionClock.Stop
    // clears _onAllComplete without invoking it) can still have ITS specific AnimateReflow pass's
    // completion retired from outside that pass's own closure. Without this, one dropped callback
    // means that pass's remainingEnters-equivalent count never reaches zero, FinishReflow() never
    // runs, and every DashboardVirtualizationHost in the scope stays frozen (see
    // FreezeVirtualizationViewports/UnfreezeVirtualizationViewports) for the rest of the process's
    // life - the dashboard stops responding to scroll and a small scroll makes cards vanish.
    private readonly Dictionary<Guid, Action> _pendingEnterCompletions = new();
    private DispatcherTimer? _reflowSafetyTimer;

    // Watchdog escape hatch for a pass stuck on the REFLOW side, not just the enter side -
    // UiMotion.Run keys one clock per key, so a closely-overlapping second reflow pass on the
    // same _reflowBatchKey silently drops the first pass's sibling-reflow completion callback,
    // meaning reflowComplete can never become true for that pass. The existing retire-based
    // watchdog only ever forces enterComplete, so it cannot rescue this case on its own. Set by
    // AnimateReflow at the start of each pass, invoked by ForceFinishStuckEnters after the
    // retire loop, cleared once FinishReflow actually runs for that pass.
    private Action? _forceFinishPass;

    // Per-pass caches so resolving thousands of entering items' layout roots doesn't cost a
    // fresh O(m) scan (plus a full synchronous layout pass - see FindEnterLayoutRoot) per item.
    // Built lazily on first use within a pass, invalidated wherever the tree is already
    // known-fresh or the item collection changed - see InvalidatePassLookups callers.
    private Dictionary<Guid, (DashboardVirtualizedCardGrid Grid, object Item)>? _enterItemLookup;
    private Dictionary<Guid, int>? _displayIndexLookup;
    private List<DashboardVirtualizedCardGrid>? _passGrids;
    private readonly HashSet<DashboardVirtualizedCardGrid> _layoutSyncedThisPass = new();

    public Action? OnCollectionLayoutChanged { get; set; }

    public bool IsLayoutAnimationActive => _layoutAnimationDepth > 0;

    public static bool ShouldDeferVirtualizationRefresh(DashboardMotionScope? scope = null)
    {
        if (scope is { } singleScope)
        {
            return ActiveByScope.TryGetValue(singleScope, out var engine)
                && engine.IsLayoutAnimationActive;
        }

        foreach (var engine in ActiveByScope.Values)
        {
            if (engine.IsLayoutAnimationActive)
                return true;
        }

        return false;
    }

    private static int _pinReorderDepth;

    public static bool IsPinReorderActive => _pinReorderDepth > 0;

    public static void BeginPinReorder()
        => Interlocked.Increment(ref _pinReorderDepth);

    public static void EndPinReorder()
        => Interlocked.Decrement(ref _pinReorderDepth);

    public static bool ShouldPreserveReflowTransform(FrameworkElement element)
    {
        var current = element as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement framework && HasActiveReflowTransform(framework))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    public void BeginLayoutAnimation()
    {
        if (_layoutAnimationDepth == 0)
            SuspendCardShadowsForMotion();

        _layoutAnimationDepth++;
    }

    public void EndLayoutAnimation()
    {
        if (_layoutAnimationDepth <= 0)
            return;

        _layoutAnimationDepth--;
        if (_layoutAnimationDepth == 0)
            ResumeCardShadowsForMotion();
    }

    private void SuspendCardShadowsForMotion()
    {
        if (_scrollViewer is not null)
            DashboardShadowSuspender.TrySuspend(_scope, _scrollViewer);
        else if (_dashboardRoot is not null)
            DashboardShadowSuspender.TrySuspend(_scope, _dashboardRoot);
    }

    private void ResumeCardShadowsForMotion()
        => DashboardShadowSuspender.TryResume(_scope);

    public DashboardMotionEngine(Dispatcher dispatcher, DashboardMotionScope scope)
    {
        _dispatcher = dispatcher;
        _scope = scope;
    }

    public void Bind(
        FrameworkElement? dashboardRoot,
        FrameworkElement? ungroupedSection,
        FrameworkElement? ungroupedItemsControl,
        ScrollViewer? scrollViewer = null)
    {
        _dashboardRoot = dashboardRoot;
        _ungroupedSection = ungroupedSection;
        _ungroupedItemsControl = ungroupedItemsControl;
        _scrollViewer = scrollViewer ?? DashboardMotionViewport.FindScrollViewer(dashboardRoot);
    }

    public static bool ShouldHoldCollapsedEnter(DashboardMotionScope scope, Guid itemId)
        => ActiveByScope.TryGetValue(scope, out var engine)
            && engine.IsTrackedEnter(itemId);

    public static bool IsPendingEnter(FrameworkElement element)
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
        {
            if (!DashboardMotionCardCatalog.TryResolveCard(element, scope, out var layoutRoot, out _, out var itemId))
                continue;

            if (!ShouldHoldCollapsedEnter(scope, itemId))
                continue;

            if (ActiveByScope.TryGetValue(scope, out var engine)
                && engine.IsAuthorizedEnterLayoutRoot(layoutRoot, itemId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when imperative visibility forcing would fight enter/exit motion.
    /// </summary>
    public static bool ShouldDeferForcedVisibility(FrameworkElement element)
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
        {
            if (!DashboardMotionCardCatalog.TryResolveCard(element, scope, out var layoutRoot, out _, out var itemId))
                continue;

            if (ShouldHoldCollapsedEnter(scope, itemId))
                return true;

            // Exit (and similar holds) set IsHitTestVisible false before opacity animates.
            // Do not require Opacity < 0.99 — post-drag PrepareRecycled* would otherwise
            // UiMotion.Stop the exit and drop CommitMoveAfterExit's onComplete.
            if (!layoutRoot.IsHitTestVisible)
                return true;
        }

        return false;
    }

    public static void NotifyItemContainerRealized(FrameworkElement element)
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
        {
            if (!DashboardMotionCardCatalog.TryResolveCard(element, scope, out _, out _, out var itemId))
                continue;

            if (!ActiveByScope.TryGetValue(scope, out var engine))
                continue;

            engine.OnItemContainerRealized(itemId);
            return;
        }
    }

    /// <summary>
    /// Same as <see cref="NotifyItemContainerRealized(FrameworkElement)"/>, but skips straight to
    /// <paramref name="knownScope"/> instead of probing every <see cref="DashboardMotionScope"/> —
    /// callers that host a single fixed scope (e.g. one dashboard grid per section) already know it.
    /// Falls back to the exhaustive probe if the scope is unresolved.
    /// </summary>
    public static void NotifyItemContainerRealized(FrameworkElement element, DashboardMotionScope? knownScope)
    {
        if (knownScope is not { } scope)
        {
            NotifyItemContainerRealized(element);
            return;
        }

        if (!DashboardMotionCardCatalog.TryResolveCard(element, scope, out _, out _, out var itemId))
            return;

        if (!ActiveByScope.TryGetValue(scope, out var engine))
            return;

        engine.OnItemContainerRealized(itemId);
    }

    public static bool TryPrepareEnterState(FrameworkElement element)
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
        {
            if (!DashboardMotionCardCatalog.TryResolveCard(element, scope, out var layoutRoot, out var motionSurface, out var itemId))
                continue;

            if (!ActiveByScope.TryGetValue(scope, out var engine)
                || !engine.IsTrackedEnter(itemId)
                || !engine.IsAuthorizedEnterLayoutRoot(layoutRoot, itemId))
                continue;

            if (motionSurface.Opacity is > 0.02 and < 0.98)
                return true;

            engine.PrepareEnterHoldSlot(layoutRoot, motionSurface);
            return true;
        }

        return false;
    }

    private bool IsTrackedEnter(Guid itemId)
        => _enteringItemIds.Contains(itemId);

    private void PrepareEnterHoldSlot(FrameworkElement layoutRoot, FrameworkElement motionSurface)
    {
        UiMotion.Stop(layoutRoot);
        UiMotion.Stop(motionSurface);
        ClearLayoutAnimationOverrides(layoutRoot);
        layoutRoot.Visibility = Visibility.Visible;
        layoutRoot.IsHitTestVisible = false;
        ResetLayoutReflowTransform(layoutRoot);
        SetMotionSurfaceEnterStart(motionSurface);
    }

    private static void SetMotionSurfaceEnterStart(FrameworkElement motionSurface)
    {
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        translate.X = 0;
        translate.Y = -UiMotion.CardEnterLiftY;
        scale.ScaleX = UiMotion.EnterScale;
        scale.ScaleY = UiMotion.EnterScale;
        motionSurface.Opacity = 0;
    }

    private static void ClearLayoutAnimationOverrides(FrameworkElement layoutRoot)
    {
        if (layoutRoot.ReadLocalValue(FrameworkElement.HeightProperty) != DependencyProperty.UnsetValue)
            layoutRoot.ClearValue(FrameworkElement.HeightProperty);

        if (layoutRoot.ReadLocalValue(FrameworkElement.MaxHeightProperty) != DependencyProperty.UnsetValue)
            layoutRoot.ClearValue(FrameworkElement.MaxHeightProperty);

        if (layoutRoot.MinHeight == 0
            && layoutRoot.ReadLocalValue(FrameworkElement.MinHeightProperty) != DependencyProperty.UnsetValue)
            layoutRoot.ClearValue(FrameworkElement.MinHeightProperty);
    }

    public void ForceLayout()
    {
        _dashboardRoot?.UpdateLayout();
        _ungroupedSection?.UpdateLayout();
        _ungroupedItemsControl?.UpdateLayout();
    }

    public void CapturePositions(bool ungroupedOnly = false, bool freezeViewport = true)
    {
        CancelActiveReflowTransforms();

        _positionSnapshots.Clear();
        _panelByItemId.Clear();
        _layoutRootByItemId.Clear();

        var searchRoot = ungroupedOnly ? _ungroupedSection : _dashboardRoot;
        if (searchRoot is null)
            return;

        StabilizeMotionLayout(searchRoot);
        if (freezeViewport)
            FreezeVirtualizationViewports();
        CaptureItemIndices();

        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(searchRoot, _scope))
        {
            if (!DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out var itemId))
                continue;

            if (FindItemsPanel(layoutRoot) is not { } panel)
                continue;

            var reflowTarget = ResolveReflowTarget(layoutRoot);
            _layoutRootByItemId[itemId] = layoutRoot;
            _reflowTargetByItemId[itemId] = reflowTarget;
            _positionSnapshots[itemId] = reflowTarget.TransformToAncestor(panel).Transform(new Point(0, 0));
            _panelByItemId[itemId] = panel;
        }
    }

    public void BeginBatchMutation()
    {
        _batchMutationDepth++;
        if (_batchMutationInFlight)
        {
            ScrollPerfDiag.Log($"batchMutationBegin\tscope={_scope}\tdepth={_batchMutationDepth}\tcoalesced=true\treflowRunning={_batchMutationReflowRunning}");
            return;
        }

        _batchMutationInFlight = true;
        ScrollPerfDiag.Log($"batchMutationBegin\tscope={_scope}\tdepth={_batchMutationDepth}\tcoalesced=false\treflowRunning={_batchMutationReflowRunning}");
        if (!IsLayoutAnimationActive)
            BeginLayoutAnimation();

        CapturePositions(ungroupedOnly: false);
    }

    public void RegisterBatchItemAdd(Guid itemId, bool grouped = false)
    {
        _ = grouped;
        if (!_batchMutationInFlight)
            BeginBatchMutation();

        _enteringItemIds.Add(itemId);
    }

    public void CompleteBatchAdd(IReadOnlyList<Guid> enterItemIds, Action? afterMotion = null)
    {
        if (_batchMutationDepth > 0)
            _batchMutationDepth--;

        if (afterMotion is not null)
            _batchMutationAfterMotion.Add(afterMotion);

        foreach (var itemId in enterItemIds)
            _scheduledEnterIds.Add(itemId);

        ScrollPerfDiag.Log($"batchMutationComplete\tscope={_scope}\tdepth={_batchMutationDepth}\tinFlight={_batchMutationInFlight}\treflowRunning={_batchMutationReflowRunning}\tpendingEnters={_scheduledEnterIds.Count}");

        if (_batchMutationDepth > 0)
            return;

        // Deliberately NOT also gating on _batchMutationReflowRunning here: that flag is only
        // cleared inside FinishReflow's terminal branch, so if a reflow pass ever fails to reach
        // it (see the _pendingEnterCompletions comment - a dropped enter-animation completion is
        // the known way that happens), this flag would get stuck true and permanently prevent
        // every future duplicate/add on this scope from ever scheduling a reflow again.
        // ScheduleReflowAfterLayout()'s own _reflowScheduled guard already prevents redundant
        // concurrent scheduling, so this doesn't need a second gate.
        ScheduleReflowAfterLayout();
    }

    public void PreparePendingEnter(Guid itemId, bool grouped = false)
    {
        _ = grouped;
        RegisterItemAdded(itemId);
        _enteringItemIds.Add(itemId);
    }

    public void ScheduleReflowAfterLayout(Guid? fadeInItemId = null, bool instantEnter = false, Action? afterMotion = null)
    {
        if (fadeInItemId.HasValue)
        {
            _scheduledEnterIds.Add(fadeInItemId.Value);
            if (instantEnter)
                _instantEnterIds.Add(fadeInItemId.Value);
        }

        if (afterMotion is not null)
            _onReflowComplete = afterMotion;

        if (_reflowScheduled)
            return;

        _reflowScheduled = true;
        RunReflowDispatcherWork();
    }

    private void RunReflowDispatcherWork()
    {
        var waitForItemId = _scheduledEnterIds.Count == 1 ? _scheduledEnterIds.First() : (Guid?)null;

        ScheduleAfterLayoutReady(() =>
        {
            var enterIds = _scheduledEnterIds.ToList();
            _reflowScheduled = false;
            _scheduledEnterIds.Clear();
            _batchMutationReflowRunning = _batchMutationInFlight;
            ScrollPerfDiag.Log($"reflowStart\tscope={_scope}\tbatchInFlight={_batchMutationInFlight}\tbatchDepth={_batchMutationDepth}\tenterCount={enterIds.Count}");
            AnimateReflow(enterIds);

            if (_scheduledEnterIds.Count > 0 && !_batchMutationReflowRunning)
                ScheduleReflowAfterLayout();
        }, waitForItemId);
    }

    private void ScheduleReflowWork(Action work)
        => _dispatcher.BeginInvoke(work, DispatcherPriority.Render);

    public void RequestDelete(Guid itemId, Action commitDelete, Action? afterMotion = null)
    {
        ResetDeleteBatchState();
        if (!IsLayoutAnimationActive)
            BeginLayoutAnimation();

        CapturePositions(ungroupedOnly: false);

        var layoutRoot = FindLayoutRootById(itemId);
        if (layoutRoot is null || UiMotion.PrefersReducedMotion)
        {
            CommitDeleteBatch(commitDelete, afterMotion);
            return;
        }

        var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope);
        ArmDeleteBatchSafety(commitDelete, afterMotion);
        PlayCardExit(
            layoutRoot,
            motionSurface,
            () => CommitDeleteBatch(commitDelete, afterMotion));
    }

    public void RequestDeleteBatch(IReadOnlyList<Guid> itemIds, Action commitDelete, Action? afterMotion = null)
    {
        if (itemIds.Count == 0)
            return;

        ResetDeleteBatchState();
        if (!IsLayoutAnimationActive)
            BeginLayoutAnimation();

        CapturePositions(ungroupedOnly: false);
        ArmDeleteBatchSafety(commitDelete, afterMotion);

        if (UiMotion.PrefersReducedMotion)
        {
            CommitDeleteBatch(commitDelete, afterMotion);
            return;
        }

        var scrollViewer = _scrollViewer ?? DashboardMotionViewport.FindScrollViewer(_dashboardRoot);
        var cards = new List<(FrameworkElement LayoutRoot, FrameworkElement MotionSurface)>();
        foreach (var itemId in itemIds)
        {
            var layoutRoot = FindLayoutRootById(itemId);
            if (layoutRoot is null)
                continue;

            if (!DashboardMotionViewport.IntersectsViewport(layoutRoot, scrollViewer))
                continue;

            cards.Add((layoutRoot, DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope)));
        }

        if (cards.Count > UiMotion.MaxBulkAnimatedExits)
            cards.RemoveRange(UiMotion.MaxBulkAnimatedExits, cards.Count - UiMotion.MaxBulkAnimatedExits);

        if (cards.Count == 0)
        {
            CommitDeleteBatch(commitDelete, afterMotion);
            return;
        }

        var remaining = cards.Count;
        void OnExitComplete()
        {
            remaining--;
            if (remaining > 0)
                return;

            CommitDeleteBatch(commitDelete, afterMotion);
        }

        foreach (var (layoutRoot, motionSurface) in cards)
            PlayCardExit(layoutRoot, motionSurface, OnExitComplete);
    }

    public void RequestItemMove(Guid itemId, Action commitMove, Action? afterMotion = null)
        => RequestItemMoveBatch(new[] { itemId }, commitMove, afterMotion);

    public void RequestItemMoveBatch(IReadOnlyList<Guid> itemIds, Action commitMove, Action? afterMotion = null)
    {
        if (itemIds.Count == 0)
            return;

        var ids = itemIds.ToArray();
        ResetMoveBatchState();
        if (!IsLayoutAnimationActive)
            BeginLayoutAnimation();

        // Drop handlers run inside DoDragDrop while the drag ghost still dims the source.
        // Defer exit until after RestoreDragSource / ghost teardown on the dispatcher.
        _dispatcher.BeginInvoke(
            () => ExecuteMoveExitBatch(ids, commitMove, afterMotion),
            DispatcherPriority.Input);
    }

    private void ExecuteMoveExitBatch(
        Guid[] ids,
        Action commitMove,
        Action? afterMotion)
    {
        if (_moveBatchCommitted)
            return;

        RealizeItemsForIds(ids);
        CapturePositions(ungroupedOnly: false, freezeViewport: true);
        ArmMoveBatchSafety(ids, commitMove, afterMotion);

        if (UiMotion.PrefersReducedMotion)
        {
            CommitMoveAfterExit(ids, commitMove, afterMotion);
            return;
        }

        var exitTargets = new List<(FrameworkElement LayoutRoot, FrameworkElement MotionSurface)>(ids.Length);
        var resolvedIds = new HashSet<Guid>();
        foreach (var itemId in ids)
        {
            if (!resolvedIds.Add(itemId))
                continue;

            if (!_layoutRootByItemId.TryGetValue(itemId, out var layoutRoot))
                layoutRoot = FindLayoutRootById(itemId);

            if (layoutRoot is null)
                continue;

            // Do not cull by viewport — drag-time opacity/hit-test quirks falsely skip on-screen cards.
            exitTargets.Add((layoutRoot, DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope)));
        }

        if (exitTargets.Count == 0)
        {
            CommitMoveAfterExit(ids, commitMove, afterMotion);
            return;
        }

        var remaining = exitTargets.Count;
        void OnExitComplete()
        {
            remaining--;
            if (remaining > 0)
                return;

            CommitMoveAfterExit(ids, commitMove, afterMotion);
        }

        foreach (var (layoutRoot, motionSurface) in exitTargets)
            PlayCardExit(layoutRoot, motionSurface, OnExitComplete);
    }

    private void ResetMoveBatchState()
    {
        _moveBatchCommitted = false;
        _moveBatchSafetyTimer?.Stop();
        _moveBatchSafetyTimer = null;
    }

    private void ArmMoveBatchSafety(IReadOnlyList<Guid> enterItemIds, Action commitMove, Action? afterMotion = null)
    {
        _moveBatchSafetyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UiMotion.NoteDeleteMs + 120)
        };
        _moveBatchSafetyTimer.Tick += (_, _) => CommitMoveAfterExit(enterItemIds, commitMove, afterMotion);
        _moveBatchSafetyTimer.Start();
    }

    private void CommitMoveAfterExit(IReadOnlyList<Guid> enterItemIds, Action commitMove, Action? afterMotion = null)
    {
        if (_moveBatchCommitted)
            return;

        _moveBatchCommitted = true;
        _moveBatchSafetyTimer?.Stop();
        _moveBatchSafetyTimer = null;

        _relocatingItemIds.Clear();
        foreach (var itemId in enterItemIds)
        {
            _relocatingItemIds.Add(itemId);
            PreparePendingEnter(itemId);
        }

        commitMove();
        CompleteBatchAdd(enterItemIds, afterMotion);
    }

    private void RealizeItemsForIds(IReadOnlyList<Guid> itemIds)
    {
        if (itemIds.Count == 0)
            return;

        foreach (var grid in GetReflowGrids())
            grid.UpdateLayout();

        foreach (var itemId in itemIds)
        {
            if (FindLayoutRootById(itemId) is not null)
                continue;

            foreach (var grid in GetReflowGrids())
            {
                if (!BuildCurrentIndexMap(grid).ContainsKey(itemId))
                    continue;

                if (TryFindVirtualizationHost(grid) is { } host)
                    host.RequestViewportRefresh();

                break;
            }
        }

        ForceLayout();
    }

    private void ResetDeleteBatchState()
    {
        _deleteBatchCommitted = false;
        _deleteBatchSafetyTimer?.Stop();
        _deleteBatchSafetyTimer = null;
    }

    private void ArmDeleteBatchSafety(Action commitDelete, Action? afterMotion = null)
    {
        _deleteBatchSafetyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UiMotion.NoteDeleteMs + UiMotion.ReflowMs + UiMotion.ReflowStaggerCapMs + 80)
        };
        _deleteBatchSafetyTimer.Tick += (_, _) => CommitDeleteBatch(commitDelete, afterMotion);
        _deleteBatchSafetyTimer.Start();
    }

    /// <summary>
    /// Bounds how long an add/duplicate reflow pass can stay waiting on card-enter completions.
    /// Mirrors ArmDeleteBatchSafety. This is the last line of defense: even if a card's enter
    /// animation gets cancelled without notice (see the comment on _pendingEnterCompletions) and
    /// SweepOrphanedEnterCompletions doesn't get a chance to run again, this guarantees the pass
    /// - and therefore UnfreezeVirtualizationViewports - completes within a bounded time instead
    /// of leaving every host in the scope frozen for the rest of the process's life.
    /// </summary>
    private void ArmReflowSafety()
    {
        _reflowSafetyTimer?.Stop();
        _reflowSafetyTimer = new DispatcherTimer(DispatcherPriority.Loaded)
        {
            Interval = TimeSpan.FromMilliseconds(
                UiMotion.NoteAddEnterMs + UiMotion.NoteAddReflowMs + UiMotion.ReflowStaggerCapMs + 300)
        };
        _reflowSafetyTimer.Tick += (_, _) => ForceFinishStuckEnters();
        _reflowSafetyTimer.Start();
    }

    private void MaybeDisarmReflowSafety()
    {
        // Keep the timer alive while a pass still needs the reflow-side escape hatch
        // (_forceFinishPass), even if every enter completion has already retired. Disarming
        // here when only reflowComplete is still false is exactly the overlapping-duplicate
        // failure mode that hatch exists for: UiMotion drops the sibling-reflow callback, every
        // enter retires normally, this method used to kill the timer, and every host in the
        // scope stayed frozen until the 3s viewport-freeze timeout - visible as large blank
        // gaps in groups/ungrouped until something finally scrolled/remeasured.
        if (_pendingEnterCompletions.Count > 0 || _forceFinishPass is not null)
            return;

        _reflowSafetyTimer?.Stop();
        _reflowSafetyTimer = null;
    }

    private void ForceFinishStuckEnters()
    {
        _reflowSafetyTimer?.Stop();
        _reflowSafetyTimer = null;

        if (_pendingEnterCompletions.Count > 0)
        {
            ScrollPerfDiag.Log($"reflowSafetyForceFinish\tscope={_scope}\tstuckCount={_pendingEnterCompletions.Count}\tentering={_enteringItemIds.Count}\tplaying={_playingEnterIds.Count}");
            foreach (var (itemId, retire) in _pendingEnterCompletions.ToList())
            {
                _ = itemId;
                retire();
            }
        }

        // The retire loop above only ever forces enterComplete for entries still in
        // _pendingEnterCompletions - it does nothing if that dict is already empty (every enter
        // retired normally) but reflowComplete is the side still stuck (see the comment on
        // _forceFinishPass). Always give the current pass, if any, a chance to force through
        // regardless of which side the retire loop did or didn't touch.
        var forceFinishPass = _forceFinishPass;
        _forceFinishPass = null;
        if (forceFinishPass is not null)
        {
            ScrollPerfDiag.Log($"reflowSafetyForceFinishPass\tscope={_scope}\tentering={_enteringItemIds.Count}");
            forceFinishPass();
        }
    }

    private void CommitDeleteBatch(Action commitDelete, Action? afterMotion = null)
    {
        if (_deleteBatchCommitted)
            return;

        _deleteBatchCommitted = true;
        _deleteBatchSafetyTimer?.Stop();
        _deleteBatchSafetyTimer = null;

        if (!IsLayoutAnimationActive)
            BeginLayoutAnimation();

        _onReflowComplete = afterMotion;
        commitDelete();
        ScheduleReflowWork(() => AnimateReflow(Array.Empty<Guid>()));
    }

    private void RegisterItemAdded(Guid itemId)
    {
        var now = Environment.TickCount64;
        if (now - _lastAddTimestampMs < EnterBurstWindowMs)
            _rapidAddCount++;
        else
            _rapidAddCount = 1;

        _lastAddTimestampMs = now;
    }

    private void ScheduleAfterLayoutReady(Action work, Guid? waitForItemId = null, int attempt = 0)
    {
        var priority = attempt switch
        {
            0 when waitForItemId.HasValue => DispatcherPriority.Render,
            0 => DispatcherPriority.Loaded,
            1 => DispatcherPriority.Loaded,
            _ => DispatcherPriority.Render
        };

        _dispatcher.BeginInvoke(() =>
        {
            if (_enteringItemIds.Count > 0)
                PrepareAddMotionLayout(waitForItemId);

            if (waitForItemId.HasValue
                && FindEnterLayoutRoot(waitForItemId.Value) is not { IsLoaded: true })
            {
                if (attempt < MaxLayoutWaitAttempts)
                    ScheduleAfterLayoutReady(work, waitForItemId, attempt + 1);
                else
                {
                    if (waitForItemId.HasValue)
                        AbandonUnrealizedEnter(waitForItemId.Value);

                    work();
                }

                return;
            }

            work();
        }, priority);
    }

    private void OnItemContainerRealized(Guid itemId)
    {
        if (!_deferredEnterIds.Contains(itemId) && !_instantEnterIds.Contains(itemId))
            return;

        TryPlayEnterForItem(itemId);
    }

    private static int MaxEnterPlayMs => UiMotion.NoteAddEnterMs + UiMotion.ReflowStaggerCapMs + 500;

    private bool IsStalePlayingEnter(Guid itemId)
        => _playingEnterStartedAtMs.TryGetValue(itemId, out var startedAt)
           && Environment.TickCount64 - startedAt > MaxEnterPlayMs;

    private void ClearPlayingEnter(Guid itemId)
    {
        _playingEnterIds.Remove(itemId);
        _playingEnterStartedAtMs.Remove(itemId);
    }

    private void TryPlayEnterForItem(Guid itemId, Action? onComplete = null, int startDelayMs = 0)
    {
        if (!_enteringItemIds.Contains(itemId))
        {
            onComplete?.Invoke();
            return;
        }

        if (_playingEnterIds.Contains(itemId))
        {
            if (!IsStalePlayingEnter(itemId))
            {
                onComplete?.Invoke();
                return;
            }

            // The previous "playing" attempt never called back to remove this id - almost
            // certainly a replay started from OnItemContainerRealized/FinishReflow's deferred
            // retry (untracked, no watchdog) whose clock got Stop()'d mid-animation. Treat it as
            // abandoned and let this call start a real replay instead of skipping forever.
            ScrollPerfDiag.Log($"enterPlayStaleReplay\tscope={_scope}\titemId={itemId}");
            ClearPlayingEnter(itemId);
        }

        var entering = FindEnterLayoutRoot(itemId);
        if (entering is null)
        {
            if (!_instantEnterIds.Contains(itemId))
                _deferredEnterIds.Add(itemId);

            onComplete?.Invoke();
            return;
        }

        _deferredEnterIds.Remove(itemId);
        _playingEnterIds.Add(itemId);
        _playingEnterStartedAtMs[itemId] = Environment.TickCount64;

        var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(entering, _scope);
        var scrollViewer = _scrollViewer ?? DashboardMotionViewport.FindScrollViewer(_dashboardRoot);
        if (_instantEnterIds.Contains(itemId) || IsInBurstAddWindow())
        {
            CompleteEnteredCard(entering, motionSurface, itemId);
            _instantEnterIds.Remove(itemId);
            ClearPlayingEnter(itemId);
            onComplete?.Invoke();
            return;
        }

        PlayCardEnter(entering, motionSurface, itemId, () =>
        {
            _instantEnterIds.Remove(itemId);
            ClearPlayingEnter(itemId);
            onComplete?.Invoke();
        }, startDelayMs);
    }

    private void AnimateReflow(IReadOnlyList<Guid> enterItemIds)
    {
        if (_dashboardRoot is null)
        {
            FinishReflow();
            return;
        }

        if (UiMotion.PrefersReducedMotion)
        {
            FinalizePendingEntersWithoutMotion(enterItemIds);
            FinishReflow();
            return;
        }

        // This pass's item collection may differ from whatever an earlier pass last built
        // EnterItemLookup()/DisplayIndexLookup() from (items were just added/duplicated) -
        // start every pass with a fresh lookup instead of risking a stale one that resolves a
        // newly-entering item to nothing, or to a wrong/recycled container.
        InvalidatePassLookups();

        StabilizeMotionLayout(_dashboardRoot);

        var animatedEnterIds = ResolveAnimatedEnterIds(enterItemIds);
        var enteringIdSet = enterItemIds.Count > 0 ? enterItemIds.ToHashSet() : null;

        _layoutRootByItemId.Clear();
        _reflowTargetByItemId.Clear();
        RefreshLayoutRootReferences();
        ForceLayout();

        // A prior reflow pass (e.g. from an overlapping duplicate/add that started before this
        // one) may still have entries here whose card left the tree before its enter animation's
        // completion callback fired. Retire those now, using this pass's freshly-refreshed view
        // of the tree, so a dropped callback from an earlier pass can never block indefinitely.
        SweepOrphanedEnterCompletions();

        foreach (var grid in GetReflowGrids())
            grid.EnsureRealizedCardsInteractive();

        if (enteringIdSet is { Count: > 0 })
        {
            if (enterItemIds.Count > UiMotion.BulkAddLayoutHideThreshold)
                FinalizeInstantEnterCards(enterItemIds.Except(animatedEnterIds));

            ExpandEnteringCardsForReflow(animatedEnterIds);
        }

        if (enterItemIds.Count == 0)
        {
            AnimateSiblingReflow(enteringIdSet, FinishReflow);
            return;
        }

        var reflowComplete = false;
        var enterComplete = false;
        var passFinished = false;

        void TryCompleteAddMotion()
        {
            if (!reflowComplete || !enterComplete || passFinished)
                return;

            passFinished = true;
            FinishReflow();
        }

        AnimateSiblingReflow(enteringIdSet, () =>
        {
            reflowComplete = true;
            TryCompleteAddMotion();
        });

        var remainingEnters = new HashSet<Guid>(enterItemIds);
        void RetireEnter(Guid itemId)
        {
            if (!remainingEnters.Remove(itemId))
                return;

            _pendingEnterCompletions.Remove(itemId);
            MaybeDisarmReflowSafety();
            if (remainingEnters.Count > 0)
                return;

            enterComplete = true;
            TryCompleteAddMotion();
        }

        // Registered per-id so SweepOrphanedEnterCompletions (called from a LATER AnimateReflow
        // pass, or from the reflow safety-timer watchdog) can retire a specific stuck id and
        // correctly drive THIS pass's completion, even though remainingEnters/TryCompleteAddMotion
        // are local to this call and not otherwise reachable from outside it.
        foreach (var enterItemId in enterItemIds)
            _pendingEnterCompletions[enterItemId] = () => RetireEnter(enterItemId);

        ArmReflowSafety();

        // Escape hatch for a pass stuck on the REFLOW side, not just the enter side (see the
        // comment on the _forceFinishPass field): UiMotion.Run keys one clock per key, so a
        // closely-overlapping second pass on the same _reflowBatchKey can silently drop THIS
        // pass's sibling-reflow completion, leaving reflowComplete stuck false forever even
        // after every enter-completion above has retired normally. Routed through
        // TryCompleteAddMotion (not FinishReflow directly) so the passFinished guard prevents a
        // late-arriving genuine completion from calling FinishReflow a second time.
        _forceFinishPass = () =>
        {
            foreach (var itemId in enterItemIds)
                SnapEnterToFinal(itemId);

            reflowComplete = true;
            enterComplete = true;
            TryCompleteAddMotion();
        };

        var enterDelay = 0;
        foreach (var enterItemId in enterItemIds)
        {
            TryPlayEnterForItem(enterItemId, () => RetireEnter(enterItemId), enterDelay);
            if (animatedEnterIds.Contains(enterItemId))
                enterDelay = Math.Min(enterDelay + UiMotion.ReflowStaggerMs, UiMotion.ReflowStaggerCapMs);
        }
    }

    /// <summary>
    /// Retires any pending enter-completion whose card is no longer findable in the tree - the
    /// symptom of a dropped RenderTransitionClock completion (see the comment on
    /// _pendingEnterCompletions). Safe to call whenever the tree/layout is known-fresh.
    /// </summary>
    private void SweepOrphanedEnterCompletions()
    {
        if (_pendingEnterCompletions.Count == 0)
            return;

        foreach (var (itemId, retire) in _pendingEnterCompletions.ToList())
        {
            if (FindEnterLayoutRoot(itemId) is not null)
                continue;

            ScrollPerfDiag.Log($"enterSweepOrphaned\tscope={_scope}\titemId={itemId}");
            retire();
        }
    }

    private IReadOnlyList<Guid> ResolveAnimatedEnterIds(IReadOnlyList<Guid> enterItemIds)
    {
        var forcedInstant = _instantEnterIds.ToHashSet();
        _instantEnterIds.Clear();
        if (enterItemIds.Count == 0)
            return enterItemIds;

        if (enterItemIds.All(_relocatingItemIds.Contains))
            return enterItemIds;

        if (forcedInstant.Count > 0)
        {
            var deferredAnimated = new List<Guid>();
            foreach (var itemId in enterItemIds)
            {
                if (forcedInstant.Contains(itemId))
                    _instantEnterIds.Add(itemId);
                else
                    deferredAnimated.Add(itemId);
            }

            return deferredAnimated;
        }

        var scrollViewer = _scrollViewer ?? DashboardMotionViewport.FindScrollViewer(_dashboardRoot);
        var contentOffsetY = ResolveSectionRelativeViewportTop();
        var metrics = ResolvePrimaryGridMetrics();
        var animated = new List<Guid>(Math.Min(enterItemIds.Count, UiMotion.MaxBulkAnimatedEnters));

        foreach (var itemId in enterItemIds)
        {
            if (FindEnterLayoutRoot(itemId) is { } layoutRoot
                && DashboardMotionViewport.IntersectsViewport(layoutRoot, scrollViewer))
            {
                animated.Add(itemId);
                if (animated.Count >= UiMotion.MaxBulkAnimatedEnters)
                    break;
            }
        }

        if (animated.Count == 0 && metrics is not null)
        {
            foreach (var itemId in enterItemIds)
            {
                if (!TryResolveDisplayIndex(itemId, out var index))
                    continue;

                if (!DashboardMotionViewport.IsGridIndexLikelyVisible(index, metrics.Value, scrollViewer, contentOffsetY))
                    continue;

                animated.Add(itemId);
                if (animated.Count >= UiMotion.MaxBulkAnimatedEnters)
                    break;
            }
        }

        foreach (var itemId in enterItemIds)
        {
            if (!animated.Contains(itemId))
                _instantEnterIds.Add(itemId);
        }

        return animated;
    }

    private void FinalizeInstantEnterCards(IEnumerable<Guid> itemIds)
    {
        foreach (var itemId in itemIds)
        {
            if (FindEnterLayoutRoot(itemId) is not { } layoutRoot)
                continue;

            var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope);
            CompleteEnteredCard(layoutRoot, motionSurface, itemId);
            _instantEnterIds.Remove(itemId);
        }
    }

    private bool TryResolveDisplayIndex(Guid itemId, out int index)
        => DisplayIndexLookup().TryGetValue(itemId, out index);

    private DashboardMotionViewport.GridReflowMetrics? ResolvePrimaryGridMetrics()
    {
        foreach (var grid in GetReflowGrids())
            return ResolveGridReflowMetrics(grid);

        return null;
    }

    private void FinishReflow()
    {
        if (_batchMutationInFlight && _scheduledEnterIds.Count > 0)
        {
            _batchMutationReflowRunning = false;
            ScrollPerfDiag.Log($"reflowContinue\tscope={_scope}\tbatchDepth={_batchMutationDepth}\tpendingEnters={_scheduledEnterIds.Count}");
            ScheduleReflowAfterLayout();
            return;
        }

        ClearMotionCaches();
        _instantEnterIds.Clear();
        _relocatingItemIds.Clear();
        _batchMutationDepth = 0;
        _batchMutationInFlight = false;
        _batchMutationReflowRunning = false;
        _pendingEnterCompletions.Clear();
        _forceFinishPass = null;
        _reflowSafetyTimer?.Stop();
        _reflowSafetyTimer = null;
        EndAddMotionLayoutOnGrids();
        UnfreezeVirtualizationViewports();
        EndLayoutAnimation();

        // After a bulk duplicate/add the unfreeze path may have cleared per-host slice state
        // because item count grew during the freeze; also drop any sibling _sectionTop latched
        // against pre-growth geometry before OnCollectionLayoutChanged's viewport refresh runs.
        foreach (var host in EnumerateVirtualizationHosts(_dashboardRoot))
            host.InvalidateSectionGeometry();

        OnCollectionLayoutChanged?.Invoke();

        var afterMotion = _onReflowComplete;
        _onReflowComplete = null;
        afterMotion?.Invoke();

        var batchAfterMotion = _batchMutationAfterMotion.ToArray();
        _batchMutationAfterMotion.Clear();
        foreach (var callback in batchAfterMotion)
            callback();

        ScrollPerfDiag.Log($"reflowFinish\tscope={_scope}\tbatchCallbacks={batchAfterMotion.Length}");

        // Always scheduled, not gated on _deferredEnterIds.Count > 0: a card leaked into
        // _playingEnterIds by an interrupted untracked replay (see the comment on
        // _playingEnterStartedAtMs) is usually NOT in _deferredEnterIds - it was removed from
        // there the moment that replay started - so gating this on deferred-only would skip the
        // one sweep most likely to catch it. The deferred-replay loop below iterates an empty
        // list when there's nothing actually deferred, so this is safe to always run.
        var deferredIds = _deferredEnterIds.ToList();
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var itemId in deferredIds)
                TryPlayEnterForItem(itemId);

            FinalizeOrphanedEnteringCards();
        }, DispatcherPriority.Loaded);
    }

    private void FinalizeOrphanedEnteringCards()
    {
        foreach (var itemId in _enteringItemIds.ToList())
        {
            var layoutRoot = FindEnterLayoutRoot(itemId) ?? FindLayoutRootById(itemId);
            if (layoutRoot is null)
            {
                _enteringItemIds.Remove(itemId);
                _deferredEnterIds.Remove(itemId);
                ClearPlayingEnter(itemId);
                continue;
            }

            var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope);
            if (motionSurface.Opacity >= 0.95 && layoutRoot.IsHitTestVisible)
            {
                _enteringItemIds.Remove(itemId);
                _deferredEnterIds.Remove(itemId);
                ClearPlayingEnter(itemId);
                continue;
            }

            // A stale "playing" id can be stuck at ANY intermediate opacity/scale/position, not
            // just near-invisible - snap it to final state unconditionally rather than requiring
            // Opacity < 0.05 the way the non-stale branch below does.
            if (IsStalePlayingEnter(itemId))
            {
                ScrollPerfDiag.Log($"enterStuckSnap\tscope={_scope}\titemId={itemId}\topacity={motionSurface.Opacity:F2}\tsource=orphanSweep");
                ClearPlayingEnter(itemId);
                CompleteEnteredCard(layoutRoot, motionSurface, itemId);
                continue;
            }

            // Widened from a plain "Opacity < 0.05" check: a card whose animation got dropped
            // mid-flight (not just never-started) can be stuck at ANY intermediate opacity, or
            // fully opaque but never re-enabled for hit-testing (PlayCardEnter's completion sets
            // both together - see PrepareEnterHoldSlot/CompleteEnteredCard). Snap either.
            if (!_playingEnterIds.Contains(itemId) && (motionSurface.Opacity < 0.95 || !layoutRoot.IsHitTestVisible))
                CompleteEnteredCard(layoutRoot, motionSurface, itemId);
        }
    }

    /// <summary>
    /// Force-resolves one still-entering item to its final visible state, bypassing the normal
    /// animation entirely. Used by the reflow-safety watchdog's <see cref="_forceFinishPass"/>
    /// escape hatch, where waiting any longer for the real animation/reflow handshake to
    /// complete is no longer an option.
    /// </summary>
    private void SnapEnterToFinal(Guid itemId)
    {
        var layoutRoot = FindEnterLayoutRoot(itemId) ?? FindLayoutRootById(itemId);
        if (layoutRoot is null)
        {
            _enteringItemIds.Remove(itemId);
            _deferredEnterIds.Remove(itemId);
            ClearPlayingEnter(itemId);
            return;
        }

        ClearPlayingEnter(itemId);
        CompleteEnteredCard(layoutRoot, DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope), itemId);
    }

    private void AnimateSiblingReflow(HashSet<Guid>? enteringIdSet, Action? onComplete = null)
    {
        if (_dashboardRoot is null)
        {
            onComplete?.Invoke();
            return;
        }

        SyncReflowGrids();

        var scrollViewer = _scrollViewer ?? DashboardMotionViewport.FindScrollViewer(_dashboardRoot);
        var isAddReflow = enteringIdSet is { Count: > 0 };
        var reflowMs = isAddReflow ? UiMotion.NoteAddReflowMs : UiMotion.ReflowMs;
        var reflowEasing = UiMotion.OutCubic;
        var candidates = new List<(FrameworkElement LayoutRoot, double DeltaX, double DeltaY, int SortOrder)>();

        CancelActiveReflowTransforms();

        foreach (var grid in GetReflowGrids())
        {
            if (grid.ResolveItemsPanel() is not Panel panel)
                continue;

            var currentIndices = BuildCurrentIndexMap(grid);
            var gridMetrics = ResolveGridReflowMetrics(grid);

            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                if (FindLayoutRootInContainer(child, _scope) is not { } layoutRoot)
                    continue;

                if (!TryResolveContainerItemId(grid, child, out var itemId))
                    continue;

                if (enteringIdSet is not null && enteringIdSet.Contains(itemId))
                    continue;

                if (!TryComputeReflowDelta(
                        itemId,
                        layoutRoot,
                        currentIndices,
                        gridMetrics,
                        enteringIdSet,
                        isAddReflow,
                        out var deltaX,
                        out var deltaY))
                    continue;

                if (!DashboardMotionViewport.IntersectsViewport(layoutRoot, scrollViewer))
                    continue;

                var sortOrder = _indexSnapshots.TryGetValue(itemId, out var oldIndex) ? oldIndex : int.MaxValue;
                candidates.Add((layoutRoot, deltaX, deltaY, sortOrder));
            }
        }

        if (candidates.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        candidates.Sort(static (left, right) => left.SortOrder.CompareTo(right.SortOrder));

        var useStagger = candidates.Count <= UiMotion.ReflowStaggerMaxItems;
        var tracks = new List<RenderTransitionClock.Track>(candidates.Count * 2);
        var animatedRoots = new List<FrameworkElement>(candidates.Count);

        for (var index = 0; index < candidates.Count; index++)
        {
            var (layoutRoot, deltaX, deltaY, _) = candidates[index];
            var staggerMs = useStagger
                ? Math.Min(index * UiMotion.ReflowStaggerMs, UiMotion.ReflowStaggerCapMs)
                : 0;

            UiMotion.Stop(layoutRoot);
            var (_, translate) = UiMotion.EnsureScaleTranslateGroup(layoutRoot);
            translate.X = deltaX;
            translate.Y = deltaY;
            animatedRoots.Add(layoutRoot);
            _activeReflowTargets.Add(layoutRoot);

            tracks.Add(UiMotion.Track(reflowMs, reflowEasing, deltaX, 0, value => translate.X = value, startDelayMs: staggerMs));
            tracks.Add(UiMotion.Track(reflowMs, reflowEasing, deltaY, 0, value => translate.Y = value, startDelayMs: staggerMs));
        }

        foreach (var layoutRoot in animatedRoots)
            UiMotion.BeginMotionPrep(layoutRoot, suppressEffect: true);

        UiMotion.Run(
            _reflowBatchKey,
            tracks,
            () =>
            {
                foreach (var layoutRoot in animatedRoots)
                {
                    UiMotion.EndMotionPrep(layoutRoot);
                    ResetLayoutReflowTransform(layoutRoot);
                }

                _activeReflowTargets.Clear();
                onComplete?.Invoke();
            });
    }

    private static FrameworkElement? FindLayoutRootInContainer(DependencyObject container, DashboardMotionScope scope)
    {
        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(container, scope))
            return layoutRoot;

        return null;
    }

    private void RefreshLayoutRootReferences(bool ungroupedOnly = false)
    {
        var searchRoot = ungroupedOnly ? _ungroupedSection : _dashboardRoot;
        if (searchRoot is null || _positionSnapshots.Count == 0)
            return;

        var trackedIds = _positionSnapshots.Keys.ToHashSet();

        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(searchRoot, _scope))
        {
            if (!DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out var itemId))
                continue;

            if (!trackedIds.Contains(itemId) || _enteringItemIds.Contains(itemId))
                continue;

            if (FindItemsPanel(layoutRoot) is not { } panel)
                continue;

            _layoutRootByItemId[itemId] = layoutRoot;
            _reflowTargetByItemId[itemId] = ResolveReflowTarget(layoutRoot);
            _panelByItemId[itemId] = panel;
        }
    }

    private void ExpandEnteringCardsForReflow(IReadOnlyList<Guid> enterItemIds)
    {
        foreach (var enterItemId in enterItemIds)
        {
            if (FindEnterLayoutRoot(enterItemId) is not { } layoutRoot)
                continue;

            var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope);
            PrepareEnterHoldSlot(layoutRoot, motionSurface);
        }

        _dashboardRoot?.UpdateLayout();
    }

    private void FinalizePendingEntersWithoutMotion(IReadOnlyList<Guid> enterItemIds)
    {
        foreach (var enterItemId in enterItemIds)
        {
            var entering = FindEnterLayoutRoot(enterItemId);
            if (entering is null)
            {
                _enteringItemIds.Remove(enterItemId);
                continue;
            }

            var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(entering, _scope);
            CompleteEnteredCard(entering, motionSurface, enterItemId);
        }
    }

    private void SyncReflowGrids()
    {
        foreach (var grid in GetReflowGrids())
            grid.SyncLayoutForMotion();
    }

    private IEnumerable<DashboardVirtualizedCardGrid> GetReflowGrids()
    {
        if (_ungroupedItemsControl is DashboardVirtualizedCardGrid primary)
            yield return primary;

        foreach (var grid in EnumerateCardGrids(_dashboardRoot))
        {
            if (!ReferenceEquals(grid, _ungroupedItemsControl))
                yield return grid;
        }
    }

    private Dictionary<Guid, int> BuildCurrentIndexMap(DashboardVirtualizedCardGrid grid)
    {
        var map = new Dictionary<Guid, int>(_indexSnapshots.Count);
        var displayIndex = 0;
        foreach (var item in EnumerateDisplayItems(grid))
        {
            if (DashboardMotionCardCatalog.TryGetItemIdFromGridItem(item, _scope, out var itemId))
                map[itemId] = displayIndex;

            displayIndex++;
        }

        return map;
    }

    private void PrepareAddMotionLayout(Guid? waitForItemId = null)
    {
        IReadOnlyList<Guid> enterItemIds = waitForItemId.HasValue
            ? new[] { waitForItemId.Value }
            : _enteringItemIds.ToList();

        if (enterItemIds.Count == 0)
            return;

        _layoutRootByItemId.Clear();
        _reflowTargetByItemId.Clear();

        var scrollViewer = _scrollViewer ?? DashboardMotionViewport.FindScrollViewer(_dashboardRoot);
        var affectedHosts = new HashSet<DashboardVirtualizationHost>();

        foreach (var grid in GetReflowGrids())
        {
            var indexMap = BuildCurrentIndexMap(grid);
            var entersInGrid = enterItemIds.Where(indexMap.ContainsKey).ToList();
            if (entersInGrid.Count == 0)
                continue;

            if (TryFindVirtualizationHost(grid) is { } host)
                affectedHosts.Add(host);

            var sectionViewportTop = TryFindVirtualizationHost(grid)?.ResolveSectionRelativeViewportTop()
                ?? ResolveSectionRelativeViewportTop(grid);
            var metrics = ResolveGridReflowMetrics(grid);

            var needsMotionLayout = false;
            foreach (var itemId in entersInGrid)
            {
                if (scrollViewer is null
                    || DashboardMotionViewport.IsGridIndexLikelyVisible(
                        indexMap[itemId],
                        metrics,
                        scrollViewer,
                        sectionViewportTop))
                {
                    needsMotionLayout = true;
                    continue;
                }

                _instantEnterIds.Add(itemId);
            }

            if (needsMotionLayout)
                grid.BeginAddMotionLayout();
        }

        foreach (var host in affectedHosts)
            host.SyncLayoutForAddMotion();

        SyncReflowGrids();
        ForceLayout();
        FreezeVirtualizationViewports();
    }

    private void EndAddMotionLayoutOnGrids()
    {
        foreach (var grid in GetReflowGrids())
            grid.EndAddMotionLayout();
    }

    private double ResolveSectionRelativeViewportTop(DashboardVirtualizedCardGrid? grid = null)
    {
        if (grid is not null && TryFindVirtualizationHost(grid) is { } specificHost)
            return specificHost.ResolveSectionRelativeViewportTop();

        foreach (var candidate in GetReflowGrids())
        {
            if (TryFindVirtualizationHost(candidate) is { } host)
                return host.ResolveSectionRelativeViewportTop();
        }

        return _scrollViewer?.VerticalOffset ?? 0;
    }

    private void AbandonUnrealizedEnter(Guid itemId)
    {
        _enteringItemIds.Remove(itemId);
        _deferredEnterIds.Remove(itemId);
        ClearPlayingEnter(itemId);
        _instantEnterIds.Remove(itemId);
        _scheduledEnterIds.Remove(itemId);

        if (_pendingEnterCompletions.Remove(itemId, out var retire))
            retire();
    }

    private static DashboardVirtualizationHost? TryFindVirtualizationHost(DashboardVirtualizedCardGrid grid)
    {
        var ancestor = LogicalTreeHelper.GetParent(grid) ?? VisualTreeHelper.GetParent(grid);
        while (ancestor is not null)
        {
            if (ancestor is DashboardVirtualizationHost host)
                return host;

            ancestor = LogicalTreeHelper.GetParent(ancestor) ?? VisualTreeHelper.GetParent(ancestor);
        }

        return null;
    }

    private bool TryComputeReflowDelta(
        Guid itemId,
        FrameworkElement layoutRoot,
        Dictionary<Guid, int> currentIndices,
        DashboardMotionViewport.GridReflowMetrics metrics,
        HashSet<Guid>? enteringIdSet,
        bool isAddReflow,
        out double deltaX,
        out double deltaY)
    {
        if (!isAddReflow
            && TryComputePositionReflowDelta(itemId, layoutRoot, out deltaX, out deltaY))
            return true;

        if (!isAddReflow)
            return TryComputeIndexReflowDelta(itemId, currentIndices, metrics, out deltaX, out deltaY);

        if (enteringIdSet is not { Count: > 0 })
        {
            deltaX = 0;
            deltaY = 0;
            return false;
        }

        return TryComputeAddReflowDelta(itemId, currentIndices, enteringIdSet, metrics, out deltaX, out deltaY);
    }

    /// <summary>
    /// Sort-agnostic add reflow from insert position only.
    /// At/after insert: snapshot index shift, else synthetic i → i+1 (head/middle).
    /// Before insert on tail append: synthetic i+1 → i (oldest / last-modified asc).
    /// </summary>
    private bool TryComputeAddReflowDelta(
        Guid itemId,
        Dictionary<Guid, int> currentIndices,
        HashSet<Guid> enteringIdSet,
        DashboardMotionViewport.GridReflowMetrics metrics,
        out double deltaX,
        out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (!_indexSnapshots.ContainsKey(itemId)
            || !currentIndices.TryGetValue(itemId, out var siblingIndex)
            || !TryResolveEnterIndex(enteringIdSet, currentIndices, out var enterIndex))
            return false;

        var isTailInsert = enterIndex == currentIndices.Values.Max();

        if (siblingIndex >= enterIndex)
        {
            if (TryComputeIndexReflowDelta(itemId, currentIndices, metrics, out deltaX, out deltaY))
                return true;

            return TryComputeIndexReflowDelta(siblingIndex, siblingIndex + 1, metrics, out deltaX, out deltaY);
        }

        if (!isTailInsert)
            return false;

        return TryComputeIndexReflowDelta(siblingIndex + 1, siblingIndex, metrics, out deltaX, out deltaY);
    }

    private static bool TryResolveEnterIndex(
        HashSet<Guid> enteringIdSet,
        Dictionary<Guid, int> currentIndices,
        out int enterIndex)
    {
        enterIndex = -1;
        foreach (var enterId in enteringIdSet)
        {
            if (!currentIndices.TryGetValue(enterId, out var index))
                continue;

            enterIndex = Math.Max(enterIndex, index);
        }

        return enterIndex >= 0;
    }

    private bool TryComputePositionReflowDelta(
        Guid itemId,
        FrameworkElement layoutRoot,
        out double deltaX,
        out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (!_positionSnapshots.TryGetValue(itemId, out var oldPosition))
            return false;

        if (!TryResolveReflowTarget(itemId, layoutRoot, out var reflowTarget))
            return false;

        var panel = _panelByItemId.TryGetValue(itemId, out var cachedPanel) && cachedPanel.IsLoaded
            ? cachedPanel
            : FindItemsPanel(layoutRoot);

        if (panel is null)
            return false;

        try
        {
            var newPosition = reflowTarget.TransformToAncestor(panel).Transform(new Point(0, 0));
            deltaX = oldPosition.X - newPosition.X;
            deltaY = oldPosition.Y - newPosition.Y;
            return Math.Abs(deltaX) > 0.5 || Math.Abs(deltaY) > 0.5;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// End-insert adds keep sibling indices unchanged, so index/position FLIP can miss visible cards.
    /// Mirror head-insert reflow by sliding each sibling from its successor's slot (oldest-created / append).
    /// </summary>
    private bool TryComputeEndInsertCascadeReflowDelta(
        Guid itemId,
        FrameworkElement layoutRoot,
        Dictionary<Guid, int> currentIndices,
        HashSet<Guid> enteringIdSet,
        out double deltaX,
        out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (!currentIndices.TryGetValue(itemId, out var siblingIndex))
            return false;

        var enterIndex = -1;
        foreach (var enterId in enteringIdSet)
        {
            if (!currentIndices.TryGetValue(enterId, out var index))
                continue;

            if (index > enterIndex)
                enterIndex = index;
        }

        if (enterIndex < 0 || siblingIndex >= enterIndex)
            return false;

        var maxIndex = -1;
        foreach (var index in currentIndices.Values)
            maxIndex = Math.Max(maxIndex, index);

        if (enterIndex != maxIndex)
            return false;

        if (_indexSnapshots.TryGetValue(itemId, out var oldIndex)
            && currentIndices.TryGetValue(itemId, out var newIndex)
            && oldIndex != newIndex)
            return false;

        var successorIndex = siblingIndex + 1;
        Guid successorId = Guid.Empty;
        foreach (var entry in currentIndices)
        {
            if (entry.Value != successorIndex)
                continue;

            successorId = entry.Key;
            break;
        }

        if (successorId == Guid.Empty)
            return false;

        var successorLayoutRoot = FindLayoutRootById(successorId)
            ?? (enteringIdSet.Contains(successorId) ? FindEnterLayoutRoot(successorId) : null);
        if (successorLayoutRoot is null)
            return false;

        if (FindItemsPanel(layoutRoot) is not { } panel)
            return false;

        try
        {
            var siblingTarget = ResolveReflowTarget(layoutRoot);
            var successorTarget = ResolveReflowTarget(successorLayoutRoot);
            var siblingPosition = siblingTarget.TransformToAncestor(panel).Transform(new Point(0, 0));
            var successorPosition = successorTarget.TransformToAncestor(panel).Transform(new Point(0, 0));
            deltaX = successorPosition.X - siblingPosition.X;
            deltaY = successorPosition.Y - siblingPosition.Y;
            return Math.Abs(deltaX) > 0.5 || Math.Abs(deltaY) > 0.5;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryComputeIndexReflowDelta(
        Guid itemId,
        Dictionary<Guid, int> currentIndices,
        DashboardMotionViewport.GridReflowMetrics metrics,
        out double deltaX,
        out double deltaY)
    {
        if (!_indexSnapshots.TryGetValue(itemId, out var oldIndex)
            || !currentIndices.TryGetValue(itemId, out var newIndex)
            || oldIndex == newIndex)
        {
            deltaX = 0;
            deltaY = 0;
            return false;
        }

        return TryComputeIndexReflowDelta(oldIndex, newIndex, metrics, out deltaX, out deltaY);
    }

    private static bool TryComputeIndexReflowDelta(
        int oldIndex,
        int newIndex,
        DashboardMotionViewport.GridReflowMetrics metrics,
        out double deltaX,
        out double deltaY)
    {
        deltaX = 0;
        deltaY = 0;

        if (oldIndex == newIndex)
            return false;

        if (metrics.IsListLayout)
        {
            deltaY = (oldIndex - newIndex) * 96;
            return Math.Abs(deltaY) > 0.5;
        }

        var oldRow = oldIndex / metrics.PerRow;
        var newRow = newIndex / metrics.PerRow;
        var oldCol = oldIndex % metrics.PerRow;
        var newCol = newIndex % metrics.PerRow;
        deltaX = (oldCol - newCol) * metrics.CellWidth;
        deltaY = (oldRow - newRow) * metrics.CellHeight;
        return Math.Abs(deltaX) > 0.5 || Math.Abs(deltaY) > 0.5;
    }

    private static DashboardMotionViewport.GridReflowMetrics ResolveGridReflowMetrics(DashboardVirtualizedCardGrid grid)
    {
        if (grid is DashboardVirtualizedNoteCardGrid { IsListLayout: true })
            return new DashboardMotionViewport.GridReflowMetrics(true, 0, 96, 1);

        var width = grid.ActualWidth;
        if (width <= 0 && grid.Parent is FrameworkElement parent)
            width = parent.ActualWidth;

        if (width <= 0)
            width = 960;

        var cellWidth = grid is DashboardVirtualizedNoteCardGrid ? 244 : grid.ItemCellSize.Width > 0 ? grid.ItemCellSize.Width : 244;
        var cellHeight = grid is DashboardVirtualizedNoteCardGrid ? 164 : grid.ItemCellSize.Height > 0 ? grid.ItemCellSize.Height : 164;
        var perRow = Math.Max(1, (int)Math.Floor(width / cellWidth));
        return new DashboardMotionViewport.GridReflowMetrics(false, cellWidth, cellHeight, perRow);
    }

    private void CaptureItemIndices()
    {
        _indexSnapshots.Clear();
        foreach (var grid in GetReflowGrids())
        {
            foreach (var entry in BuildCurrentIndexMap(grid))
                _indexSnapshots[entry.Key] = entry.Value;
        }
    }

    private static IEnumerable<object> EnumerateDisplayItems(DashboardVirtualizedCardGrid grid)
    {
        if (grid.ItemsSource is ICollectionView view)
        {
            foreach (var item in view)
                yield return item;

            yield break;
        }

        foreach (var item in grid.Items)
            yield return item;
    }

    private void FreezeVirtualizationViewports()
    {
        foreach (var host in EnumerateVirtualizationHosts(_dashboardRoot))
            host.FreezeMotionViewport();
    }

    private void UnfreezeVirtualizationViewports()
    {
        foreach (var host in EnumerateVirtualizationHosts(_dashboardRoot))
            host.UnfreezeMotionViewport();
    }

    private static IEnumerable<DashboardVirtualizedCardGrid> EnumerateCardGrids(DependencyObject? root)
    {
        if (root is null)
            yield break;

        if (root is DashboardVirtualizedCardGrid grid)
            yield return grid;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateCardGrids(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }

    private static IEnumerable<DashboardVirtualizationHost> EnumerateVirtualizationHosts(DependencyObject? root)
    {
        if (root is null)
            yield break;

        if (root is DashboardVirtualizationHost host)
            yield return host;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateVirtualizationHosts(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }

    private bool TryResolveReflowTarget(Guid itemId, FrameworkElement layoutRoot, out FrameworkElement reflowTarget)
    {
        if (_reflowTargetByItemId.TryGetValue(itemId, out var cached)
            && cached.IsLoaded
            && _layoutRootByItemId.TryGetValue(itemId, out var cachedLayoutRoot)
            && ReferenceEquals(cachedLayoutRoot, layoutRoot))
        {
            reflowTarget = cached;
            return true;
        }

        reflowTarget = ResolveReflowTarget(layoutRoot);
        _reflowTargetByItemId[itemId] = reflowTarget;
        return true;
    }

    private static FrameworkElement ResolveReflowTarget(FrameworkElement layoutRoot)
    {
        if (layoutRoot.Parent is FrameworkElement parent && parent is not Panel)
            return parent;

        return layoutRoot;
    }

    private static void ResetLayoutReflowTransform(FrameworkElement reflowTarget)
    {
        var (_, translate) = UiMotion.EnsureScaleTranslateGroup(reflowTarget);
        translate.X = 0;
        translate.Y = 0;
    }

    private static bool HasActiveReflowTransform(FrameworkElement reflowTarget)
    {
        if (reflowTarget.RenderTransform is not TransformGroup group)
            return false;

        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        return translate is not null
            && (Math.Abs(translate.X) > 0.5 || Math.Abs(translate.Y) > 0.5);
    }

    private void CancelActiveReflowTransforms()
    {
        if (_activeReflowTargets.Count == 0)
        {
            UiMotion.Stop(_reflowBatchKey);
            return;
        }

        var staleTargets = _activeReflowTargets.ToList();
        UiMotion.Stop(_reflowBatchKey);
        _activeReflowTargets.Clear();

        foreach (var layoutRoot in staleTargets)
        {
            UiMotion.Stop(layoutRoot);
            UiMotion.EndMotionPrep(layoutRoot);
            ResetLayoutReflowTransform(layoutRoot);
        }
    }

    private void StabilizeMotionLayout(DependencyObject searchRoot)
    {
        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(searchRoot, _scope))
        {
            if (_enteringItemIds.Count > 0
                && DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out var itemId)
                && _enteringItemIds.Contains(itemId))
                continue;

            if (!HasActiveReflowTransform(layoutRoot))
                continue;

            UiMotion.Stop(layoutRoot);
            ResetLayoutReflowTransform(layoutRoot);
        }
    }

    private bool IsInBurstAddWindow()
        => _rapidAddCount > MaxBurstAddsBeforeSkip
            && Environment.TickCount64 - _lastAddTimestampMs < EnterBurstWindowMs;

    private void ClearMotionCaches()
    {
        _positionSnapshots.Clear();
        _panelByItemId.Clear();
        _layoutRootByItemId.Clear();
        _reflowTargetByItemId.Clear();
        _indexSnapshots.Clear();
        _activeReflowTargets.Clear();
        InvalidatePassLookups();
    }

    /// <summary>
    /// Drops the per-pass entering-item lookup built by <see cref="EnterItemLookup"/>/
    /// <see cref="DisplayIndexLookup"/> - cheap, since both rebuild lazily on next use. Call
    /// wherever the tracked item set changed or the tree is already known-fresh, so a stale
    /// lookup never causes a newly-tracked item to resolve to nothing (or an old container).
    /// </summary>
    private void InvalidatePassLookups()
    {
        _enterItemLookup = null;
        _displayIndexLookup = null;
        _passGrids = null;
        _layoutSyncedThisPass.Clear();
    }

    private Dictionary<Guid, (DashboardVirtualizedCardGrid Grid, object Item)> EnterItemLookup()
    {
        if (_enterItemLookup is not null)
            return _enterItemLookup;

        var map = new Dictionary<Guid, (DashboardVirtualizedCardGrid, object)>();
        foreach (var grid in PassGrids())
        {
            foreach (var item in EnumerateDisplayItems(grid))
            {
                if (DashboardMotionCardCatalog.TryGetItemIdFromGridItem(item, _scope, out var id))
                    map.TryAdd(id, (grid, item)); // first grid wins, matching FindEnterLayoutRoot's old scan order
            }
        }

        return _enterItemLookup = map;
    }

    private Dictionary<Guid, int> DisplayIndexLookup()
    {
        if (_displayIndexLookup is not null)
            return _displayIndexLookup;

        var map = new Dictionary<Guid, int>();
        foreach (var grid in PassGrids())
        {
            foreach (var entry in BuildCurrentIndexMap(grid))
                map.TryAdd(entry.Key, entry.Value); // first grid wins, matching TryResolveDisplayIndex's old scan order
        }

        return _displayIndexLookup = map;
    }

    private List<DashboardVirtualizedCardGrid> PassGrids()
        => _passGrids ??= GetReflowGrids().ToList();

    private void EnsureGridLayoutSynced(DashboardVirtualizedCardGrid grid)
    {
        if (_layoutSyncedThisPass.Add(grid))
            grid.SyncLayoutForMotion();
    }

    private bool IsAuthorizedEnterLayoutRoot(FrameworkElement layoutRoot, Guid itemId)
    {
        if (FindEnterLayoutRoot(itemId) is not { } authorized)
            return false;

        return ReferenceEquals(authorized, layoutRoot);
    }

    private FrameworkElement? FindEnterLayoutRoot(Guid itemId)
    {
        // Was a fresh O(m) linear scan (EnumerateDisplayItems over the grid's entire
        // ItemsSource) PLUS a full synchronous SyncLayoutForMotion (== UpdateLayout()) call, on
        // every single invocation. Callers resolve this per entering item, so a batch of
        // thousands (e.g. "duplicate all" on the real library) turned into thousands of full
        // layout passes over a multi-thousand-item grid - multiple seconds of UI-thread block.
        // EnterItemLookup() builds the id->item map once per pass (O(m) total, not O(n*m)), and
        // EnsureGridLayoutSynced only syncs each grid once per pass instead of once per item.
        if (!EnterItemLookup().TryGetValue(itemId, out var entry))
            return null;

        var (grid, item) = entry;
        EnsureGridLayoutSynced(grid);

        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container)
            return null;

        if (FindLayoutRootInContainer(container, _scope) is not { } layoutRoot)
            return null;

        _layoutRootByItemId[itemId] = layoutRoot;
        _reflowTargetByItemId[itemId] = ResolveReflowTarget(layoutRoot);
        return layoutRoot;
    }

    private bool TryResolveContainerItemId(
        DashboardVirtualizedCardGrid grid,
        DependencyObject container,
        out Guid itemId)
    {
        itemId = Guid.Empty;
        var index = grid.ItemContainerGenerator.IndexFromContainer(container);
        if (index >= 0
            && index < grid.Items.Count
            && grid.Items[index] is { } item
            && DashboardMotionCardCatalog.TryGetItemIdFromGridItem(item, _scope, out itemId))
            return true;

        if (FindLayoutRootInContainer(container, _scope) is { } layoutRoot)
            return DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out itemId);

        return false;
    }

    private void PlayCardEnter(
        FrameworkElement layoutRoot,
        FrameworkElement motionSurface,
        Guid itemId,
        Action? onComplete = null,
        int startDelayMs = 0)
    {
        PrepareEnterHoldSlot(layoutRoot, motionSurface);

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        var enterMs = UiMotion.NoteAddEnterMs;
        UiMotion.RunComposited(
            motionSurface,
            new[]
            {
                UiMotion.Track(enterMs, UiMotion.OutCubic, 0, 1, value => motionSurface.Opacity = value, startDelayMs: startDelayMs),
                UiMotion.Track(enterMs, UiMotion.OutCubic, UiMotion.EnterScale, 1, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                }, startDelayMs: startDelayMs),
                UiMotion.Track(enterMs, UiMotion.OutCubic, -UiMotion.CardEnterLiftY, 0, value => translate.Y = value, startDelayMs: startDelayMs)
            },
            () =>
            {
                CompleteEnteredCard(layoutRoot, motionSurface, itemId);
                onComplete?.Invoke();
            });
    }

    private void CompleteEnteredCard(
        FrameworkElement layoutRoot,
        FrameworkElement motionSurface,
        Guid itemId)
    {
        FinalizeEnteredCard(layoutRoot, motionSurface);
        _enteringItemIds.Remove(itemId);
    }

    private void FinalizeEnteredCard(FrameworkElement layoutRoot, FrameworkElement motionSurface)
    {
        UiMotion.Cleanup(motionSurface);
        UiMotion.Cleanup(layoutRoot);

        ClearLayoutAnimationOverrides(layoutRoot);
        layoutRoot.Visibility = Visibility.Visible;
        layoutRoot.IsHitTestVisible = true;
        motionSurface.Opacity = 1;

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        ResetLayoutReflowTransform(layoutRoot);

        layoutRoot.Dispatcher.BeginInvoke(
            () => Behaviors.CardSurfaceMotionBehavior.SyncPointerState(layoutRoot),
            DispatcherPriority.Input);
    }

    private static void PlayCardExit(
        FrameworkElement layoutRoot,
        FrameworkElement motionSurface,
        Action? onComplete = null)
    {
        // Dedicated reference key so UiMotion.Stop(layoutRoot) from hover/recycle/cleanup
        // cannot cancel this exit and drop CommitMoveAfterExit's onComplete.
        var exitKey = CardExitMotionKeys.GetValue(layoutRoot, static _ => new object());
        UiMotion.Stop(exitKey);
        UiMotion.Stop(motionSurface);

        layoutRoot.IsHitTestVisible = false;
        HideCardShadowPlates(layoutRoot);

        // Force full opacity — drag ghost may have left the source at ~0.18.
        layoutRoot.Opacity = 1;
        motionSurface.Opacity = 1;

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        translate.X = 0;
        translate.Y = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        void Complete()
        {
            UiMotion.EndMotionPrep(layoutRoot);
            FinalizeExitedCard(layoutRoot, motionSurface);
            onComplete?.Invoke();
        }

        if (UiMotion.PrefersReducedMotion)
        {
            FinalizeExitedCard(layoutRoot, motionSurface);
            onComplete?.Invoke();
            return;
        }

        UiMotion.BeginMotionPrep(layoutRoot, suppressEffect: true);
        UiMotion.Run(
            exitKey,
            new[]
            {
                UiMotion.Track(UiMotion.NoteDeleteMs, UiMotion.InCubic, 1, 0, value => layoutRoot.Opacity = value),
                UiMotion.Track(UiMotion.NoteDeleteMs, UiMotion.InCubic, 1, UiMotion.ExitScale, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                }),
                UiMotion.Track(UiMotion.NoteDeleteMs, UiMotion.InCubic, 0, -UiMotion.CardEnterLiftY * 0.75, value => translate.Y = value)
            },
            Complete);
    }

    private static void HideCardShadowPlates(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CardShadowLayer layer)
                layer.Visibility = Visibility.Collapsed;
            else
                HideCardShadowPlates(child);
        }
    }

    private static void FinalizeExitedCard(FrameworkElement layoutRoot, FrameworkElement motionSurface)
    {
        layoutRoot.IsHitTestVisible = false;
        layoutRoot.Opacity = 0;
        motionSurface.Opacity = 0;
    }

    private FrameworkElement? FindLayoutRootById(Guid itemId)
    {
        if (_layoutRootByItemId.TryGetValue(itemId, out var cached)
            && cached.IsLoaded
            && DashboardMotionCardCatalog.TryGetItemId(cached, out var cachedId)
            && cachedId == itemId)
            return cached;

        _layoutRootByItemId.Remove(itemId);
        _reflowTargetByItemId.Remove(itemId);

        if (_dashboardRoot is null)
            return null;

        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(_dashboardRoot, _scope))
        {
            if (!DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out var id) || id != itemId)
                continue;

            _layoutRootByItemId[itemId] = layoutRoot;
            _reflowTargetByItemId[itemId] = ResolveReflowTarget(layoutRoot);
            return layoutRoot;
        }

        return null;
    }

    private static Panel? FindItemsPanel(FrameworkElement layoutRoot)
    {
        var current = layoutRoot.Parent as DependencyObject;
        while (current != null)
        {
            if (current is Panel panel)
                return panel;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public void ReleaseStuckEnteringCards()
    {
        if (_dashboardRoot is null)
            return;

        foreach (var layoutRoot in DashboardMotionCardCatalog.EnumerateLayoutRoots(_dashboardRoot, _scope))
        {
            if (!DashboardMotionCardCatalog.TryGetItemId(layoutRoot, out var itemId))
                continue;

            var motionSurface = DashboardMotionCardCatalog.ResolveMotionSurface(layoutRoot, _scope);

            if (_enteringItemIds.Contains(itemId))
            {
                // Second, recurring chance to heal a leaked _playingEnterIds entry beyond the
                // one-shot FinalizeOrphanedEnteringCards sweep right after FinishReflow - this
                // runs on every later mutation/dashboard-switch via
                // MainWindow.EnsureListingCardsInteractive, so a card that goes stale after that
                // first sweep already ran still gets cleaned up eventually.
                if (!IsStalePlayingEnter(itemId))
                    continue;

                ScrollPerfDiag.Log($"enterStuckSnap\tscope={_scope}\titemId={itemId}\topacity={motionSurface.Opacity:F2}\tsource=release");
                ClearPlayingEnter(itemId);
                CompleteEnteredCard(layoutRoot, motionSurface, itemId);
                continue;
            }

            if (layoutRoot.IsHitTestVisible && motionSurface.Opacity < 0.05)
                FinalizeEnteredCard(layoutRoot, motionSurface);
        }
    }

    public void Activate()
        => ActiveByScope[_scope] = this;

    public void Deactivate()
    {
        if (ActiveByScope.TryGetValue(_scope, out var active) && active == this)
            ActiveByScope.Remove(_scope);

        UiMotion.Stop(_reflowBatchKey);
        _layoutAnimationDepth = 0;
        EndAddMotionLayoutOnGrids();
        UnfreezeVirtualizationViewports();
        ResetDeleteBatchState();
        ResetMoveBatchState();
        _relocatingItemIds.Clear();
        _enteringItemIds.Clear();
        _scheduledEnterIds.Clear();
        _reflowScheduled = false;
        _deferredEnterIds.Clear();
        _playingEnterIds.Clear();
        _playingEnterStartedAtMs.Clear();
        _instantEnterIds.Clear();
        _batchMutationDepth = 0;
        _batchMutationInFlight = false;
        _batchMutationReflowRunning = false;
        _pendingEnterCompletions.Clear();
        _reflowSafetyTimer?.Stop();
        _reflowSafetyTimer = null;
        var afterMotion = _onReflowComplete;
        _onReflowComplete = null;
        afterMotion?.Invoke();

        var batchAfterMotion = _batchMutationAfterMotion.ToArray();
        _batchMutationAfterMotion.Clear();
        foreach (var callback in batchAfterMotion)
            callback();

        ClearMotionCaches();
    }
}
