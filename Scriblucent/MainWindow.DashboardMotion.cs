using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Services;
namespace Scriblucent;

public partial class MainWindow : IDashboardMotionHost
{
    private readonly Dictionary<DashboardMotionScope, DashboardMotionEngine> _dashboardMotionEngines = new();
    private readonly HashSet<DashboardMotionScope> _pendingGeometryCorrections = new();
    private bool _skipGroupRemoveAnimation;

    private DashboardMotionEngine EnsureDashboardMotionEngine(DashboardMotionScope scope)
    {
        if (!_dashboardMotionEngines.TryGetValue(scope, out var engine))
        {
            engine = new DashboardMotionEngine(Dispatcher, scope);
            _dashboardMotionEngines[scope] = engine;
        }

        engine.OnCollectionLayoutChanged = () => RefreshVirtualizationViewportForScope(scope);

        switch (scope)
        {
            case DashboardMotionScope.Notes:
                engine.Bind(
                    NotesDashboardSectionBodyElement,
                    UngroupedSectionBodyElement,
                    UngroupedNotesItemsControlElement,
                    NotesScrollViewer);
                break;
            case DashboardMotionScope.Flashcards:
                engine.Bind(
                    FlashcardsDashboardViewElement,
                    FlashcardDashboardUngroupedSectionBodyElement,
                    FlashcardDashboardUngroupedItemsControlElement,
                    FlashcardsScrollViewerElement);
                break;
            case DashboardMotionScope.Quizzes:
                engine.Bind(
                    QuizzesDashboardViewElement,
                    QuizDashboardUngroupedSectionBodyElement,
                    QuizDashboardUngroupedItemsControlElement,
                    QuizzesScrollViewerElement);
                break;
            case DashboardMotionScope.MindMaps:
                engine.Bind(
                    MindMapsDashboardViewElement,
                    MindMapDashboardUngroupedSectionBodyElement,
                    MindMapDashboardUngroupedItemsControlElement,
                    MindMapsScrollViewerElement);
                break;
        }

        engine.Activate();
        return engine;
    }

    private void ReleaseDashboardMotionEngines()
    {
        foreach (var engine in _dashboardMotionEngines.Values)
            engine.Deactivate();

        _dashboardMotionEngines.Clear();
    }

    public void PrepareMutation(DashboardMotionScope scope)
    {
        var engine = EnsureDashboardMotionEngine(scope);
        engine.BeginLayoutAnimation();
        engine.CapturePositions(ungroupedOnly: false, freezeViewport: false);
    }

    public void PrepareItemAdd(DashboardMotionScope scope, Guid itemId)
        => EnsureDashboardMotionEngine(scope).PreparePendingEnter(itemId);

    public void CompleteItemAdd(DashboardMotionScope scope, Guid itemId, bool deferCardEnter = false)
        => EnsureDashboardMotionEngine(scope).ScheduleReflowAfterLayout(itemId, deferCardEnter);

    public void BeginBatchMutation(DashboardMotionScope scope)
        => EnsureDashboardMotionEngine(scope).BeginBatchMutation();

    public void RegisterBatchItemAdd(DashboardMotionScope scope, Guid itemId)
        => EnsureDashboardMotionEngine(scope).RegisterBatchItemAdd(itemId);

    public void CompleteBatchAdd(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action? afterMotion = null)
        => EnsureDashboardMotionEngine(scope).CompleteBatchAdd(itemIds, afterMotion);

    public void CompleteMutation(DashboardMotionScope scope, Action? afterMotion = null)
        => EnsureDashboardMotionEngine(scope).ScheduleReflowAfterLayout(afterMotion: afterMotion);

    public void RequestItemMove(DashboardMotionScope scope, Guid itemId, Action commitMove, Action? afterMotion = null)
    {
        EnsureDashboardMotionEngine(scope).RequestItemMove(itemId, commitMove, afterMotion);
    }

    public void RequestItemMoveBatch(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action commitMove, Action? afterMotion = null)
    {
        EnsureDashboardMotionEngine(scope).RequestItemMoveBatch(itemIds, commitMove, afterMotion);
    }

    public void RequestDelete(DashboardMotionScope scope, Guid itemId, Action commitDelete, Action? afterMotion = null)
    {
        EnsureDashboardMotionEngine(scope).RequestDelete(itemId, commitDelete, afterMotion);
    }

