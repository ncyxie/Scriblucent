using Scriblucent.Models;
using Scriblucent.Services;
using System.Text.Json;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    internal string? PersistenceDirectoryOverride { get; set; }
    internal bool NotesEditorLoadFailed => _dashboardLoadFailures.Contains(DashboardNotes);
    internal void EnsureEditorStoreLoaded(EditorDocumentKind kind) => EnsureDashboardPersistedContentLoaded(kind switch
    {
        EditorDocumentKind.Flashcards => DashboardFlashcards,
        EditorDocumentKind.Quiz => DashboardQuizzes,
        EditorDocumentKind.MindMap => DashboardMindMaps,
        _ => DashboardNotes
    });
    public Func<IReadOnlyList<EditorDocumentKey>, bool>? EditorRemovalGuard { get; set; }

    internal bool CommitEditorFlashcards(FlashcardSetDocument snapshot)
    {
        if (_dashboardLoadFailures.Contains(DashboardFlashcards)) return false;
        var original = FlashcardSets.FirstOrDefault(s => s.Document.Id == snapshot.Id);
        var previous = original is null ? null : EditorHosting.Clone(original.Document);
        snapshot.LastModified = DateTime.Now;
        var updated = AddOrUpdateFlashcardSet(snapshot, persist: false);
        if (TrySaveFlashcardSets()) return true;
        if (previous is not null) AddOrUpdateFlashcardSet(previous, persist: false);
        else
        {
            FlashcardSets.Remove(updated);
            RebuildFlashcardSetGroups(); RefreshAvailableFlashcardTags(); ApplyFlashcardFilters();
            NotifyFlashcardSetsChanged(); RefreshRecentFlashcardSets();
        }
        return false;
    }

    internal bool CommitEditorQuiz(QuizDocument snapshot)
    {
        if (_dashboardLoadFailures.Contains(DashboardQuizzes)) return false;
        var original = Quizzes.FirstOrDefault(s => s.Document.Id == snapshot.Id);
        var previous = original is null ? null : EditorHosting.Clone(original.Document);
        snapshot.LastModified = DateTime.Now;
        var updated = AddOrUpdateQuiz(snapshot, persist: false);
        if (TrySaveQuizzes()) return true;
        if (previous is not null) AddOrUpdateQuiz(previous, persist: false);
        else
        {
            Quizzes.Remove(updated);
            RebuildQuizGroups(); RefreshAvailableQuizTags(); ApplyQuizFilters();
            NotifyQuizzesChanged(); RefreshRecentQuizzes();
        }
        return false;
    }

    internal bool CommitEditorMindMap(MindMapDocument snapshot)
    {
        if (_dashboardLoadFailures.Contains(DashboardMindMaps)) return false;
        var original = MindMaps.FirstOrDefault(s => s.Document.Id == snapshot.Id);
        var previous = original is null ? null : EditorHosting.Clone(original.Document);
        snapshot.LastModified = DateTime.Now;
        var updated = AddOrUpdateMindMap(snapshot, persist: false);
        if (TrySaveMindMaps()) return true;
        if (previous is not null) AddOrUpdateMindMap(previous, persist: false);
        else
        {
            MindMaps.Remove(updated);
            RebuildMindMapGroups(); RefreshAvailableMindMapTags(); ApplyMindMapFilters();
            NotifyMindMapsChanged(); RefreshRecentMindMaps();
        }
        return false;
    }

    public bool TrySaveNotes()
    {
        if (_dashboardLoadFailures.Contains(DashboardNotes)) return false;
        // Existing dashboard writes may already be queued. Let them settle before the confirmed
        // editor write so an older snapshot cannot overwrite the newly saved document.
        FlushPendingNotesSaveBeforeExit(TimeSpan.FromSeconds(10));
        if (_notesWriteInFlight || _notesWritePending || _saveNotesQueued) return false;
        try
        {
            var signature = BuildNotesStoreSignature();
            if (signature == _lastSavedNotesSignature) return true;
            AtomicFileWriter.Write(GetNotesFilePath(), stream => JsonSerializer.Serialize(stream, BuildNotesStoreSnapshot(), StoreJsonOptions));
            _lastSavedNotesSignature = signature;
            return true;
        }
        catch { return false; }
    }

    internal bool CommitEditorNote(NoteCardViewModel note, NoteDocument snapshot)
    {
        // The editor owns a working copy; dashboard-owned structure remains authoritative.
        snapshot.GroupId = note.Document.GroupId;
        snapshot.IsPinned = note.Document.IsPinned;
        snapshot.Schedules = note.Document.Schedules;
        snapshot.ScheduledAt = note.Document.ScheduledAt;
        snapshot.ScheduleNote = note.Document.ScheduleNote;
        var previous = EditorHosting.Clone(note.Document);
        CopyNote(snapshot, note.Document);
        if (!TrySaveNotes()) { CopyNote(previous, note.Document); return false; }
        note.NotifyContentChanged();
        RefreshTagFiltersAfterNoteEdit();
        return true;
    }

    private static void CopyNote(NoteDocument source, NoteDocument target)
    {
        foreach (var property in typeof(NoteDocument).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(target, property.GetValue(source));
    }
}
