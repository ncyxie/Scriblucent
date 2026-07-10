using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.ViewModels;

namespace Scriblucent.Behaviors;

/// <summary>
/// Mass-select card chrome on dashboard listing surfaces.
/// Applies background, border brush, and thickness imperatively so recycled virtual
/// containers never keep stale trigger binding state.
/// </summary>
public static class MassSelectHighlightBehavior
{
    private const double NormalBorderThickness = 1.0;
    private const double SelectedBorderThickness = 2.0;

    private sealed class State
    {
        public INotifyPropertyChanged? Item;
        public PropertyChangedEventHandler? ItemHandler;
        public bool LastSelected;
        public object AnimationKey = new();
        public bool LifecycleHandlersAttached;
    }

    private static MainViewModel? _watchedViewModel;
    private static bool _isSyncingAllGrids;
    private static bool _allGridsSyncScheduled;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(MassSelectHighlightBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MassSelectHighlightBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static void PrepareContainer(DependencyObject root)
    {
        foreach (var border in EnumerateEnabledBorders(root))
        {
            EnsureLifecycleHandlers(border);
            Attach(border);
        }

        SyncContainer(root);
        ScheduleDeferredContainerSync(root);
    }

    public static void ResetContainer(DependencyObject root)
    {
        foreach (var border in EnumerateEnabledBorders(root))
            ResetBorder(border);
    }

    public static void SyncContainer(DependencyObject root)
    {
        foreach (var border in EnumerateEnabledBorders(root))
            SyncBorder(border);
    }

    public static void RequestVisualSync(DependencyObject source)
    {
        if (FindItemTemplateRoot(source) is { } templateRoot)
            SyncContainer(templateRoot);
        else
            SyncContainer(source);

        if (FindAncestor<DashboardVirtualizedCardGrid>(source) is { } grid
            && Application.Current?.MainWindow?.DataContext is MainViewModel { IsMassSelectMode: true })
        {
            var container = FindItemTemplateRoot(source) ?? source;
            grid.SyncMassSelectContainer(container);
        }
    }

    public static void SyncAllDashboardGrids()
    {
        if (_isSyncingAllGrids)
            return;

        if (Application.Current?.MainWindow is not DependencyObject root)
            return;

        _isSyncingAllGrids = true;
        try
        {
            foreach (var grid in EnumerateDescendants<DashboardVirtualizedCardGrid>(root))
                grid.SyncMassSelectChrome();
        }
        finally
        {
            _isSyncingAllGrids = false;
        }
    }

    public static void ScheduleSyncAllDashboardGrids()
    {
        if (_allGridsSyncScheduled
            || Application.Current?.MainWindow is not FrameworkElement window)
            return;

        _allGridsSyncScheduled = true;
        window.Dispatcher.BeginInvoke(() =>
        {
            _allGridsSyncScheduled = false;
            SyncAllDashboardGrids();
        }, DispatcherPriority.Loaded);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        if ((bool)e.NewValue)
        {
            EnsureLifecycleHandlers(border);
            if (border.IsLoaded)
                Attach(border);
            return;
        }

        RemoveLifecycleHandlers(border);
        ResetBorder(border);
        border.ClearValue(StateProperty);
    }

    private static void EnsureLifecycleHandlers(Border border)
    {
        var state = GetState(border);
        if (state.LifecycleHandlersAttached)
            return;

        border.Loaded += Border_Loaded;
        border.Unloaded += Border_Unloaded;
        border.DataContextChanged += Border_DataContextChanged;
        state.LifecycleHandlersAttached = true;
    }

    private static void RemoveLifecycleHandlers(Border border)
    {
        var state = GetState(border);
        if (!state.LifecycleHandlersAttached)
            return;

        border.Loaded -= Border_Loaded;
        border.Unloaded -= Border_Unloaded;
        border.DataContextChanged -= Border_DataContextChanged;
        state.LifecycleHandlersAttached = false;
    }

    private static void Border_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
            Attach(border);
    }

    private static void Border_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;

