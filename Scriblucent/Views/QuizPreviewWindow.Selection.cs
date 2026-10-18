using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class QuizPreviewWindow
{
    public EditorSelectionController BulkSelection { get; } = new();
    private bool _bulkQuestionEdit;
    private readonly HashSet<QuizPreviewQuestion> _trackedQuestions = [];

    private void InitializeBulkSelection()
    {
        BulkSelection.Starting += PrepareBulkSelection;
        BulkSelection.Changed += SyncBulkSelectionChrome;
        BulkSelectionBar.Initialize(BulkSelection, ConfirmDeleteSelectedQuestions, BuildQuestionSelectionMenu, DuplicateSelectedQuestions);
        LocalizationService.CultureChanged += BulkSelectionCultureChanged;
        Closed += (_, _) =>
        {
            BulkSelection.End(); BulkSelectionBar.Dispose();
            BulkSelection.Starting -= PrepareBulkSelection;
            BulkSelection.Changed -= SyncBulkSelectionChrome;
            LocalizationService.CultureChanged -= BulkSelectionCultureChanged;
            foreach (var question in _trackedQuestions.ToArray()) DetachQuestionChangeTracking(question);
            _questions.CollectionChanged -= Questions_CollectionChanged;
        };
        BulkSelection.SetItems(_questions.Select(question => question.Id));
        SyncBulkSelectionChrome();
    }
    private void BulkSelectionCultureChanged(object? sender, EventArgs e) => BulkSelection.RefreshText();
    private void PrepareBulkSelection()
    {
        CommitWorkspaceEdits();
        foreach (var question in _questions.Where(question => question.IsFocused)) question.IsFocused = false;
    }
    private void SyncBulkSelectionChrome()
    {
        BulkSelection.SetAvailable(IsEditMode);
        AddQuestionButton.IsEnabled = ImportQuestionsButton.IsEnabled = true;
        StartQuizButton.IsEnabled = QuizOptionsButton.IsEnabled = !BulkSelection.IsActive;
        ShuffleQuestionsButton.IsEnabled = !BulkSelection.IsActive && _questions.Count >= 2;
    }
    private void SelectQuestionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: CollectionSelectionFrame frame } }) { BulkSelection.Begin(frame.ItemId); BulkSelectionBar.FocusOnEntry(); }
    }
    private HashSet<Guid> SelectedQuestionIds() => BulkSelection.SelectedIds.ToHashSet();
    private ContextMenu BuildQuestionSelectionMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(SelectionAction("EditorMoveSelectedUp", () => MoveSelectedQuestions(false), CanMoveSelectedQuestions(false)));
        menu.Items.Add(SelectionAction("EditorMoveSelectedDown", () => MoveSelectedQuestions(true), CanMoveSelectedQuestions(true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(SelectionAction("SelectionExportSelected", async () => await ExportSelectedContentAsync()));
        return menu;
    }
    private static MenuItem SelectionAction(string key, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = LocalizationService.GetString(key), IsEnabled = enabled };
        item.Click += (_, e) => { action(); e.Handled = true; };
        return item;
    }
    private async Task ExportSelectedContentAsync()
    {
        var ids = BulkSelection.SelectedIds.ToHashSet(); if (ids.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.GetString("SelectionExportSelected"),
            Filter = LocalizationService.GetString("QuizExportDialogFilter"),
            FileName = SelectionExportService.SafeName(GetExportFileName()) + " - " + LocalizationService.GetString("SelectionExportSuffix"), OverwritePrompt = true
        };
        if (dialog.ShowDialog(WorkspaceOwner) != true) return;
        await BulkSelectionBar.RunOperationAsync(async (token, progress) =>
        {
            CommitWorkspaceEdits(); var snapshot = SelectionExportService.Subset(ToDocument(), ids);
            var warnings = await SelectionExportService.ExportDocumentAsync(snapshot, dialog.FileName, token, (path, token) => QuizPrintableExport.SaveAsync((QuizDocument)snapshot, path, token));
            if (warnings.Count > 0) ModernDialog.ShowInfo(WorkspaceOwner, LocalizationService.GetString("Success"),
                LocalizationService.GetString("SelectionExportComplete") + "\n\n" + string.Join("\n", warnings));
        });
    }
    private void CommitQuestionBulk(IReadOnlyList<QuizPreviewQuestion> result, IEnumerable<Guid>? selectCopies = null)
    {
        _bulkQuestionEdit = true;
        try
        {
            _isSubmitted = false;
            _questions.ReplaceAll(result);
            RenumberQuestions();
            foreach (var question in _questions)
                if (question.IsSubmitted || question.HasSelection) question.Reset();
        }
        finally { _bulkQuestionEdit = false; }
        _questionsChangeVersion++;
        BulkSelection.SetItems(_questions.Select(question => question.Id));
        if (selectCopies is not null) BulkSelection.SelectOnly(selectCopies);
        UpdateSummary(); UpdateEditedIndicator(); SyncBulkSelectionChrome();
    }
    internal void DuplicateSelectedQuestions()
    {
        var selected = SelectedQuestionIds();
        if (selected.Count == 0) return;
        var result = new List<QuizPreviewQuestion>(_questions.Count + selected.Count);
        var copies = new List<Guid>();
        foreach (var question in _questions)
        {
            result.Add(question);
            if (!selected.Contains(question.Id)) continue;
            var model = question.ToModel();
            model.Id = Guid.NewGuid(); model.LastAttemptCorrect = null;
            var copy = new QuizPreviewQuestion(0, model);
            result.Add(copy); copies.Add(copy.Id);
        }
        CommitQuestionBulk(result, copies);
    }
    internal bool CanMoveSelectedQuestions(bool down)
    {
        var selected = SelectedQuestionIds();
        var indices = _questions.Select((item, index) => (item, index)).Where(pair => selected.Contains(pair.item.Id)).Select(pair => pair.index).ToArray();
        return indices.Length > 0 && (down ? indices.Max() < _questions.Count - 1 : indices.Min() > 0);
    }
    internal void MoveSelectedQuestions(bool down)
    {
        if (!CanMoveSelectedQuestions(down)) return;
        var selected = SelectedQuestionIds();
        var moving = _questions.Where(item => selected.Contains(item.Id)).ToArray();
        var positions = _questions.Select((item, index) => (item, index)).Where(pair => selected.Contains(pair.item.Id)).Select(pair => pair.index).ToArray();
        int index = down ? positions.Max() + 1 : positions.Min() - 1;
        if (index >= 0 && index < _questions.Count)
            Scriblucent.Animations.ReorderMotion.Commit(QuestionsItemsControl, () => TryReorderQuestions(moving, _questions[index], down));
    }
    private void ConfirmDeleteSelectedQuestions()
    {
        var ids = SelectedQuestionIds();
        if (ids.Count == 0 || !ModernDialog.ConfirmDanger(WorkspaceOwner, LocalizationService.GetString("MassSelectDeleteSelected"),
            string.Format(LocalizationService.GetString("EditorDeleteSelectedConfirmation"), ids.Count), LocalizationService.GetString("Delete"))) return;
        DeleteSelectedQuestions(ids);
    }
    internal void DeleteSelectedQuestions(IReadOnlySet<Guid> ids)
    {
        if (!_questions.Any(question => ids.Contains(question.Id))) return;
        var result = _questions.Where(question => !ids.Contains(question.Id)).ToList();
        if (result.Count == 0) result.Add(new QuizPreviewQuestion(1, CreateDefaultQuestion()));
        CommitQuestionBulk(result);
    }
}
