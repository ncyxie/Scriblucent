using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.ViewModels;

namespace Scriblucent.Behaviors;

/// <summary>
/// Keeps dashboard mass-select checkboxes hidden unless mass-select mode is active.
/// Routes checkbox clicks through MainViewModel toggles to avoid binding feedback loops.
/// </summary>
public static class DashboardMassSelectCheckBoxBehavior
{
    private static MainViewModel? _watchedViewModel;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DashboardMassSelectCheckBoxBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool IsMassSelectCheckBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox checkBox && GetIsEnabled(checkBox))
                return true;

            source = GetParent(source);
        }

        return false;
    }

    public static void SyncContainer(DependencyObject root)
    {
        foreach (var checkBox in EnumerateMassSelectCheckBoxes(root))
            SyncCheckBox(checkBox);
    }

    public static void HideAllInContainer(DependencyObject root)
    {
        foreach (var checkBox in EnumerateMassSelectCheckBoxes(root))
        {
            checkBox.Visibility = Visibility.Collapsed;
            checkBox.IsChecked = false;
            CheckBoxCheckMotionBehavior.SyncCheckBoxGlyph(checkBox);
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CheckBox checkBox)
            return;

        if ((bool)e.NewValue)
        {
            checkBox.Loaded += CheckBox_Loaded;
            checkBox.Unloaded += CheckBox_Unloaded;
            checkBox.Click += CheckBox_Click;
            EnsureWatchingMainViewModel();
            SyncCheckBox(checkBox);
            return;
        }

        checkBox.Loaded -= CheckBox_Loaded;
        checkBox.Unloaded -= CheckBox_Unloaded;
        checkBox.Click -= CheckBox_Click;
        checkBox.Visibility = Visibility.Collapsed;
        checkBox.IsChecked = false;
    }

    private static void CheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            EnsureWatchingMainViewModel();
            SyncCheckBox(checkBox);
        }
    }

    private static void CheckBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            checkBox.Visibility = Visibility.Collapsed;
            checkBox.IsChecked = false;
        }
    }

    private static void CheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || !GetIsEnabled(checkBox))
            return;

        e.Handled = true;

        if (Window.GetWindow(checkBox)?.DataContext is not MainViewModel mainVm || !mainVm.IsMassSelectMode)
            return;

        switch (ResolveItemDataContext(checkBox))
        {
            case NoteCardViewModel note:
                mainVm.ToggleMassSelectForNote(note);
                break;
            case FlashcardSetViewModel set:
                mainVm.ToggleMassSelectForDashboardItem(set);
                break;
            case MindMapViewModel map:
                mainVm.ToggleMassSelectForDashboardItem(map);
                break;
            case QuizViewModel quiz:
                mainVm.ToggleMassSelectForDashboardItem(quiz);
                break;
        }

        SyncCheckBox(checkBox);
        MassSelectHighlightBehavior.RequestVisualSync(checkBox);
    }

    private static void SyncCheckBox(CheckBox checkBox)
    {
        if (!GetIsEnabled(checkBox))
            return;

        checkBox.Visibility = IsMassSelectActive() ? Visibility.Visible : Visibility.Collapsed;

        if (!IsMassSelectActive())
        {
            checkBox.IsChecked = false;
            return;
        }

        checkBox.IsChecked = ResolveItemDataContext(checkBox) switch
        {
            NoteCardViewModel note => note.IsSelectedInMassSelect,
            FlashcardSetViewModel set => set.IsSelectedInMassSelect,
            MindMapViewModel map => map.IsSelectedInMassSelect,
            QuizViewModel quiz => quiz.IsSelectedInMassSelect,
            _ => false
        };

        CheckBoxCheckMotionBehavior.SyncCheckBoxGlyph(checkBox);
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
        if (e.PropertyName != nameof(MainViewModel.IsMassSelectMode)
            || sender is not MainViewModel mainViewModel
            || Application.Current?.MainWindow is not DependencyObject root)
            return;

        foreach (var checkBox in EnumerateMassSelectCheckBoxes(root))
            SyncCheckBox(checkBox);
    }

    private static bool IsMassSelectActive()
        => _watchedViewModel?.IsMassSelectMode == true
           || Application.Current?.MainWindow?.DataContext is MainViewModel { IsMassSelectMode: true };

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

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);

        return LogicalTreeHelper.GetParent(current);
    }

    private static IEnumerable<CheckBox> EnumerateMassSelectCheckBoxes(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CheckBox checkBox && GetIsEnabled(checkBox))
                yield return checkBox;

            foreach (var nested in EnumerateMassSelectCheckBoxes(child))
                yield return nested;
        }
    }
}