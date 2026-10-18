using Scriblucent.Services;
using Scriblucent.Models;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    private DashboardSelectionScope? _dashboardSelectionScope;
    public ICollectionSelectionScope DashboardSelection => _dashboardSelectionScope ??= new DashboardSelectionScope(this);

    internal IReadOnlyList<EditorDocumentKey> SelectedDashboardExportKeys() => _dashboardSelectionScope?.ExportKeys() ?? [];

    internal IReadOnlyList<RenameTarget> SelectedDashboardRenameTargets() => _dashboardSelectionScope?.RenameTargets() ?? [];
    internal bool TryRenameDashboardSelection(IReadOnlyList<RenameChange> changes) => _dashboardSelectionScope?.Rename(changes) ?? false;

    private sealed class DashboardSelectionScope(MainViewModel owner) : ICollectionSelectionScope
    {
        private Dictionary<Guid, object>? _items;
        private Guid? _anchor;
        private bool _refreshing;
        public bool IsActive => owner.IsMassSelectMode;
        public bool CanSelect => owner.IsActiveDashboardReady && !owner.IsActiveDashboardLoadFailed;
        public int Count => owner.ActiveMassSelectedCount;
        public int TotalCount => IsActive ? Items.Count : 0;
        public string StatusText { get; private set; } = string.Empty;
        internal void ReportReorderStatus()
        {
            StatusText = Scriblucent.Localization.LocalizationService.GetString("ReorderSelectionScope");
            Changed?.Invoke();
        }
        public event Action? Changed;
        private Dictionary<Guid, object> Items => _items ??= BuildItems();
        private Dictionary<Guid, object> BuildItems()
        {
            IEnumerable<object> grouped, ungrouped;
            bool groupsFirst;
            switch (owner.ActiveDashboard)
            {
                case DashboardFlashcards:
                    grouped = owner.FlashcardSetGroups.SelectMany(group => group.Sets).Where(owner.MatchesFlashcardSetFilters);
                    ungrouped = owner.FlashcardSetsView.Cast<FlashcardSetViewModel>();
                    groupsFirst = owner.IsFlashcardDashboardGroupsFirst; break;
                case DashboardMindMaps:
                    grouped = owner.MindMapGroups.SelectMany(group => group.MindMaps).Where(owner.MatchesMindMapFilters);
                    ungrouped = owner.MindMapsView.Cast<MindMapViewModel>();
                    groupsFirst = owner.IsMindMapDashboardGroupsFirst; break;
                case DashboardQuizzes:
                    grouped = owner.QuizGroups.SelectMany(group => group.Quizzes).Where(owner.MatchesQuizFilters);
                    ungrouped = owner.QuizzesView.Cast<QuizViewModel>();
                    groupsFirst = owner.IsQuizDashboardGroupsFirst; break;
                default:
                    grouped = owner.NoteGroups.SelectMany(group => group.Notes).Where(owner.MatchesSearch);
                    ungrouped = owner.NotesView.Cast<NoteCardViewModel>();
                    groupsFirst = owner.IsGroupsFirst; break;
            }
            return (groupsFirst ? grouped.Concat(ungrouped) : ungrouped.Concat(grouped)).DistinctBy(Id).ToDictionary(Id);
        }
        private static Guid Id(object item) => item switch
        {
            NoteCardViewModel note => note.Document.Id, FlashcardSetViewModel set => set.Document.Id,
            MindMapViewModel map => map.Document.Id, QuizViewModel quiz => quiz.Document.Id, _ => Guid.Empty
        };
        internal IReadOnlyList<EditorDocumentKey> ExportKeys()
        {
            _items = null; // Commands capture the current view even while a visual reaction is queued.
            var kind = owner.ActiveDashboard switch { DashboardFlashcards => EditorDocumentKind.Flashcards, DashboardQuizzes => EditorDocumentKind.Quiz, DashboardMindMaps => EditorDocumentKind.MindMap, _ => EditorDocumentKind.Note };
            return Items.Keys.Where(Contains).Select(id => new EditorDocumentKey(kind, id)).ToArray();
        }
        private static object Document(object item) => item switch
        { NoteCardViewModel note => note.Document, FlashcardSetViewModel cards => cards.Document, MindMapViewModel map => map.Document, QuizViewModel quiz => quiz.Document, _ => throw new ArgumentException() };
        private static (string Title, DateTime Modified) TitleState(object document) => document switch
        { NoteDocument note => (note.Title, note.LastModified), FlashcardSetDocument cards => (cards.Title, cards.LastModified), MindMapDocument map => (map.Title, map.LastModified), QuizDocument quiz => (quiz.Title, quiz.LastModified), _ => throw new ArgumentException() };
        private static void SetTitle(object document, string title, DateTime modified)
        {
            switch (document)
            {
                case NoteDocument note: note.Title = title; note.LastModified = modified; break;
                case FlashcardSetDocument cards: cards.Title = title; cards.LastModified = modified; break;
                case MindMapDocument map: map.Title = title; map.LastModified = modified; break;
                case QuizDocument quiz: quiz.Title = title; quiz.LastModified = modified; break;
            }
        }
        internal IReadOnlyList<RenameTarget> RenameTargets()
        {
            _items = null;
            return Items.Where(pair => Contains(pair.Key)).Select(pair => new RenameTarget(pair.Key, TitleState(Document(pair.Value)).Title)).ToArray();
        }
        internal bool Rename(IReadOnlyList<RenameChange> changes)
        {
            if (!IsActive || changes.Count == 0) return false;
            if (changes.Any(change => string.IsNullOrWhiteSpace(change.After))) return false;
            var names = changes.ToDictionary(change => change.Id, change => change.After.Trim());
            var selected = Items.Where(pair => Contains(pair.Key) && names.ContainsKey(pair.Key)).Select(pair => (pair.Key, Item: pair.Value, Doc: Document(pair.Value))).ToArray();
            if (selected.Length != names.Count) return false;
            var previous = selected.ToDictionary(pair => pair.Key, pair => TitleState(pair.Doc));
            var modified = DateTime.Now;
            foreach (var pair in selected) SetTitle(pair.Doc, names[pair.Key], modified);
            bool saved = owner._massSelectDashboard switch
            {
                DashboardFlashcards => owner.TrySaveFlashcardSets(), DashboardMindMaps => owner.TrySaveMindMaps(),
                DashboardQuizzes => owner.TrySaveQuizzes(), _ => owner.TrySaveNotes()
            };
            if (!saved)
            {
                foreach (var pair in selected) SetTitle(pair.Doc, previous[pair.Key].Title, previous[pair.Key].Modified);
                return false;
            }
            foreach (var pair in selected)
                switch (pair.Item)
                {
                    case NoteCardViewModel note: note.NotifyTitleChanged(); break;
                    case FlashcardSetViewModel cards: cards.NotifyTitleChanged(); break;
                    case MindMapViewModel map: map.NotifyTitleChanged(); break;
                    case QuizViewModel quiz: quiz.NotifyTitleChanged(); break;
                }
            owner.ReorderActiveMassSelectDashboard(); owner.RefreshActiveMassSelectDashboard();
            switch (owner._massSelectDashboard)
            {
                case DashboardFlashcards: owner.RefreshRecentFlashcardSets(); break;
                case DashboardMindMaps: owner.RefreshRecentMindMaps(); break;
                case DashboardQuizzes: owner.RefreshRecentQuizzes(); break;
                default: owner.RefreshRecentNotes(); break;
            }
            owner.RefreshCalendarScheduledNotes();
            return true;
        }
        public bool Contains(Guid id) => owner._massSelectDashboard switch
        {
            DashboardFlashcards => owner._massSelectedFlashcardSetIds.Contains(id),
            DashboardMindMaps => owner._massSelectedMindMapIds.Contains(id),
            DashboardQuizzes => owner._massSelectedQuizIds.Contains(id), _ => owner._massSelectedNoteIds.Contains(id)
        };
        private void Set(object item, bool selected)
        {
            switch (item)
            {
                case NoteCardViewModel note: owner.SetNoteSelectedState(note, selected, false); break;
                case FlashcardSetViewModel set: owner.SetFlashcardSetSelectedState(set, selected, false); break;
                case MindMapViewModel map: owner.SetMindMapSelectedState(map, selected, false); break;
                case QuizViewModel quiz: owner.SetQuizSelectedState(quiz, selected, false); break;
            }
        }
        public void Begin(Guid? initial = null)
        {
            if (!CanSelect) return;
            if (!IsActive) { _items = null; _anchor = null; owner._massSelectDashboard = owner.ActiveDashboard; owner.IsMassSelectMode = true; }
            if (initial.HasValue) _items = null;
            if (initial is Guid id && Items.TryGetValue(id, out var item)) { Set(item, true); _anchor = id; }
            owner.NotifyMassSelectionChanged();
        }
        public void Toggle(Guid id, bool range = false, bool additive = false)
        {
            StatusText = string.Empty;
            if (!CanSelect) return;
            if (!IsActive) Begin();
            if (!Items.TryGetValue(id, out var item))
            { _items = null; if (!Items.TryGetValue(id, out item)) return; }
            if (range && _anchor is Guid anchor && Items.ContainsKey(anchor))
            {
                var order = Items.Keys.ToArray(); int a = Array.IndexOf(order, anchor), b = Array.IndexOf(order, id);
                if (!additive) foreach (var pair in Items) if (Contains(pair.Key)) Set(pair.Value, false);
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Set(Items[order[i]], true);
            }
            else { Set(item, !Contains(id)); _anchor = id; }
            owner.NotifyMassSelectionChanged();
        }
        public void SelectAll() { if (!CanSelect) return; if (!IsActive) Begin(); _items = null; foreach (var pair in Items) Set(pair.Value, true); owner.NotifyMassSelectionChanged(); }
        public void Clear() { foreach (var pair in Items) if (Contains(pair.Key)) Set(pair.Value, false); _anchor = null; owner.NotifyMassSelectionChanged(); }
        public void End() { _anchor = null; _items = null; owner.ExitMassSelect(); }
        internal void Notify() => Changed?.Invoke();
        internal void RefreshItems()
        {
            StatusText = string.Empty;
            _items = null;
            if (!IsActive || _refreshing) return;
            _refreshing = true;
            try
            {
                IEnumerable<object> selected = owner._massSelectDashboard switch
                {
                    DashboardFlashcards => owner.GetMassSelectedFlashcardSets(), DashboardMindMaps => owner.GetMassSelectedMindMaps(),
                    DashboardQuizzes => owner.GetMassSelectedQuizzes(), _ => owner.GetMassSelectedNotes()
                };
                foreach (var item in selected.ToArray()) if (!Items.ContainsKey(Id(item))) Set(item, false);
                if (_anchor is Guid anchor && !Items.ContainsKey(anchor)) _anchor = null;
                owner.NotifyMassSelectionChanged();
            }
            finally { _refreshing = false; }
        }
    }
}
