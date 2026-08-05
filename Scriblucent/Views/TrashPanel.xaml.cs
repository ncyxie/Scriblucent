using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Scriblucent.Views;

public partial class TrashPanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;
    private MainViewModel? _boundViewModel;
    private TrashItemKind? _activeFilter;
    private string _searchQuery = string.Empty;
    private bool _isApplyingFilterLabels;

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
                _boundViewModel.TrashedItems.CollectionChanged -= TrashedItems_CollectionChanged;

            _boundViewModel = viewModel;
            _boundViewModel.TrashedItems.CollectionChanged += TrashedItems_CollectionChanged;
        }

        viewModel.RefreshTrashedItems();
        UpdateFilterChipLabels();
        ApplyFilters();
    }

    private void TrashedItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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

        IEnumerable<TrashItemViewModel> items = _boundViewModel.TrashedItems;
        if (_activeFilter is { } kind)
            items = items.Where(item => item.Kind == kind);

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            items = items.Where(item =>
                item.Title.Contains(_searchQuery, StringComparison.CurrentCultureIgnoreCase)
                || item.KindDisplayName.Contains(_searchQuery, StringComparison.CurrentCultureIgnoreCase));
        }

        var filtered = items.ToList();
        TrashedItemsListBox.ItemsSource = filtered;

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
        => UpdateSelectionActions();

    private void UpdateSelectionActions()
    {
        var hasSelection = TrashedItemsListBox.SelectedItems.Count > 0;
        RestoreSelectedButton.IsEnabled = hasSelection;
        DeleteSelectedButton.IsEnabled = hasSelection;
    }

    private IReadOnlyList<TrashItemViewModel> GetSelectedItems()
        => TrashedItemsListBox.SelectedItems.OfType<TrashItemViewModel>().ToList();

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
