using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.ViewModels;

namespace Scriblucent;

public partial class MainWindow
{
    private const int DashboardWaveMs = DashboardLoadingOverlay.WaveDurationMs;
    private readonly Stopwatch _dashboardLoadingClock = new();
    private string? _loadingDashboard;
    private int _loadingGeneration;
    private int _loadingFramesWaiting;
    private bool _loadingDashboardViewActivated;
    private bool _startupLoadingEntrancePending;
    private EventHandler? _loadingRenderHandler;
    private DispatcherTimer? _loadingFinishTimer;

    private bool IsLoadingDashboardActive => _loadingDashboard is not null;

    private void SyncDashboardLoadingState(MainViewModel? vm)
    {
        if (vm is null)
        {
            CancelDashboardLoading();
            return;
        }

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
        var enterOverCurrentDashboard = IsLoaded
            && !UiMotion.PrefersReducedMotion
            && !string.Equals(_lastActiveDashboard, vm.ActiveDashboard, StringComparison.Ordinal);
        var initialOpacity = enterOverCurrentDashboard
            ? DashboardLoadingLayer.Visibility == Visibility.Visible
                ? DashboardLoadingLayer.Opacity : 0
            : 1;

        CancelLoadingCallbacks();
        _loadingGeneration++;
        _loadingDashboard = vm.ActiveDashboard;
        _loadingFramesWaiting = 0;
        _loadingDashboardViewActivated = !enterOverCurrentDashboard;
        _startupLoadingEntrancePending = !IsLoaded && !UiMotion.PrefersReducedMotion;
        _dashboardLoadingClock.Restart();
        DashboardLoadingShell.SetLayout(BuildDashboardSkeletonLayout(vm));
        DashboardLoadingShell.BeginAnimation(UIElement.OpacityProperty, null);
        DashboardLoadingShell.Opacity = _startupLoadingEntrancePending ? 0 : 1;
        DashboardLoadingLayer.BeginAnimation(UIElement.OpacityProperty, null);
        DashboardLoadingLayer.Opacity = initialOpacity;
        DashboardLoadingLayer.Visibility = Visibility.Visible;
        DashboardLoadingShell.StartWave(UiMotion.PrefersReducedMotion);
    }

    private void StartStartupLoadingEntrance()
    {
        if (!_startupLoadingEntrancePending || _loadingDashboard is null)
            return;

        _startupLoadingEntrancePending = false;
        var generation = _loadingGeneration;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (generation != _loadingGeneration)
                return;
            DashboardLoadingShell.BeginAnimation(UIElement.OpacityProperty, null);
            DashboardLoadingShell.Opacity = 1;
        };
        DashboardLoadingShell.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Cover the outgoing page before replacing its content with the loading page.</summary>
    private void EnterDashboardLoading(string dashboard)
    {
        var generation = _loadingGeneration;
        if (UiMotion.PrefersReducedMotion || DashboardLoadingLayer.Opacity >= 0.99)
        {
            ActivateDashboardUnderLoadingShell(dashboard, generation);
            return;
        }

        var fade = new DoubleAnimation(DashboardLoadingLayer.Opacity, 1,
            TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) => ActivateDashboardUnderLoadingShell(dashboard, generation);
        DashboardLoadingLayer.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void ActivateDashboardUnderLoadingShell(string dashboard, int generation)
    {
        if (generation != _loadingGeneration
            || !string.Equals(_loadingDashboard, dashboard, StringComparison.Ordinal)
            || !string.Equals(_observedViewModel?.ActiveDashboard, dashboard, StringComparison.Ordinal))
            return;

        DashboardLoadingLayer.BeginAnimation(UIElement.OpacityProperty, null);
        DashboardLoadingLayer.Opacity = 1;
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

    private static DashboardLoadingOverlay.Layout BuildDashboardSkeletonLayout(MainViewModel vm)
    {
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
                CardWidth = 244,
                CardHeight = 164
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
                CardWidth = 330,
                CardHeight = 204
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
                CardWidth = 330,
                CardHeight = 194
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
                CardWidth = 360,
                CardHeight = 224
            },
            _ => common with
            {
                Kind = DashboardLoadingOverlay.DashboardKind.Notes,
                ShowContent = true,
                ExpandContent = true,
                ShowUngrouped = true,
                ExpandUngrouped = true,
                GroupsFirst = true,
                CardWidth = 244,
                CardHeight = 164
            }
        };
    }

    private void WaitForDashboardFirstFrame()
    {
        if (_loadingRenderHandler is not null || _loadingFinishTimer is not null)
            return;

        var generation = _loadingGeneration;
        _loadingRenderHandler = (_, _) =>
        {
            if (generation != _loadingGeneration || _loadingDashboard is null)
                return;

            if (!_loadingDashboardViewActivated)
                return;

            if (!IsVisibleDashboardViewportRealized() && ++_loadingFramesWaiting < 120)
                return;

            if (_loadingRenderHandler is not null)
                CompositionTarget.Rendering -= _loadingRenderHandler;
            _loadingRenderHandler = null;
            Dispatcher.BeginInvoke(() => FinishDashboardLoadingAfterWave(generation), DispatcherPriority.Loaded);
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

    private void FinishDashboardLoadingAfterWave(int generation)
    {
        if (generation != _loadingGeneration || _loadingDashboard is null
            || _observedViewModel is not { IsActiveDashboardReady: true } vm
            || !string.Equals(vm.ActiveDashboard, _loadingDashboard, StringComparison.Ordinal))
            return;

        var remaining = UiMotion.PrefersReducedMotion ? 0 :
            DashboardWaveMs - (int)(_dashboardLoadingClock.ElapsedMilliseconds % DashboardWaveMs);
        if (!UiMotion.PrefersReducedMotion && remaining < 300)
            remaining += DashboardWaveMs;
        _loadingFinishTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, remaining))
        };
        _loadingFinishTimer.Tick += (_, _) =>
        {
            _loadingFinishTimer?.Stop();
            _loadingFinishTimer = null;
            FadeDashboardLoadingShell(generation);
        };
        _loadingFinishTimer.Start();
    }

    private void FadeDashboardLoadingShell(int generation)
    {
        if (generation != _loadingGeneration || _loadingDashboard is null)
            return;

        DashboardLoadingShell.StopWave();
        if (UiMotion.PrefersReducedMotion)
        {
            CancelDashboardLoading();
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (generation == _loadingGeneration)
                CancelDashboardLoading();
        };
        DashboardLoadingLayer.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void CancelDashboardLoading()
    {
        CancelLoadingCallbacks();
        _loadingGeneration++;
        _loadingDashboard = null;
        _loadingDashboardViewActivated = false;
        _startupLoadingEntrancePending = false;
        _dashboardLoadingClock.Reset();
        DashboardLoadingShell.StopWave();
        DashboardLoadingShell.BeginAnimation(UIElement.OpacityProperty, null);
        DashboardLoadingShell.Opacity = 1;
        DashboardLoadingLayer.BeginAnimation(UIElement.OpacityProperty, null);
        DashboardLoadingLayer.Opacity = 1;
        DashboardLoadingLayer.Visibility = Visibility.Collapsed;
    }

    private void CancelLoadingCallbacks()
    {
        if (_loadingRenderHandler is not null)
            CompositionTarget.Rendering -= _loadingRenderHandler;
        _loadingRenderHandler = null;
        _loadingFinishTimer?.Stop();
        _loadingFinishTimer = null;
    }
}
