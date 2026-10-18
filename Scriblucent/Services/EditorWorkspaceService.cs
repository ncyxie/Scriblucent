using System.Windows;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using Scriblucent.Localization;
using Scriblucent.Animations;

namespace Scriblucent.Services;

/// <summary>One non-modal document workspace per dashboard; typed editors retain their state.</summary>
public sealed class EditorWorkspaceService
{
    private readonly Window _owner;
    private readonly MainViewModel _viewModel;
    private readonly Action<Guid> _openNote;
    private MainViewModel ViewModel => _viewModel;
    private EditorWorkspaceWindow? _window;
    public EditorWorkspaceService(MainWindow owner)
        : this(owner, (MainViewModel)owner.DataContext, id => owner.OpenNoteById((MainViewModel)owner.DataContext, id)) { }
    internal EditorWorkspaceService(Window owner, MainViewModel viewModel, Action<Guid>? openNote = null)
    {
        _owner = owner;
        _viewModel = viewModel;
        _openNote = openNote ?? (_ => { });
    }
    public EditorWorkspaceWindow? Window => _window;

    internal DocumentExportSnapshot CaptureExportSnapshot(EditorDocumentKey key)
    {
        _owner.Dispatcher.VerifyAccess();
        object saved = key.Kind switch
        {
            EditorDocumentKind.Note => ViewModel.Notes.First(note => note.Document.Id == key.Id).Document,
            EditorDocumentKind.Flashcards => ViewModel.FlashcardSets.First(set => set.Document.Id == key.Id).Document,
            EditorDocumentKind.Quiz => ViewModel.Quizzes.First(quiz => quiz.Document.Id == key.Id).Document,
            _ => ViewModel.MindMaps.First(map => map.Document.Id == key.Id).Document
        };
        var editor = _window?.Sessions.FirstOrDefault(session => session.Key == key)?.Editor;
        return CaptureExportSnapshot(saved, editor);
    }

