using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class FlashcardsPreviewWindow
{
    public EditorSelectionController BulkSelection { get; } = new();
    private bool _bulkFlashcardEdit;

    private void InitializeBulkSelection()
    {
        BulkSelection.Starting += PrepareBulkSelection;
        BulkSelection.Changed += SyncBulkSelectionChrome;
        BulkSelectionBar.Initialize(BulkSelection, ConfirmDeleteSelectedFlashcards, BuildFlashcardSelectionMenu, DuplicateSelectedFlashcards);
        LocalizationService.CultureChanged += BulkSelectionCultureChanged;
        Closed += (_, _) =>
        {
            BulkSelection.End(); BulkSelectionBar.Dispose();
            BulkSelection.Starting -= PrepareBulkSelection;
            BulkSelection.Changed -= SyncBulkSelectionChrome;
            LocalizationService.CultureChanged -= BulkSelectionCultureChanged;
        };
        BulkSelection.SetItems(_items.Select(item => item.Id));
        SyncBulkSelectionChrome();
    }
    private void BulkSelectionCultureChanged(object? sender, EventArgs e) => BulkSelection.RefreshText();
    private void PrepareBulkSelection()
    {
        CommitWorkspaceEdits();
        _audioPlayer?.Stop();
        if (_selectedItem is not null) _selectedItem.IsSelected = false;
        _selectedItem = null;
    }
    private void SyncBulkSelectionChrome()
    {
        BulkSelection.SetAvailable(!_isStudyMode && !_isCramMode);
        StartStudyModeButton.IsEnabled = !BulkSelection.IsActive && _items.Count > 0 && !_isCramMode;
        CramModeButton.IsEnabled = !BulkSelection.IsActive && (_isCramMode || (!_isStudyMode && _allItems.Any(item => item.SetIndex == _currentSetIndex)));
        AddCardToolButton.IsEnabled = ImportCardsButton.IsEnabled = true;
        ShuffleModeButton.IsEnabled = !BulkSelection.IsActive && _items.Count >= 2;
        QuestionAnswerToggleButton.IsEnabled = !BulkSelection.IsActive && _items.Count > 0;
    }
    private void SelectFlashcardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menu && ResolveFlashcardFromMenuItem(menu) is { } item) { BulkSelection.Begin(item.Id); BulkSelectionBar.FocusOnEntry(); }
    }
    private FlashcardPreviewItem[] SelectedFlashcards()
    {
        var ids = BulkSelection.SelectedIds.ToHashSet();
        return _allItems.Where(item => ids.Contains(item.Id)).ToArray();
    }
    private static MenuItem SelectionAction(string key, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = LocalizationService.GetString(key), IsEnabled = enabled };
        item.Click += (_, e) => { action(); e.Handled = true; };
        return item;
    }
    private ContextMenu BuildFlashcardSelectionMenu()
    {
        var menu = new ContextMenu();
        var selected = SelectedFlashcards();
        var move = new MenuItem { Header = LocalizationService.GetString("MoveToSet") };
        foreach (var set in _setOptions.OrderBy(set => set.SetIndex))
        {
            int target = set.SetIndex;
            var item = new MenuItem { Header = set.DisplayName, IsEnabled = selected.Any(card => card.SetIndex != target) };
            item.Click += (_, _) => MoveSelectedFlashcards(target);
            move.Items.Add(item);
        }
        move.Items.Add(new Separator());
        move.Items.Add(SelectionAction("CreateFlashcardSetAndMove", () =>
        {
            if (BulkSelection.HasSelection && TryCreateSet(out var created, false, markEdited: false)) MoveSelectedFlashcards(created.SetIndex);
        }));
        menu.Items.Add(move);
        var category = new MenuItem { Header = LocalizationService.GetString("EditorChangeCategory") };
        foreach (var value in GetCategoryOptions())
        {
            var item = new MenuItem { Header = value };
            item.Click += (_, _) => ChangeSelectedFlashcardCategory(value);
            category.Items.Add(item);
        }
        if (category.Items.Count > 0) category.Items.Add(new Separator());
        category.Items.Add(SelectionAction("EditorCustomCategory", () =>
        {
            var dialog = new SimpleInputDialog(LocalizationService.GetString("EditorChangeCategory"),
                LocalizationService.GetString("EditorCategoryPrompt"), string.Empty) { Owner = WorkspaceOwner };
            if (dialog.ShowDialog() == true) ChangeSelectedFlashcardCategory(dialog.InputText);
        }));
        category.Items.Add(SelectionAction("EditorClearCategory", () => ChangeSelectedFlashcardCategory(string.Empty)));
        menu.Items.Add(category);
        menu.Items.Add(SelectionAction("EditorStarSelected", () => StarSelectedFlashcards(true), selected.Any(card => !card.IsStarred)));
        menu.Items.Add(SelectionAction("EditorUnstarSelected", () => StarSelectedFlashcards(false), selected.Any(card => card.IsStarred)));
        menu.Items.Add(new Separator());
        menu.Items.Add(SelectionAction("SelectionExportSelected", async () => await ExportSelectedContentAsync()));
        return menu;
    }
    private async Task ExportSelectedContentAsync()
    {
        var ids = BulkSelection.SelectedIds.ToHashSet(); if (ids.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.GetString("SelectionExportSelected"),
            Filter = LocalizationService.GetString("FlashcardExportDialogFilter"),
            FileName = SelectionExportService.SafeName(GetExportFileName()) + " - " + LocalizationService.GetString("SelectionExportSuffix"), OverwritePrompt = true
        };
        if (dialog.ShowDialog(WorkspaceOwner) != true) return;
        await BulkSelectionBar.RunOperationAsync(async (token, progress) =>
        {
            CommitWorkspaceEdits(); var snapshot = SelectionExportService.Subset(ToDocument(), ids);
            await SelectionExportService.ExportDocumentAsync(snapshot, dialog.FileName, token);
        });
    }
    private void CommitFlashcardBulk(Action mutation, IEnumerable<Guid>? selectCopies = null)
    {
        _bulkFlashcardEdit = true;
        try { mutation(); InitializeCategoryFilterOptions(); }
        finally { _bulkFlashcardEdit = false; }
        ApplyFilters(preserveStudyPosition: true);
        if (selectCopies is not null) BulkSelection.SelectOnly(selectCopies);
        UpdateEditedIndicator();
    }
    internal void MoveSelectedFlashcards(int target)
    {
        if (!_setOptions.Any(set => set.SetIndex == target)) return;
        var selected = SelectedFlashcards();
        if (!selected.Any(item => item.SetIndex != target)) return;
        CommitFlashcardBulk(() => { foreach (var card in selected) card.SetIndex = target; });
    }
    internal void ChangeSelectedFlashcardCategory(string category)
    {
        category = category.Trim();
        var selected = SelectedFlashcards();
        if (!selected.Any(card => card.Category != category)) return;
        CommitFlashcardBulk(() => { foreach (var card in selected) card.Category = category; });
    }
    internal void StarSelectedFlashcards(bool starred)
    {
        var selected = SelectedFlashcards();
        if (!selected.Any(card => card.IsStarred != starred)) return;
        CommitFlashcardBulk(() => { foreach (var card in selected) card.IsStarred = starred; });
    }
    internal void DuplicateSelectedFlashcards()
        => DuplicateFlashcards(BulkSelection.SelectedIds.ToHashSet());

    private void DuplicateFlashcardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || ResolveFlashcardFromMenuItem(menu) is not { } card) return;
        DuplicateFlashcards(BulkSelection.IsActive && BulkSelection.Contains(card.Id)
            ? BulkSelection.SelectedIds.ToHashSet() : new HashSet<Guid> { card.Id });
        e.Handled = true;
    }
    private bool DuplicateCurrentFlashcards(DependencyObject? source)
    {
        if (_isStudyMode || _isCramMode) return false;
        if (BulkSelection.IsActive)
        {
            if (!BulkSelection.HasSelection) return false;
            DuplicateSelectedFlashcards(); return true;
        }
        var card = source is not null && CollectionSelectionFrame.For(source)?.DataContext is FlashcardPreviewItem focused
            ? focused : _selectedItem;
        if (card is null || !_items.Contains(card)) return false;
        DuplicateFlashcards(new HashSet<Guid> { card.Id }); return true;
    }
    private void DuplicateFlashcards(IReadOnlySet<Guid> selected)
    {
        if (_isStudyMode || _isCramMode || selected.Count == 0) return;
        CommitWorkspaceEdits();
        bool bulk = BulkSelection.IsActive;
        var baseline = CardOrderForEdit();
        var result = new List<FlashcardPreviewItem>(_allItems.Count + selected.Count);
        var copies = new List<FlashcardPreviewItem>(selected.Count);
        foreach (var card in baseline)
        {
            result.Add(card);
            if (!selected.Contains(card.Id)) continue;
            var copy = new FlashcardPreviewItem(Guid.NewGuid(), card.Question, card.Answer, card.SetIndex,
                category: card.Category, hint: card.Hint, questionImageFileName: card.QuestionImageFileName,
                questionAudioFileName: card.QuestionAudioFileName, answerImageFileName: card.AnswerImageFileName,
                answerAudioFileName: card.AnswerAudioFileName, isStarred: card.IsStarred);
            copies.Add(copy); result.Add(copy);
        }
        CommitFlashcardBulk(() =>
        {
            foreach (var pair in copies.Zip(baseline.Where(card => selected.Contains(card.Id))))
                _cardNoteLinks[pair.First.Id] = _cardNoteLinks.GetValueOrDefault(pair.Second.Id);
            _allItems.Clear(); _allItems.AddRange(result);
            // A fresh single copy must remain visible instead of disappearing under a maturity filter.
            if (!bulk && _statusFilterMaturity is CardMaturity.Learning or CardMaturity.Mature) StatusFilterComboBox.SelectedIndex = 0;
        }, bulk ? copies.Select(card => card.Id) : null);
        if (!bulk && copies.FirstOrDefault(copy => _items.Contains(copy)) is { } first)
        {
            SelectFlashcard(first);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                () => EditorSelectionInput.RevealItem(FlashcardsItemsControl, first));
        }
        int hiddenCopies = copies.Count(copy => !_items.Contains(copy));
        if (hiddenCopies > 0) BulkSelection.ReportStatus(string.Format(LocalizationService.GetString("EditorCopiesHiddenByFilters"), hiddenCopies));
    }
    private void ConfirmDeleteSelectedFlashcards()
    {
        var ids = SelectedFlashcards().Select(card => card.Id).ToHashSet();
        if (ids.Count == 0 || !ModernDialog.ConfirmDanger(WorkspaceOwner, LocalizationService.GetString("MassSelectDeleteSelected"),
            string.Format(LocalizationService.GetString("EditorDeleteSelectedConfirmation"), ids.Count), LocalizationService.GetString("Delete"))) return;
        DeleteSelectedFlashcards(ids);
    }
    internal void DeleteSelectedFlashcards(IReadOnlySet<Guid> ids)
    {
        if (ids.Count == 0) return;
        CommitFlashcardBulk(() =>
        {
            _allItems.RemoveAll(card => ids.Contains(card.Id));
            for (int i = _studyHistory.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(_studyHistory[i].Id)) continue;
                _studyHistory.RemoveAt(i);
                if (_studyHistoryPosition >= i) _studyHistoryPosition--;
            }
            _studyHistoryPosition = Math.Max(-1, _studyHistoryPosition);
            _sessionKnownCardIds.ExceptWith(ids);
            foreach (var id in ids) { _cardNoteLinks.Remove(id); _cramStepsRemaining.Remove(id); }
            if (_activeAudioFlashcard is { } playing && ids.Contains(playing.Id)) { _audioPlayer?.Stop(); _activeAudioFlashcard = null; }
            if (_selectedItem is { } active && ids.Contains(active.Id)) { active.IsSelected = false; _selectedItem = null; }
        });
    }
}
