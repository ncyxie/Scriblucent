using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Views;

public partial class TrashPanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;
    private MainViewModel? _boundViewModel;
    private TrashItemKind? _activeFilter;
    private string _searchQuery = string.Empty;
    private bool _isApplyingFilterLabels;
    private bool _suppressSelectionChanged;
    private readonly HashSet<TrashItemViewModel> _logicalSelectionExclusions = new();

    public static readonly DependencyProperty IsLogicalAllSelectedProperty = DependencyProperty.Register(
        nameof(IsLogicalAllSelected), typeof(bool), typeof(TrashPanel), new PropertyMetadata(false));

    public bool IsLogicalAllSelected
    {
        get => (bool)GetValue(IsLogicalAllSelectedProperty);
        private set => SetValue(IsLogicalAllSelectedProperty, value);
    }

    public TrashPanel()
    {
        InitializeComponent();
        _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
        LocalizationProvider.Instance.PropertyChanged += LocalizationProvider_PropertyChanged;
        Unloaded += (_, _) => LocalizationProvider.Instance.PropertyChanged -= LocalizationProvider_PropertyChanged;
    }

    public void ShowAnimated()
    {
        _overlayAnimator.Show(
            () => _overlayAnimator.RunAfterFirstRender(PopulatePanelContent, DispatcherPriority.ApplicationIdle),
            () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
    }

    public void HideAnimated()
    {
        _overlayAnimator.Hide();
    }

    private void LocalizationProvider_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
            UpdateFilterChipLabels();
    }

    private void PopulatePanelContent()
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (!ReferenceEquals(_boundViewModel, viewModel))
        {
            if (_boundViewModel is not null)
                _boundViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _boundViewModel = viewModel;
            _boundViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        viewModel.RefreshTrashedItems();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.TrashedItems))
            return;
        UpdateFilterChipLabels();
        ApplyFilters();
    }

    private void UpdateFilterChipLabels()
    {
        if (_boundViewModel is null)
            return;

        _isApplyingFilterLabels = true;
        try
        {
            FilterAllRadio.Content = $"{LocalizationService.GetString("TrashFilterAll")} ({_boundViewModel.TrashItemCount})";
            FilterNotesRadio.Content = $"{LocalizationService.GetString("NotesDashboard")} ({_boundViewModel.TrashedNotes.Count})";
            FilterFlashcardsRadio.Content = $"{LocalizationService.GetString("FlashcardsDashboard")} ({_boundViewModel.TrashedFlashcardSets.Count})";
            FilterMindMapsRadio.Content = $"{LocalizationService.GetString("MindMapsDashboard")} ({_boundViewModel.TrashedMindMaps.Count})";
            FilterQuizzesRadio.Content = $"{LocalizationService.GetString("QuizzesDashboard")} ({_boundViewModel.TrashedQuizzes.Count})";
        }
        finally
        {
            _isApplyingFilterLabels = false;
        }
    }

    private void ApplyFilters()
    {
        if (_boundViewModel is null)
            return;

        var filtered = ((LazyTrashItemList)_boundViewModel.TrashedItems).Filter(_activeFilter, _searchQuery);
        _suppressSelectionChanged = true;
        IsLogicalAllSelected = false;
        ClearLogicalSelectionExclusions();
        TrashedItemsListBox.ItemsSource = filtered;
        _suppressSelectionChanged = false;

        var isEmpty = filtered.Count == 0;
        EmptyStateHost.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TrashedItemsListBox.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateText.Text = LocalizationService.GetString(
            _activeFilter is null && string.IsNullOrWhiteSpace(_searchQuery)
                ? "TrashEmptyState"
                : "TrashEmptyStateFiltered");

        EmptyTrashButton.IsEnabled = GetEmptyableCount() > 0;
        UpdateSelectionActions();
    }

    private int GetEmptyableCount()
    {
        if (_boundViewModel is null)
            return 0;

        return _activeFilter switch
        {
            TrashItemKind.Note => _boundViewModel.TrashedNotes.Count,
            TrashItemKind.FlashcardSet => _boundViewModel.TrashedFlashcardSets.Count,
            TrashItemKind.MindMap => _boundViewModel.TrashedMindMaps.Count,
            TrashItemKind.Quiz => _boundViewModel.TrashedQuizzes.Count,
            _ => _boundViewModel.TrashItemCount
        };
    }

    private void TrashFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingFilterLabels || sender is not RadioButton { Tag: string tag })
            return;

        _activeFilter = tag switch
        {
            "Note" => TrashItemKind.Note,
            "FlashcardSet" => TrashItemKind.FlashcardSet,
            "MindMap" => TrashItemKind.MindMap,
            "Quiz" => TrashItemKind.Quiz,
            _ => null
        };
        ApplyFilters();
    }

    private void TrashSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = TrashSearchBox.Text?.Trim() ?? string.Empty;
        ApplyFilters();
    }

    private void TrashedItemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
            return;
        if (IsLogicalAllSelected)
        {
            IsLogicalAllSelected = false;
            ClearLogicalSelectionExclusions();
        }
        UpdateSelectionActions();
    }

    private void TrashedItemsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsLogicalAllSelected || e.OriginalSource is not DependencyObject source)
            return;

        var container = ItemsControl.ContainerFromElement(TrashedItemsListBox, source) as ListBoxItem;
        if (container?.DataContext is not TrashItemViewModel item)
            return;

        for (var current = source; current is not null && !ReferenceEquals(current, container);
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
                return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            item.IsExcludedFromLogicalSelection = !_logicalSelectionExclusions.Remove(item);
            if (item.IsExcludedFromLogicalSelection)
                _logicalSelectionExclusions.Add(item);
            UpdateSelectionActions();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            IsLogicalAllSelected = false;
            ClearLogicalSelectionExclusions();
            TrashedItemsListBox.SelectedItem = item;
            UpdateSelectionActions();
            e.Handled = true;
        }
    }

    private void ClearLogicalSelectionExclusions()
    {
        foreach (var item in _logicalSelectionExclusions)
            item.IsExcludedFromLogicalSelection = false;
        _logicalSelectionExclusions.Clear();
    }

    private void UpdateSelectionActions()
    {
        var count = TrashedItemsListBox.Items.Count;
        var selected = IsLogicalAllSelected
            ? count - _logicalSelectionExclusions.Count : TrashedItemsListBox.SelectedItems.Count;
        RestoreSelectedButton.IsEnabled = selected > 0;
        DeleteSelectedButton.IsEnabled = selected > 0;
        SelectAllTrashButton.IsEnabled = count > 0 && selected < count;
    }

    private void SelectAllTrashButton_Click(object sender, RoutedEventArgs e)
        => SelectAllTrashItems();

    private void TrashedItemsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.A || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        SelectAllTrashItems();
    }

    private void SelectAllTrashItems()
    {
        if (TrashedItemsListBox.Items.Count < 10_000)
        {
            TrashedItemsListBox.SelectAll();
            return;
        }

        _suppressSelectionChanged = true;
        TrashedItemsListBox.SelectedItems.Clear();
        _suppressSelectionChanged = false;
        ClearLogicalSelectionExclusions();
        IsLogicalAllSelected = true;
        UpdateSelectionActions();
    }

    private IReadOnlyList<TrashItemViewModel> GetSelectedItems()
    {
        if (IsLogicalAllSelected && TrashedItemsListBox.ItemsSource is LazyTrashItemList allItems)
        {
            var excludedSources = _logicalSelectionExclusions
                .Select(item => item.Source)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            return allItems.ExcludeSources(excludedSources);
        }

        return TrashedItemsListBox.SelectedItems.OfType<TrashItemViewModel>().ToList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideAnimated();

    private void OverlayDim_MouseDown(object sender, MouseButtonEventArgs e) => HideAnimated();

    private void RestoreItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null || sender is not Button { Tag: TrashItemViewModel item })
            return;

        _boundViewModel.RestoreTrashItem(item);
    }

    private void DeleteItemPermanentlyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null || sender is not Button { Tag: TrashItemViewModel item })
            return;

        var confirmed = ModernDialog.ConfirmDanger(
            Window.GetWindow(this),
            LocalizationService.GetString("ConfirmDelete"),
            LocalizationService.GetString("TrashDeleteItemConfirmation"),
            LocalizationService.GetString("Confirm"));

        if (confirmed)
            _boundViewModel.PurgeTrashItemPermanently(item);
    }

    private void RestoreSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null)
            return;

        _boundViewModel.RestoreTrashItems(GetSelectedItems());
    }

    private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null)
            return;

        var selected = GetSelectedItems();
        if (selected.Count == 0)
            return;

        var confirmed = ModernDialog.ConfirmDanger(
            Window.GetWindow(this),
            LocalizationService.GetString("ConfirmDelete"),
            string.Format(LocalizationService.GetString("TrashDeleteSelectedConfirmation"), selected.Count),
            LocalizationService.GetString("Confirm"));

        if (confirmed)
            _boundViewModel.PurgeTrashItemsPermanently(selected);
    }

    private void EmptyTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null || GetEmptyableCount() == 0)
            return;

        var confirmationKey = _activeFilter is null
            ? "EmptyTrashConfirmation"
            : "EmptyTrashFilteredConfirmation";

        var confirmed = ModernDialog.ConfirmDanger(
            Window.GetWindow(this),
            LocalizationService.GetString("TrashEmptyAction"),
            LocalizationService.GetString(confirmationKey),
            LocalizationService.GetString("Confirm"));

        if (confirmed)
            _boundViewModel.EmptyTrash(_activeFilter);
    }
}