    internal async Task<IReadOnlyList<DocumentExportSnapshot>> CaptureExportSnapshotsAsync(IReadOnlyList<EditorDocumentKey> keys, CancellationToken token)
    {
        _owner.Dispatcher.VerifyAccess(); token.ThrowIfCancellationRequested();
        var wanted = keys.ToHashSet(); var kinds = keys.Select(key => key.Kind).ToHashSet();
        var sources = new Dictionary<EditorDocumentKey, object>();
        void Add(EditorDocumentKind kind, Guid id, object document)
        { var key = new EditorDocumentKey(kind, id); if (wanted.Contains(key)) sources[key] = document; }
        // Index once: exporting a large selection must not rescan the library for each ID.
        if (kinds.Contains(EditorDocumentKind.Note)) foreach (var item in ViewModel.Notes) Add(EditorDocumentKind.Note, item.Document.Id, item.Document);
        if (kinds.Contains(EditorDocumentKind.Flashcards)) foreach (var item in ViewModel.FlashcardSets) Add(EditorDocumentKind.Flashcards, item.Document.Id, item.Document);
        if (kinds.Contains(EditorDocumentKind.Quiz)) foreach (var item in ViewModel.Quizzes) Add(EditorDocumentKind.Quiz, item.Document.Id, item.Document);
        if (kinds.Contains(EditorDocumentKind.MindMap)) foreach (var item in ViewModel.MindMaps) Add(EditorDocumentKind.MindMap, item.Document.Id, item.Document);
        var editors = _window?.Sessions.ToDictionary(session => session.Key, session => session.Editor) ?? new();
        var snapshots = new List<DocumentExportSnapshot>(keys.Count);
        var slice = System.Diagnostics.Stopwatch.StartNew();
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(key, out var saved)) throw new InvalidOperationException(LocalizationService.GetString("SelectionExportEmpty"));
            snapshots.Add(CaptureExportSnapshot(saved, editors.GetValueOrDefault(key)));
            if (slice.ElapsedMilliseconds >= 4) { await Dispatcher.Yield(DispatcherPriority.Background); slice.Restart(); }
        }
        token.ThrowIfCancellationRequested(); return snapshots;
    }

    private static DocumentExportSnapshot CaptureExportSnapshot(object saved, IHostedDocumentEditor? editor)
    {
        editor?.CommitWorkspaceEdits();
        object document = editor switch
        {
            NoteEditorWindow note => note.CaptureExportDocument((NoteDocument)saved),
            FlashcardsPreviewWindow cards => cards.ToDocument((FlashcardSetDocument)saved),
            QuizPreviewWindow quiz => quiz.ToDocument((QuizDocument)saved),
            MindMapPreviewWindow map => map.CaptureExportDocument((MindMapDocument)saved),
            _ => saved
        };
        return DocumentExportSnapshot.Capture(document) with { NoteMediaDirectory = MainViewModel.NotesMediaDirectory };
    }

    internal string CurrentTitle(EditorDocumentKey key, string fallback) => _window?.Sessions.FirstOrDefault(session => session.Key == key)?.Editor.WorkspaceTitle ?? fallback;
    internal void ApplyRenamedTitles(EditorDocumentKind kind, IReadOnlyList<RenameChange> changes)
    {
        if (_window is null) return;
        var names = changes.ToDictionary(change => change.Id, change => change.After);
        foreach (var session in _window.Sessions.Where(session => session.Key.Kind == kind && names.ContainsKey(session.Key.Id)))
        {
            var clean = !session.Editor.WorkspaceDirty;
            var title = (System.Windows.Controls.TextBox)session.Content.FindName("TitleTextBox");
            title.Text = names[session.Key.Id]; title.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            // A clean editor has no other changes to acknowledge. Dirty editors keep every
            // pending edit and their save marker; renaming never implicitly saves their content.
            if (clean)
                switch (session.Editor)
                {
                    case NoteEditorWindow note: note.MarkWorkspaceSaved(); break;
                    case FlashcardsPreviewWindow cards: cards.MarkCurrentStateAutoSaved(); break;
                    case QuizPreviewWindow quiz: quiz.MarkCurrentStateAutoSaved(); break;
                    case MindMapPreviewWindow map: map.MarkCurrentStateAutoSaved(); break;
                }
        }
    }

    private EditorWorkspaceWindow EnsureWindow()
    {
        if (_window is not null && !_window.IsCloseApproved) return _window;
        var window = new EditorWorkspaceWindow { Owner = _owner, DataContext = ViewModel };
        window.CreateRequested += Create;
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; _owner.Activate(); };
        _window = window;
        ViewModel.EditorRemovalGuard = keys => _window?.PrepareDocumentRemoval(keys) ?? true;
        return window;
    }

    private void Present(EditorWorkspaceWindow window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void OpenNote(NoteCardViewModel note)
    {
        if (note.Document.Id == Guid.Empty) note.Document.Id = Guid.NewGuid();
        var window = EnsureWindow();
        window.OpenOrFocusNote(note, ViewModel);
        Present(window);
    }

    public void OpenFlashcards(FlashcardSetDocument document, bool draft = false)
    {
        if (document.Id == Guid.Empty) document.Id = Guid.NewGuid();
        ViewModel.EnsureEditorStoreLoaded(EditorDocumentKind.Flashcards);
        var window = EnsureWindow();
        var key = new EditorDocumentKey(EditorDocumentKind.Flashcards, document.Id);
        if (window.FocusDocument(key)) { Present(window); return; }
        var working = EditorHosting.Clone(document);
        var editor = new FlashcardsPreviewWindow(working.Cards,
            ViewModel.Notes.Select(n => new FlashcardsPreviewWindow.FlashcardNoteLinkOption(n.Document.Id, n.Document.Title)),
            working.AiModelDisplayName, working.Title, working.Tags, working.SetNames, working.StudySession,
            working.SourceNoteId, _openNote, working.FlipDelayMilliseconds,
            working.StudyDueCardsOnly, isTagsPanelOpen: working.IsEditorTagsPanelOpen,
            aiReviewNoticeDismissed: working.AiReviewNoticeDismissed);
        editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
        {
            if (ViewModel.FlashcardSets.FirstOrDefault(s => s.Document.Id == key.Id) is not { } saved) return;
            saved.Document.AiReviewNoticeDismissed = true;
            saved.Document.LastModified = DateTime.Now;
            ViewModel.SaveFlashcardSets();
            saved.NotifyChanged();
        };
        Open(window, key, editor, draft, () =>
        {
            // Read structural metadata from the dashboard so pin/schedule/group changes survive.
            var baseline = ViewModel.FlashcardSets.FirstOrDefault(s => s.Document.Id == key.Id)?.Document ?? working;
            var snapshot = EditorHosting.Clone(editor.ToDocument(baseline));
            if (!ViewModel.CommitEditorFlashcards(snapshot)) return false;
            working = EditorHosting.Clone(snapshot);
            editor.MarkCurrentStateAutoSaved();
            return true;
        });
        Present(window);
    }

    public void OpenQuiz(QuizDocument document, bool draft = false)
    {
        if (document.Id == Guid.Empty) document.Id = Guid.NewGuid();
        ViewModel.EnsureEditorStoreLoaded(EditorDocumentKind.Quiz);
        var window = EnsureWindow();
        var key = new EditorDocumentKey(EditorDocumentKind.Quiz, document.Id);
        if (window.FocusDocument(key)) { Present(window); return; }
        var working = EditorHosting.Clone(document);
        Func<bool>? save = null;
        var editor = new QuizPreviewWindow(working,
            ViewModel.Notes.Select(n => new QuizPreviewWindow.QuizNoteLinkOption(n.Document.Id, n.Document.Title)),
            working.AiModelDisplayName, working.Title, _openNote,
            requestImmediateSave: () => save?.Invoke());
        editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
        {
            if (ViewModel.Quizzes.FirstOrDefault(s => s.Document.Id == key.Id) is not { } saved) return;
            saved.Document.AiReviewNoticeDismissed = true;
            saved.Document.LastModified = DateTime.Now;
            ViewModel.SaveQuizzes();
            saved.NotifyChanged();
        };
        save = Open(window, key, editor, draft, () =>
        {
            var baseline = ViewModel.Quizzes.FirstOrDefault(s => s.Document.Id == key.Id)?.Document ?? working;
            var snapshot = EditorHosting.Clone(editor.ToDocument(baseline));
            if (!ViewModel.CommitEditorQuiz(snapshot)) return false;
            editor.MarkCurrentStateAutoSaved();
            return true;
        });
        Present(window);
    }

    public void OpenMindMap(MindMapDocument document, bool draft = false)
    {
        if (document.Id == Guid.Empty) document.Id = Guid.NewGuid();
        ViewModel.EnsureEditorStoreLoaded(EditorDocumentKind.MindMap);
        var window = EnsureWindow();
        var key = new EditorDocumentKey(EditorDocumentKind.MindMap, document.Id);
        if (window.FocusDocument(key)) { Present(window); return; }
        var working = EditorHosting.Clone(document);
        var editor = new MindMapPreviewWindow(working.Root,
            ViewModel.Notes.Select(n => new MindMapPreviewWindow.MindMapNoteLinkOption(n.Document.Id, n.Document.Title)),
            working.AiModelDisplayName, working.Title, working.Tags, working.LayoutMode, working.UseManualPositions,
            working.SourceNoteId, _openNote,
            isTagsPanelOpen: working.IsEditorTagsPanelOpen, aiReviewNoticeDismissed: working.AiReviewNoticeDismissed);
        editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
        {
            if (ViewModel.MindMaps.FirstOrDefault(s => s.Document.Id == key.Id) is not { } saved) return;
            saved.Document.AiReviewNoticeDismissed = true;
            saved.Document.LastModified = DateTime.Now;
            ViewModel.SaveMindMaps();
            saved.NotifyChanged();
        };
        Open(window, key, editor, draft, () =>
        {
            var baseline = ViewModel.MindMaps.FirstOrDefault(s => s.Document.Id == key.Id)?.Document ?? working;
            var snapshot = EditorHosting.Clone(editor.ToDocument(baseline));
            if (!ViewModel.CommitEditorMindMap(snapshot)) return false;
            working = EditorHosting.Clone(snapshot);
            editor.MarkCurrentStateAutoSaved();
            return true;
        });
        Present(window);
    }

    private static Func<bool> Open(EditorWorkspaceWindow window, EditorDocumentKey key,
        IHostedDocumentEditor editor, bool draft, Func<bool> persist)
    {
        EditorSession? session = null;
        bool Save() => session is not null && window.SaveSession(session, manual: true);
        var content = editor.AttachToWorkspace(window, Save, () => window.CloseDocument(key));
        session = new EditorSession { Key = key, Editor = editor, Content = content, Save = persist, IsDraft = draft };
        window.AddSession(session);
        return Save;
    }

    public void Create(EditorDocumentKind kind)
    {
        switch (kind)
        {
            case EditorDocumentKind.Note:
                CreateNoteAndOpen();
                break;
            case EditorDocumentKind.Flashcards:
                OpenFlashcards(NewDocumentDefaults.Flashcards());
                break;
            case EditorDocumentKind.Quiz:
                OpenQuiz(NewDocumentDefaults.Quiz());
                break;
            case EditorDocumentKind.MindMap:
                OpenMindMap(NewDocumentDefaults.MindMap());
                break;
        }
    }

    private async void CreateNoteAndOpen()
    {
        // Startup can still be loading Notes while another dashboard/editor is already usable.
        // Creating before that read finishes could lose the new row when the library is applied.
        var window = EnsureWindow();
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        void CheckLoaded()
        {
            if (ViewModel.NotesEditorLoadFailed) loaded.TrySetResult(false);
            else if (ViewModel.IsNotesDashboardReady) loaded.TrySetResult(true);
        }
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => CheckLoaded();
        EventHandler closed = (_, _) => { cancelled = true; loaded.TrySetResult(false); };
        ViewModel.PropertyChanged += changed;
        _owner.Closed += closed;
        window.Closed += closed;
        try
        {
            ViewModel.EnsureEditorStoreLoaded(EditorDocumentKind.Note);
            CheckLoaded();
            if (!await loaded.Task)
            {
                if (!cancelled) ModernDialog.ShowError(window.IsVisible ? window : _owner, LocalizationService.GetString("NotesLoadFailedTitle"),
                    LocalizationService.GetString("WorkspaceCreateNoteFailed"));
                return;
            }
            if (!cancelled && !window.IsCloseApproved) OpenNote(ViewModel.CreateNoteForEditor());
        }
        catch (Exception exception)
        {
            if (!cancelled && !window.IsCloseApproved)
                ModernDialog.ShowError(window.IsVisible ? window : _owner, LocalizationService.GetString("Error"), exception.Message);
        }
        finally
        {
            ViewModel.PropertyChanged -= changed;
            _owner.Closed -= closed;
            window.Closed -= closed;
        }
    }
}
