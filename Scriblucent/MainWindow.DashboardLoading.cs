using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.ViewModels;

namespace Scriblucent;

public partial class MainWindow
{
    private readonly Stopwatch _dashboardFrameWait = new();
    private string? _loadingDashboard;
    private int _loadingGeneration;
    private bool _loadingDashboardViewActivated;
    private bool _loadingHandoffStarted;
    private EventHandler? _loadingRenderHandler;
    private DispatcherOperation? _loadingFinishOperation;

    private bool IsLoadingDashboardActive => _loadingDashboard is not null;

    private void SyncDashboardLoadingState(MainViewModel? vm)
    {
        if (vm is null)
        {
            CancelDashboardLoading();
            return;
        }

        if (vm.IsActiveDashboardLoadFailed)
        {
            ShowDashboardLoadFailure();
            return;
        }

        DashboardLoadFailurePanel.Visibility = Visibility.Collapsed;
        DashboardLoadingShell.Visibility = Visibility.Visible;

        if (!vm.IsActiveDashboardReady)
        {
            if (!string.Equals(_loadingDashboard, vm.ActiveDashboard, StringComparison.Ordinal))
                BeginDashboardLoading(vm);
            return;
        }

        if (string.Equals(_loadingDashboard, vm.ActiveDashboard, StringComparison.Ordinal))
            WaitForDashboardFirstFrame();
        else if (_loadingDashboard is not null || DashboardLoadingLayer.Visibility == Visibility.Visible)
            CancelDashboardLoading();
    }

    private void BeginDashboardLoading(MainViewModel vm)
    {
        SurfaceMotion.Stop(DashboardLoadingLayer, MotionChannel.Presence);
        CancelLoadingCallbacks();
        _loadingGeneration++;
        _loadingDashboard = vm.ActiveDashboard;
        _loadingDashboardViewActivated = !IsLoaded || string.Equals(_lastActiveDashboard, vm.ActiveDashboard, StringComparison.Ordinal);
        _loadingHandoffStarted = false;
        DashboardLoadFailurePanel.Visibility = Visibility.Collapsed;
        DashboardLoadingShell.Visibility = Visibility.Visible;
        DashboardLoadingShell.SetLayout(BuildDashboardSkeletonLayout(vm));
        DashboardLoadingShell.Opacity = 1;
        // Loading is present before Show/first paint. A delayed fade-in exposes the unfinished page.
        DashboardLoadingLayer.Opacity = 1;
        DashboardLoadingLayer.Visibility = Visibility.Visible;
        DashboardLoadingLayer.IsHitTestVisible = true;
        DashboardLoadingShell.StartWave();
    }

    /// <summary>Cover the outgoing page before replacing its content with the loading page.</summary>
    private void EnterDashboardLoading(string dashboard)
    {
        ActivateDashboardUnderLoadingShell(dashboard, _loadingGeneration);
    }

    private void ActivateDashboardUnderLoadingShell(string dashboard, int generation)
    {
        if (generation != _loadingGeneration
            || !string.Equals(_loadingDashboard, dashboard, StringComparison.Ordinal)
            || !string.Equals(_observedViewModel?.ActiveDashboard, dashboard, StringComparison.Ordinal))
            return;

        SetDashboardViewImmediate(dashboard);
        _loadingDashboardViewActivated = true;
        foreach (var view in AllDashboardViews())
            if (view is not null)
                view.IsHitTestVisible = true;
        _lastActiveDashboard = dashboard;
        _dashboardTransitionActive = false;
        DashboardShadowSuspender.TryResume(DashboardTransitionShadowScope);
        Dispatcher.BeginInvoke(EnsureAllListingDashboardsInteractive, DispatcherPriority.Loaded);
    }