    public void RequestDeleteBatch(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action commitDelete, Action? afterMotion = null)
    {
        EnsureDashboardMotionEngine(scope).RequestDeleteBatch(itemIds, commitDelete, afterMotion);
    }

    public void PlayDashboardContentReveal(DashboardMotionScope scope)
    {
        if (!IsLoaded)
            return;

        EnsureDashboardContentHostRestState(scope);

        if (DataContext is not ViewModels.MainViewModel { IsDashboardRevealMotionEnabled: true })
        {
            EnsureListingCardsInteractive(scope);
            return;
        }

        ScheduleDashboardContentReveal(scope, attempt: 0);
    }

    private void ScheduleDashboardContentReveal(DashboardMotionScope scope, int attempt)
    {
        if (!IsLoaded)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            EnsureDashboardContentHostRestState(scope);

            var target = ResolveDashboardContentRevealTarget(scope);
            if (target is null || target.Visibility != Visibility.Visible)
            {
                if (attempt < 24)
                {
                    ScheduleDashboardContentReveal(scope, attempt + 1);
                    return;
                }

                EnsureDashboardContentHostRestState(scope);
                return;
            }

            UiMotion.AnimateDashboardContentHostEnter(target, onComplete: () =>
            {
                EnsureDashboardContentHostRestState(scope);
                EnsureListingCardsInteractive(scope);
            });
        }, attempt == 0 ? DispatcherPriority.Render : DispatcherPriority.Loaded);
    }

    internal void EnsureDashboardContentHostRestState(DashboardMotionScope scope)
    {
        var host = ResolveDashboardContentHost(scope);
        if (host is not null)
            UiMotion.EnsureContentRevealRestState(host);

        if (ResolveDashboardContentRevealTarget(scope) is { } target)
            UiMotion.EnsureContentRevealRestState(target);
    }

    internal void EnsureAllDashboardContentHostsRestState()
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
            EnsureDashboardContentHostRestState(scope);
    }

    private FrameworkElement? ResolveDashboardContentHost(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => NotesDashboardContentHostElement,
            DashboardMotionScope.Flashcards => FlashcardsDashboardContentHostElement,
            DashboardMotionScope.Quizzes => QuizzesDashboardContentHostElement,
            DashboardMotionScope.MindMaps => MindMapsDashboardContentHostElement,
            _ => null
        };

    private FrameworkElement? ResolveDashboardContentRevealTarget(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => UngroupedSectionContainerElement,
            DashboardMotionScope.Flashcards => FlashcardDashboardUngroupedSectionContainerElement,
            DashboardMotionScope.Quizzes => QuizDashboardUngroupedSectionContainerElement,
            DashboardMotionScope.MindMaps => MindMapDashboardUngroupedSectionContainerElement,
            _ => null
        };

    public void RequestGroupRemovals(DashboardMotionScope scope, IReadOnlyList<Guid> groupIds, Action commitSync)
    {
        if (groupIds.Count == 0)
        {
            commitSync();
            return;
        }

        if (!IsLoaded || UiMotion.PrefersReducedMotion)
        {
            commitSync();
            return;
        }

        var itemsControl = ResolveGroupsItemsControl(scope);
        if (itemsControl is null)
        {
            commitSync();
            return;
        }

        itemsControl.UpdateLayout();
        var borders = new List<FrameworkElement>();
        foreach (var groupId in groupIds)
        {
            var border = FindGroupCardBorder(itemsControl, groupId);
            if (border is not null)
                borders.Add(border);
        }

        if (borders.Count == 0)
        {
            commitSync();
            return;
        }

        var remaining = borders.Count;
        foreach (var border in borders)
        {
            Behaviors.CardSurfaceMotionBehavior.PlayExit(border, () =>
            {
                remaining--;
                if (remaining > 0)
                    return;

                _skipGroupRemoveAnimation = true;
                try
                {
                    commitSync();
                }
                finally
                {
                    Dispatcher.BeginInvoke(() => _skipGroupRemoveAnimation = false, DispatcherPriority.Loaded);
                }
            });
        }
    }

    public void ScheduleGroupCardEnter(DashboardMotionScope scope, Guid groupId)
        => ScheduleGroupCardEnterCore(scope, groupId, attempt: 0);

    private void ScheduleGroupCardEnterCore(DashboardMotionScope scope, Guid groupId, int attempt)
    {
        if (!IsLoaded || UiMotion.PrefersReducedMotion)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            var itemsControl = ResolveGroupsItemsControl(scope);
            var border = itemsControl is null ? null : FindGroupCardBorder(itemsControl, groupId);
            if (border is null)
            {
                if (attempt < 24)
                    ScheduleGroupCardEnterCore(scope, groupId, attempt + 1);

                return;
            }

            Behaviors.CardSurfaceMotionBehavior.PlayGroupEnter(border);
        }, attempt == 0 ? DispatcherPriority.Render : DispatcherPriority.Loaded);
    }

    internal bool ShouldSkipGroupRemoveAnimation => _skipGroupRemoveAnimation;

    private ItemsControl? ResolveGroupsItemsControl(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => GroupsItemsControlElement,
            DashboardMotionScope.Flashcards => FlashcardDashboardGroupsItemsControlElement,
            DashboardMotionScope.Quizzes => QuizDashboardGroupsItemsControlElement,
            DashboardMotionScope.MindMaps => MindMapDashboardGroupsItemsControlElement,
            _ => null
        };

    public void PlayGroupsSectionReveal(DashboardMotionScope scope)
    {
        if (!IsLoaded)
            return;

        var container = ResolveGroupsSectionContainer(scope);
        if (container is null || container.Visibility != Visibility.Visible)
            return;

        if (DataContext is not ViewModels.MainViewModel { IsDashboardRevealMotionEnabled: true }
            || UiMotion.PrefersReducedMotion)
        {
            UiMotion.EnsureContentRevealRestState(container);
            return;
        }

        UiMotion.AnimateContentEnter(
            container,
            durationMs: UiMotion.GroupEnterMs,
            offsetY: UiMotion.GroupEnterLiftY);
    }

    private FrameworkElement? ResolveGroupsSectionContainer(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => GroupsSectionContainerElement,
            DashboardMotionScope.Flashcards => FlashcardDashboardGroupsSectionContainerElement,
            DashboardMotionScope.Quizzes => QuizDashboardGroupsSectionContainerElement,
            DashboardMotionScope.MindMaps => MindMapDashboardGroupsSectionContainerElement,
            _ => null
        };

    public void EnsureListingCardsInteractive(DashboardMotionScope scope)
    {
        EnsureDashboardMotionEngine(scope).ReleaseStuckEnteringCards();
        EnsureDashboardCardGridsInteractive(scope);
    }

    internal void EnsureActiveListingDashboardInteractive()
    {
        if (DataContext is not ViewModels.MainViewModel vm)
            return;

        var scope = vm.ActiveDashboard switch
        {
            "Notes" => DashboardMotionScope.Notes,
            "Flashcards" => DashboardMotionScope.Flashcards,
            "Quizzes" => DashboardMotionScope.Quizzes,
            "MindMaps" => DashboardMotionScope.MindMaps,
            _ => (DashboardMotionScope?)null
        };

        if (scope is { } activeScope)
            EnsureListingCardsInteractive(activeScope);
    }

    internal void EnsureAllListingDashboardsInteractive()
    {
        AttachDashboardVirtualizationHosts();

        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
            EnsureListingCardsInteractive(scope);
    }

    private void AttachDashboardVirtualizationHosts()
    {
        AttachVirtualizationHosts(NotesScrollViewer, NotesDashboardViewElement, DashboardMotionScope.Notes);
        AttachVirtualizationHosts(FlashcardsScrollViewerElement, FlashcardsDashboardViewElement, DashboardMotionScope.Flashcards);
        AttachVirtualizationHosts(QuizzesScrollViewerElement, QuizzesDashboardViewElement, DashboardMotionScope.Quizzes);
        AttachVirtualizationHosts(MindMapsScrollViewerElement, MindMapsDashboardViewElement, DashboardMotionScope.MindMaps);
    }

    internal void RefreshDashboardVirtualizationHosts(DashboardMotionScope? scope = null)
    {
        if (IsDashboardTransitionActive)
            return;

        AttachDashboardVirtualizationHosts();

        if (scope is { } singleScope)
        {
            RefreshVirtualizationViewportForScope(singleScope);
            return;
        }

        foreach (var dashboardScope in Enum.GetValues<DashboardMotionScope>())
            RefreshVirtualizationViewportForScope(dashboardScope);
    }

    private static void AttachVirtualizationHosts(ScrollViewer? scrollViewer, DependencyObject? root, DashboardMotionScope scope)
    {
        if (scrollViewer is null || root is null)
            return;

        foreach (var host in EnumerateDescendantVirtualizationHosts(root))
        {
            if (host.MotionScope != scope)
            {
                host.MotionScope = scope;
                host.InvalidateSectionGeometry();
            }

            if (host.ScrollViewerElement is null)
            {
                host.ScrollViewerElement = scrollViewer;
                host.InvalidateSectionGeometry();
            }
            else if (!ReferenceEquals(host.ScrollViewerElement, scrollViewer))
            {
                host.ScrollViewerElement = scrollViewer;
                host.InvalidateSectionGeometry();
            }
        }
    }

    private static IEnumerable<Controls.DashboardVirtualizationHost> EnumerateDescendantVirtualizationHosts(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Controls.DashboardVirtualizationHost host)
                yield return host;

            foreach (var nested in EnumerateDescendantVirtualizationHosts(child))
                yield return nested;
        }
    }

    private void RefreshVirtualizationViewportForScope(DashboardMotionScope scope)
    {
        var animActive = _dashboardMotionEngines.TryGetValue(scope, out var engine) && engine.IsLayoutAnimationActive;
        Scriblucent.Services.ScrollPerfDiag.Log($"refreshViewportForScope\tscope={scope}\tanimActive={animActive}");
        if (animActive)
            return;

        if (ResolveDashboardRoot(scope) is { } root)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var count = 0;
                foreach (var host in EnumerateDescendantVirtualizationHosts(root))
                {
                    count++;
                    host.FlushDeferredMutationRefresh();
                    host.RequestViewportRefresh();
                }
                Scriblucent.Services.ScrollPerfDiag.Log($"refreshViewportForScope-hostsRefreshed\tscope={scope}\tcount={count}");

                EnsureDashboardCardGridsInteractive(scope);
                IndicatorRevealMotionBehavior.FlushPending();
                ScheduleSectionGeometryCorrection(scope);
            }, DispatcherPriority.Loaded);
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            EnsureDashboardCardGridsInteractive(scope);
            IndicatorRevealMotionBehavior.FlushPending();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A second, deliberately-delayed sweep after RefreshVirtualizationViewportForScope's own
    /// Loaded-priority pass. A section created in the same mutation as this refresh (e.g. a new
    /// group from GroupSelectedNotes) may not have generated its DashboardVirtualizationHost yet,
    /// or may not have settled to its final arranged height yet, when that first sweep runs -
    /// existing SIBLING hosts swept at that point can latch a _sectionTop computed against the
    /// still-settling section, with no second correction ever scheduled (TryUpdateSectionTop only
    /// recomputes when _sectionTopIsCurrent is false). Background priority is strictly lower than
    /// Loaded/Render, so this cannot run until any layout pass from the new section's own first
    /// measure/arrange has fully drained, making it safe to unconditionally re-latch here.
    /// </summary>
    private void ScheduleSectionGeometryCorrection(DashboardMotionScope scope)
    {
        if (!_pendingGeometryCorrections.Add(scope))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            _pendingGeometryCorrections.Remove(scope);

            if (_dashboardMotionEngines.TryGetValue(scope, out var engine) && engine.IsLayoutAnimationActive)
                return;

            AttachDashboardVirtualizationHosts();

            if (ResolveDashboardRoot(scope) is not { } root)
                return;

            var count = 0;
            foreach (var host in EnumerateDescendantVirtualizationHosts(root))
            {
                count++;
                host.InvalidateSectionGeometry();
                host.RequestViewportRefresh();
            }

            Scriblucent.Services.ScrollPerfDiag.Log($"sectionGeometryCorrection\tscope={scope}\thosts={count}");
            EnsureDashboardCardGridsInteractive(scope);
        }, DispatcherPriority.Background);
    }

    private void EnsureDashboardCardGridsInteractive(DashboardMotionScope scope)
    {
        foreach (var grid in ResolveDashboardCardGrids(scope))
            grid.EnsureRealizedCardsInteractive();
    }

    private DependencyObject? ResolveDashboardRoot(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => NotesDashboardViewElement,
            DashboardMotionScope.Flashcards => FlashcardsDashboardViewElement,
            DashboardMotionScope.Quizzes => QuizzesDashboardViewElement,
            DashboardMotionScope.MindMaps => MindMapsDashboardViewElement,
            _ => null
        };

    private static DashboardMotionScope? ResolveDashboardMotionScope(string? dashboard)
        => dashboard switch
        {
            DashboardNotes => DashboardMotionScope.Notes,
            DashboardFlashcards => DashboardMotionScope.Flashcards,
            DashboardQuizzes => DashboardMotionScope.Quizzes,
            DashboardMindMaps => DashboardMotionScope.MindMaps,
            _ => null
        };

    private void PlayDashboardSwitchReveal(DashboardMotionScope scope)
    {
        if (!IsLoaded)
            return;

        Dispatcher.BeginInvoke(
            () => PlayDashboardSwitchRevealCore(scope),
            DispatcherPriority.Loaded);
    }

    private void PlayDashboardSwitchRevealCore(DashboardMotionScope scope)
    {
        EnsureRecentSectionRestState(scope);

        if (DataContext is not ViewModels.MainViewModel { IsDashboardRevealMotionEnabled: true }
            || UiMotion.PrefersReducedMotion)
        {
            FinishDashboardSwitchReveal(scope);
            return;
        }

        ScheduleRecentStripEnterForScope(scope);

        var targets = ResolveDashboardSwitchRevealTargets(scope)
            .Where(IsDashboardSwitchRevealTargetVisible)
            .ToList();

        if (targets.Count == 0)
        {
            FinishDashboardSwitchReveal(scope);
            return;
        }

        if (targets.Count == 1 && IsEmptyDashboardPanel(targets[0]))
        {
            UiMotion.AnimateEmptyDashboardEnter(targets[0], onComplete: () => FinishDashboardSwitchReveal(scope));
            return;
        }

        foreach (var target in targets)
            UiMotion.PrepDashboardContentRevealStart(target);

        const int staggerStepMs = 36;
        var remaining = targets.Count;
        for (var index = 0; index < targets.Count; index++)
        {
            var delayMs = index * staggerStepMs;
            UiMotion.AnimateDashboardContentHostEnter(
                targets[index],
                startDelayMs: delayMs,
                onComplete: () =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                        FinishDashboardSwitchReveal(scope);
                });
        }
    }

    private void FinishDashboardSwitchReveal(DashboardMotionScope scope)
    {
        EnsureDashboardContentHostRestState(scope);

        if (scope == DashboardMotionScope.Notes)
            RefreshNotesDashboardLayout();
        else
            RefreshDashboardVirtualizationHosts(scope);

        RefreshVirtualizationViewportForScope(scope);
        EnsureListingCardsInteractive(scope);
        IndicatorRevealMotionBehavior.FlushPending();
    }

    private void EnsureRecentSectionRestState(DashboardMotionScope scope)
    {
        if (ResolveRecentSectionBody(scope) is { } recentBody)
            UiMotion.EnsureContentRevealRestState(recentBody);
    }

    private void ScheduleRecentStripEnterForScope(DashboardMotionScope scope, int startDelayMs = 0)
    {
        if (_observedViewModel is not { IsRecentSectionVisible: true, IsRecentSectionExpanded: true })
            return;

        if (ResolveRecentSectionBody(scope) is not FrameworkElement sectionBody
            || sectionBody.Visibility != Visibility.Visible)
            return;

        void Play()
        {
            sectionBody.UpdateLayout();
            RecentStripEnterMotionBehavior.TryRequestEnterAnimation(sectionBody);
        }

        if (startDelayMs <= 0)
        {
            Dispatcher.BeginInvoke(Play, DispatcherPriority.ApplicationIdle);
            return;
        }

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(startDelayMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Play();
        };
        timer.Start();
    }

    private static bool IsDashboardSwitchRevealTargetVisible(FrameworkElement element)
        => element.Visibility == Visibility.Visible && element.IsVisible;

    private bool IsEmptyDashboardPanel(FrameworkElement element)
        => ReferenceEquals(element, NotesEmptyDashboardPanelElement)
           || ReferenceEquals(element, FlashcardsEmptyDashboardPanelElement)
           || ReferenceEquals(element, QuizzesEmptyDashboardPanelElement)
           || ReferenceEquals(element, MindMapsEmptyDashboardPanelElement);

    private IEnumerable<FrameworkElement> ResolveDashboardSwitchRevealTargets(DashboardMotionScope scope)
    {
        if (ResolveEmptyDashboardPanel(scope) is { } emptyPanel
            && emptyPanel.Visibility == Visibility.Visible)
        {
            yield return emptyPanel;
            yield break;
        }

        if (ResolveCalendarSectionContainer(scope) is { } calendar
            && calendar.Visibility == Visibility.Visible)
            yield return calendar;

        if (ResolveDashboardContentHost(scope) is { } contentHost
            && contentHost.Visibility == Visibility.Visible)
            yield return contentHost;
    }

    private FrameworkElement? ResolveEmptyDashboardPanel(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => NotesEmptyDashboardPanelElement,
            DashboardMotionScope.Flashcards => FlashcardsEmptyDashboardPanelElement,
            DashboardMotionScope.Quizzes => QuizzesEmptyDashboardPanelElement,
            DashboardMotionScope.MindMaps => MindMapsEmptyDashboardPanelElement,
            _ => null
        };

    private FrameworkElement? ResolveCalendarSectionContainer(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => CalendarSectionContainerElement,
            DashboardMotionScope.Flashcards => FlashcardCalendarSectionContainerElement,
            DashboardMotionScope.Quizzes => QuizCalendarSectionContainerElement,
            DashboardMotionScope.MindMaps => MindMapCalendarSectionContainerElement,
            _ => null
        };

    private FrameworkElement? ResolveRecentSectionBody(DashboardMotionScope scope)
        => scope switch
        {
            DashboardMotionScope.Notes => RecentSectionBodyElement,
            DashboardMotionScope.Flashcards => FlashcardRecentSectionBodyElement,
            DashboardMotionScope.Quizzes => QuizRecentSectionBodyElement,
            DashboardMotionScope.MindMaps => MindMapRecentSectionBodyElement,
            _ => null
        };

    private IEnumerable<Controls.DashboardVirtualizedCardGrid> ResolveDashboardCardGrids(DashboardMotionScope scope)
    {
        switch (scope)
        {
            case DashboardMotionScope.Notes:
                if (FindDashboardName("UngroupedNotesItemsControl") is Controls.DashboardVirtualizedCardGrid notesUngrouped)
                    yield return notesUngrouped;
                break;
            case DashboardMotionScope.Flashcards:
                if (FindDashboardName("FlashcardDashboardUngroupedItemsControl") is Controls.DashboardVirtualizedCardGrid flashcardsUngrouped)
                    yield return flashcardsUngrouped;
                break;
            case DashboardMotionScope.Quizzes:
                if (FindDashboardName("QuizDashboardUngroupedItemsControl") is Controls.DashboardVirtualizedCardGrid quizzesUngrouped)
                    yield return quizzesUngrouped;
                break;
            case DashboardMotionScope.MindMaps:
                if (FindDashboardName("MindMapDashboardUngroupedItemsControl") is Controls.DashboardVirtualizedCardGrid mindMapsUngrouped)
                    yield return mindMapsUngrouped;
                break;
        }

        var groupsControlName = scope switch
        {
            DashboardMotionScope.Notes => "GroupsItemsControl",
            DashboardMotionScope.Flashcards => "FlashcardDashboardGroupsItemsControl",
            DashboardMotionScope.Quizzes => "QuizDashboardGroupsItemsControl",
            DashboardMotionScope.MindMaps => "MindMapDashboardGroupsItemsControl",
            _ => null
        };

        if (groupsControlName is null
            || FindDashboardName(groupsControlName) is not ItemsControl groupsControl)
            yield break;

        foreach (var grid in EnumerateDescendantCardGrids(groupsControl))
            yield return grid;
    }

    private static IEnumerable<Controls.DashboardVirtualizedCardGrid> EnumerateDescendantCardGrids(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Controls.DashboardVirtualizedCardGrid grid)
                yield return grid;

            foreach (var nested in EnumerateDescendantCardGrids(child))
                yield return nested;
        }
    }
}
