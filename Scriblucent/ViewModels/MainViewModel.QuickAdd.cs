using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Scriblucent.Services;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    private ICommand? _quickAddDashboardItemCommand;
    private readonly Dictionary<EditorDocumentKind, HashSet<Guid>> _pendingQuickAdds = new();
    private readonly HashSet<EditorDocumentKind> _queuedQuickAddSaves = new();
    public event EventHandler? QuickAddFailed;
    public bool CanCreateDashboardItem => IsActiveDashboardReady && !IsActiveDashboardLoadFailed;

    public ICommand QuickAddDashboardItemCommand => _quickAddDashboardItemCommand ??= new RelayCommand(
        QuickAddDashboardItem, () => CanCreateDashboardItem);

    private void QuickAddDashboardItem()
    {
        // Guard Execute as well as CanExecute: automation and queued input can outlive a page.
        if (!QuickAddDashboardItemCommand.CanExecute(null)) return;
        var kind = IsFlashcardsView ? EditorDocumentKind.Flashcards : IsMindMapsView ? EditorDocumentKind.MindMap
            : IsQuizzesView ? EditorDocumentKind.Quiz : EditorDocumentKind.Note;
        Guid id;
        switch (kind)
        {
            case EditorDocumentKind.Note:
                CreateNoteForEditor(quickAdd: true); // Existing coalesced, asynchronous notes writer.
                return;
            case EditorDocumentKind.Flashcards:
                id = AddOrUpdateFlashcardSet(NewDocumentDefaults.Flashcards(), persist: false).Document.Id;
                break;
            case EditorDocumentKind.MindMap:
                id = AddOrUpdateMindMap(NewDocumentDefaults.MindMap(), persist: false).Document.Id;
                break;
            default:
                id = AddOrUpdateQuiz(NewDocumentDefaults.Quiz(), persist: false).Document.Id;
                break;
        }
        TrackQuickAdd(kind, id);
        // Batch input in the same dispatcher turn; keep the existing atomic store writers.
        if (!_queuedQuickAddSaves.Add(kind)) return;
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(() =>
        {
            _queuedQuickAddSaves.Remove(kind);
            var ids = PendingQuickAddIds(kind);
            bool saved = kind switch
            {
                EditorDocumentKind.Flashcards => TrySaveFlashcardSets(),
                EditorDocumentKind.MindMap => TrySaveMindMaps(),
                _ => TrySaveQuizzes()
            };
            CompleteQuickAddSave(kind, ids, saved);
        }, DispatcherPriority.Background);
    }

    private void TrackQuickAdd(EditorDocumentKind kind, Guid id)
    {
        if (!_pendingQuickAdds.TryGetValue(kind, out var ids)) _pendingQuickAdds[kind] = ids = new();
        ids.Add(id);
    }

    private Guid[] PendingQuickAddIds(EditorDocumentKind kind)
        => _pendingQuickAdds.TryGetValue(kind, out var ids) ? ids.ToArray() : [];

    private void CompleteQuickAddSave(EditorDocumentKind kind, Guid[] ids, bool saved)
    {
        if (ids.Length == 0) return;
        if (_pendingQuickAdds.TryGetValue(kind, out var pending)) pending.ExceptWith(ids);
        if (saved) return;
        // Roll back only this failed batch. Existing library documents are never removed.
        var failed = ids.ToHashSet();
        switch (kind)
        {
            case EditorDocumentKind.Note:
                foreach (var item in Notes.Where(n => failed.Contains(n.Document.Id)).ToArray()) Notes.Remove(item);
                RefreshAvailableTags(); ApplyFilters(); NotifyNotesChanged(); RefreshRecentNotes();
                break;
            case EditorDocumentKind.Flashcards:
                foreach (var item in FlashcardSets.Where(n => failed.Contains(n.Document.Id)).ToArray()) FlashcardSets.Remove(item);
                RebuildFlashcardSetGroups(); RefreshAvailableFlashcardTags(); ApplyFlashcardFilters();
                NotifyFlashcardSetsChanged(); RefreshRecentFlashcardSets();
                break;
            case EditorDocumentKind.MindMap:
                foreach (var item in MindMaps.Where(n => failed.Contains(n.Document.Id)).ToArray()) MindMaps.Remove(item);
                RebuildMindMapGroups(); RefreshAvailableMindMapTags(); ApplyMindMapFilters();
                NotifyMindMapsChanged(); RefreshRecentMindMaps();
                break;
            case EditorDocumentKind.Quiz:
                foreach (var item in Quizzes.Where(n => failed.Contains(n.Document.Id)).ToArray()) Quizzes.Remove(item);
                RebuildQuizGroups(); RefreshAvailableQuizTags(); ApplyQuizFilters();
                NotifyQuizzesChanged(); RefreshRecentQuizzes();
                break;
        }
        QuickAddFailed?.Invoke(this, EventArgs.Empty);
    }
}