    private DashboardLoadingOverlay.Layout BuildDashboardSkeletonLayout(MainViewModel vm)
    {
        double Metric(string key, double fallback) => TryFindResource(key) is double value ? value : fallback;
        var common = new DashboardLoadingOverlay.Layout
        {
            ShowRecent = vm.IsRecentSectionVisible,
            ExpandRecent = vm.IsRecentSectionExpanded,
            ShowCalendar = vm.IsCalendarSectionVisible,
            ExpandCalendar = vm.IsCalendarSectionExpanded,
            CalendarFirst = vm.IsCalendarFirst
        };

        return vm.ActiveDashboard switch
        {
            DashboardNotes => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.Notes,
                ShowContent = vm.IsNotesDashboardSectionVisible,
                ExpandContent = vm.IsNotesDashboardSectionExpanded,
                ShowGroups = vm.IsGroupsSectionVisible,
                ExpandGroups = vm.IsGroupsSectionExpanded,
                ShowUngrouped = vm.IsUngroupedSectionVisible,
                ExpandUngrouped = vm.IsUngroupedSectionExpanded,
                GroupsFirst = vm.IsGroupsFirst,
                CardWidth = DashboardVirtualizedNoteCardGrid.GridCellWidth,
                CardHeight = DashboardVirtualizedNoteCardGrid.GridCellHeight
            },
            DashboardFlashcards => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.Flashcards,
                ShowContent = vm.IsFlashcardGroupsSectionVisible,
                ExpandContent = vm.IsFlashcardGroupsSectionExpanded,
                ShowGroups = vm.IsFlashcardDashboardGroupsSectionVisible,
                ExpandGroups = vm.IsFlashcardDashboardGroupsSectionExpanded,
                ShowUngrouped = vm.IsFlashcardDashboardUngroupedSectionVisible,
                ExpandUngrouped = vm.IsFlashcardDashboardUngroupedSectionExpanded,
                GroupsFirst = vm.IsFlashcardDashboardGroupsFirst,
                CardWidth = Metric("Dashboard.UngroupedCardWidth", 330),
                CardHeight = Metric("Dashboard.UngroupedCardHeight", 204)
            },
            DashboardQuizzes => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.Quizzes,
                ShowContent = vm.IsQuizGroupsSectionVisible,
                ExpandContent = vm.IsQuizGroupsSectionExpanded,
                ShowGroups = vm.IsQuizDashboardGroupsSectionVisible,
                ExpandGroups = vm.IsQuizDashboardGroupsSectionExpanded,
                ShowUngrouped = vm.IsQuizDashboardUngroupedSectionVisible,
                ExpandUngrouped = vm.IsQuizDashboardUngroupedSectionExpanded,
                GroupsFirst = vm.IsQuizDashboardGroupsFirst,
                CardWidth = Metric("Dashboard.UngroupedCardWidth", 330),
                CardHeight = Metric("Dashboard.UngroupedCardHeight", 204)
            },
            DashboardMindMaps => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.MindMaps,
                ShowContent = vm.IsMindMapGroupsSectionVisible,
                ExpandContent = vm.IsMindMapGroupsSectionExpanded,
                ShowGroups = vm.IsMindMapDashboardGroupsSectionVisible,
                ExpandGroups = vm.IsMindMapDashboardGroupsSectionExpanded,
                ShowUngrouped = vm.IsMindMapDashboardUngroupedSectionVisible,
                ExpandUngrouped = vm.IsMindMapDashboardUngroupedSectionExpanded,
                GroupsFirst = vm.IsMindMapDashboardGroupsFirst,
                CardWidth = Metric("Dashboard.MindMapUngroupedCardWidth", 360),
                CardHeight = Metric("Dashboard.MindMapUngroupedCardHeight", 224)
            },
            _ => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.Notes,
                ShowContent = true,
                ExpandContent = true,
                ShowUngrouped = true,
                ExpandUngrouped = true,
                GroupsFirst = true,
                CardWidth = DashboardVirtualizedNoteCardGrid.GridCellWidth,
                CardHeight = DashboardVirtualizedNoteCardGrid.GridCellHeight
            }
        };
    }

    private void WaitForDashboardFirstFrame()
    {
        if (_loadingHandoffStarted || _loadingRenderHandler is not null || _loadingFinishOperation is not null)
            return;
        if (!IsLoaded) { CancelDashboardLoading(); return; }
        _dashboardFrameWait.Restart();
        var generation = _loadingGeneration;
        _loadingRenderHandler = (_, _) =>
        {
            if (generation != _loadingGeneration || _loadingDashboard is null || !_loadingDashboardViewActivated)
                return;
            if (!IsVisibleDashboardViewportRealized() && _dashboardFrameWait.Elapsed < TimeSpan.FromSeconds(2))
                return;
            if (_loadingRenderHandler is not null)
                CompositionTarget.Rendering -= _loadingRenderHandler;
            _loadingRenderHandler = null;
            _loadingFinishOperation = Dispatcher.BeginInvoke(() =>
            {
                _loadingFinishOperation = null;
                FinishDashboardLoading(generation);
            }, DispatcherPriority.Loaded);
        };
        CompositionTarget.Rendering += _loadingRenderHandler;
    }

    private bool IsVisibleDashboardViewportRealized()
    {
        var view = ResolveDashboardView(_loadingDashboard);
        if (view is null || !view.IsVisible || view.ActualWidth <= 0 || view.ActualHeight <= 0)
            return false;

        return AreVisibleCardGridsRealized(view);
    }

    private bool AreVisibleCardGridsRealized(DependencyObject node)
    {
        if (node is UIElement { IsVisible: false }) return true;
        if (node is DashboardVirtualizedCardGrid { HasItems: true } grid
            && grid.IsVisible && IsInDashboardViewport(grid) && !grid.HasRealizedContainers())
            return false;

        if (node is NoteCardControl { IsBitmapMode: false, DataContext: NoteCardViewModel note } card
            && card.IsVisible && IsInDashboardViewport(card) && !note.IsContentPreviewCached)
            return false;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (!AreVisibleCardGridsRealized(VisualTreeHelper.GetChild(node, i)))
                return false;
        }
        return true;
    }

    private bool IsInDashboardViewport(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        try
        {
            var bounds = element.TransformToAncestor(DashboardLoadingLayer.Parent as Visual ?? this)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.IntersectsWith(new Rect(0, 0,
                DashboardLoadingLayer.ActualWidth, DashboardLoadingLayer.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void FinishDashboardLoading(int generation)
    {
        if (generation != _loadingGeneration || _loadingDashboard is null
            || _observedViewModel is not { IsActiveDashboardReady: true } vm
            || !string.Equals(vm.ActiveDashboard, _loadingDashboard, StringComparison.Ordinal)) return;
        FadeDashboardLoadingShell(generation);
    }

    private void FadeDashboardLoadingShell(int generation)
    {
        if (generation != _loadingGeneration || _loadingDashboard is null || _loadingHandoffStarted) return;
        _loadingHandoffStarted = true;
        _dashboardFrameWait.Reset();
        DashboardLoadingShell.StopWave();
        // Only the cover dissolves. The hydrated dashboard is already opaque and at rest.
        EnsureAllDashboardContentHostsRestState();
        foreach (var scope in Enum.GetValues<Scriblucent.Services.DashboardMotionScope>()) EnsureRecentSectionRestState(scope);
        SurfaceMotion.Animate(DashboardLoadingLayer, 0, 180, UiMotion.InOutSine, false, () =>
        {
            if (generation == _loadingGeneration) CancelDashboardLoading();
        });
    }

    private void CancelDashboardLoading()
    {
        SurfaceMotion.Stop(DashboardLoadingLayer, MotionChannel.Presence);
        CancelLoadingCallbacks();
        _loadingGeneration++;
        _loadingDashboard = null;
        _loadingDashboardViewActivated = false;
        _loadingHandoffStarted = false;
        DashboardLoadingShell.StopWave();
        DashboardLoadingShell.Opacity = 1;
        DashboardLoadingShell.Visibility = Visibility.Visible;
        DashboardLoadFailurePanel.Visibility = Visibility.Collapsed;
        DashboardLoadingLayer.Opacity = 1;
        DashboardLoadingLayer.IsHitTestVisible = false;
        DashboardLoadingLayer.Visibility = Visibility.Collapsed;
    }

    private void ShowDashboardLoadFailure()
    {
        if (_loadingDashboard is not null || DashboardLoadFailurePanel.Visibility != Visibility.Visible)
            CancelDashboardLoading();

        DashboardLoadingShell.Visibility = Visibility.Collapsed;
        DashboardLoadFailurePanel.Visibility = Visibility.Visible;
        DashboardLoadingLayer.Opacity = 1;
        DashboardLoadingLayer.IsHitTestVisible = true;
        DashboardLoadingLayer.Visibility = Visibility.Visible;
    }

    private void RetryDashboardLoad_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.RetryActiveDashboardLoad();
    }

    private void CancelLoadingCallbacks()
    {
        if (_loadingRenderHandler is not null)
            CompositionTarget.Rendering -= _loadingRenderHandler;
        _loadingRenderHandler = null;
        _loadingFinishOperation?.Abort();
        _loadingFinishOperation = null;
        _dashboardFrameWait.Reset();
    }
}