        var state = GetState(border);
        UiMotion.Stop(state.AnimationKey);
        DetachItemHandler(state);
    }

    private static void Border_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border border || !GetIsEnabled(border))
            return;

        AttachItemHandler(border, GetState(border));
        SyncBorder(border);
    }

    private static void Attach(Border border)
    {
        EnsureWatchingMainViewModel();
        AttachItemHandler(border, GetState(border));

        if (border.IsLoaded)
            SyncBorder(border);
    }

    private static void AttachItemHandler(Border border, State state)
    {
        DetachItemHandler(state);

        if (ResolveItemDataContext(border) is not INotifyPropertyChanged item)
            return;

        state.Item = item;
        state.ItemHandler = (_, e) => OnItemPropertyChanged(border, e);
        state.Item.PropertyChanged += state.ItemHandler;
    }

    private static void DetachItemHandler(State state)
    {
        if (state.Item is not null && state.ItemHandler is not null)
            state.Item.PropertyChanged -= state.ItemHandler;

        state.Item = null;
        state.ItemHandler = null;
    }

    private static void ResetBorder(Border border)
    {
        var state = GetState(border);
        UiMotion.Stop(state.AnimationKey);
        DetachItemHandler(state);
        state.LastSelected = false;

        if (IsMassSelectActive(border))
            ApplySelectionVisual(border, selected: false, animate: false);
        else
            ClearSelectionLocalValues(border);
    }

    private static void SyncBorder(Border border)
    {
        var dataContext = ResolveItemDataContext(border);
        var selected = IsMassSelectActive(border) && ReadIsSelectedInMassSelect(dataContext);
        var state = GetState(border);
        state.LastSelected = selected;

        if (selected && IsMassSelectActive(border))
            EnsureBorderVisible(border);

        ApplySelectionVisual(border, selected, animate: false);
    }

    private static void OnItemPropertyChanged(Border border, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteCardViewModel.IsSelectedInMassSelect)
            && e.PropertyName != nameof(FlashcardSetViewModel.IsSelectedInMassSelect)
            && e.PropertyName != nameof(MindMapViewModel.IsSelectedInMassSelect)
            && e.PropertyName != nameof(QuizViewModel.IsSelectedInMassSelect))
            return;

        if (!IsMassSelectActive(border))
            return;

        var selected = ReadIsSelectedInMassSelect(ResolveItemDataContext(border));
        var state = GetState(border);
        if (state.LastSelected == selected)
            return;

        state.LastSelected = selected;
        EnsureBorderVisible(border);
        ApplySelectionVisual(border, selected, animate: true);
        DashboardMassSelectCheckBoxBehavior.SyncContainer(border);
    }

    private static void EnsureWatchingMainViewModel()
    {
        if (Application.Current?.MainWindow?.DataContext is not MainViewModel mainViewModel)
            return;

        if (ReferenceEquals(_watchedViewModel, mainViewModel))
            return;

        if (_watchedViewModel is not null)
            _watchedViewModel.PropertyChanged -= WatchedViewModel_PropertyChanged;

        _watchedViewModel = mainViewModel;
        _watchedViewModel.PropertyChanged += WatchedViewModel_PropertyChanged;
    }

    private static void WatchedViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsMassSelectMode))
            return;

        ScheduleSyncAllDashboardGrids();
    }

    private static void ApplySelectionVisual(Border border, bool selected, bool animate)
    {
        if (!IsMassSelectActive(border))
        {
            StopThicknessAnimation(border);
            ClearSelectionLocalValues(border);
            return;
        }

        var (normalBackground, normalBorderBrush) = GetNormalBrushes(border);
        var selectedBackground = GetThemeBrush(border, "NoteCardSelection");
        var selectedBorderBrush = GetThemeBrush(border, "NoteCardSelectionBorder");

        border.Background = selected ? selectedBackground ?? normalBackground : normalBackground;
        border.BorderBrush = selected ? selectedBorderBrush ?? normalBorderBrush : normalBorderBrush;

        var targetThickness = selected ? SelectedBorderThickness : NormalBorderThickness;
        var currentThickness = border.BorderThickness.Left;

        if (!border.IsLoaded || !animate || UiMotion.PrefersReducedMotion)
        {
            StopThicknessAnimation(border);
            SetBorderThickness(border, targetThickness);
            return;
        }

        if (Math.Abs(currentThickness - targetThickness) < 0.01)
            return;

        StopThicknessAnimation(border);
        var animationKey = GetState(border).AnimationKey;
        UiMotion.Run(
            animationKey,
            new[]
            {
                UiMotion.Track(
                    UiMotion.QuickMs,
                    UiMotion.OutCubic,
                    currentThickness,
                    targetThickness,
                    value => SetBorderThickness(border, value))
            });
    }

    private static void ClearSelectionLocalValues(Border border)
    {
        StopThicknessAnimation(border);
        border.ClearValue(Border.BackgroundProperty);
        border.ClearValue(Border.BorderBrushProperty);
        border.ClearValue(Border.BorderThicknessProperty);
    }

    private static (Brush? Background, Brush? BorderBrush) GetNormalBrushes(Border border)
    {
        if (FindAncestor<NoteCardControl>(border) is not null)
        {
            return (
                GetThemeBrush(border, "NoteCardBackground"),
                GetThemeBrush(border, "NoteCardBorder"));
        }

        return (
            GetThemeBrush(border, "CardBackground"),
            GetThemeBrush(border, "BorderColor"));
    }

    private static Brush? GetThemeBrush(FrameworkElement element, string resourceKey)
        => element.TryFindResource(resourceKey) as Brush;

    private static void EnsureBorderVisible(Border border)
    {
        if (border.Opacity >= 0.99 || DashboardMotionEngine.ShouldDeferForcedVisibility(border))
            return;

        UiMotion.Stop(border);
        border.Opacity = 1;

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(border);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private static void SetBorderThickness(Border border, double thickness)
        => border.BorderThickness = new Thickness(thickness);

    private static void StopThicknessAnimation(Border border)
        => UiMotion.Stop(GetState(border).AnimationKey);

    private static bool IsMassSelectActive(Border border)
        => _watchedViewModel?.IsMassSelectMode == true
           || Window.GetWindow(border)?.DataContext is MainViewModel { IsMassSelectMode: true };

    private static bool ReadIsSelectedInMassSelect(object? dataContext)
        => dataContext switch
        {
            NoteCardViewModel note => note.IsSelectedInMassSelect,
            FlashcardSetViewModel set => set.IsSelectedInMassSelect,
            MindMapViewModel map => map.IsSelectedInMassSelect,
            QuizViewModel quiz => quiz.IsSelectedInMassSelect,
            _ => false
        };

    private static object? ResolveItemDataContext(DependencyObject element)
    {
        for (var current = element; current is not null; current = GetParent(current))
        {
            if (current is not FrameworkElement framework)
                continue;

            if (IsListingViewModel(framework.Tag))
                return framework.Tag;

            if (IsListingViewModel(framework.DataContext))
                return framework.DataContext;
        }

        return null;
    }

    private static bool IsListingViewModel(object? value)
        => value is NoteCardViewModel or FlashcardSetViewModel or MindMapViewModel or QuizViewModel;

    private static void ScheduleDeferredContainerSync(DependencyObject root)
    {
        if (root is not FrameworkElement element)
            return;

        element.Dispatcher.BeginInvoke(() => SyncContainer(root), DispatcherPriority.Loaded);
    }

    private static State GetState(Border border)
    {
        if (border.GetValue(StateProperty) is State existing)
            return existing;

        var created = new State();
        border.SetValue(StateProperty, created);
        return created;
    }

    private static DependencyObject? FindItemTemplateRoot(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (GetParent(current) is Panel { IsItemsHost: true })
                return current;

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
        => VisualTreeHelper.GetParent(current) as DependencyObject
           ?? LogicalTreeHelper.GetParent(current) as DependencyObject;

    private static IEnumerable<Border> EnumerateEnabledBorders(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border && GetIsEnabled(border))
                yield return border;

            foreach (var nested in EnumerateEnabledBorders(child))
                yield return nested;
        }
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var nested in EnumerateDescendants<T>(child))
                yield return nested;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = GetParent(current);
        }

        return null;
    }
}