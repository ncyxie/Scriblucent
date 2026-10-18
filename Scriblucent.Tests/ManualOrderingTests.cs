using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class ManualOrderingTests
{
    [Fact]
    public void Block_insertion_uses_full_order_and_retains_hidden_items_and_block_order()
    {
        string[] original = ["a", "hidden1", "b", "hidden2", "c", "d"];
        Assert.Equal(["hidden1", "hidden2", "c", "a", "b", "d"], ItemOrdering.Move(original, new HashSet<string> { "b", "a" }, "c", true));
        Assert.Equal(["d", "a", "hidden1", "b", "hidden2", "c"], ItemOrdering.Move(original, new HashSet<string> { "d" }, "a", false));
        Assert.Equal(original, ItemOrdering.Move(original, new HashSet<string> { "a" }, "hidden1", false));
        Assert.Equal(original, ItemOrdering.Move(original, new HashSet<string> { "a" }, "a", true));
        Assert.Equal(original, ItemOrdering.Move(original, new HashSet<string> { "missing" }, "a", true));
    }
    [Fact]
    public void Reconciliation_keeps_restorable_positions_prepends_batches_and_prunes_permanent_deletions()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var state = new ManualItemOrder(); state.Set([ids[0], ids[1], ids[2], ids[0], Guid.Empty]);
        state.Reconcile([ids[0], ids[2], ids[3], ids[4], ids[1]]); // id 1 is in trash, and remains restorable.
        Assert.Equal([ids[3], ids[4], ids[0], ids[1], ids[2]], state.Ids);
        state.Reconcile([ids[0], ids[2], ids[3], ids[4]]);
        Assert.Equal([ids[3], ids[4], ids[0], ids[2]], state.Ids);
        Assert.True(state.Move(new HashSet<Guid> { ids[2] }, ids[3], false));
        Assert.Equal(0, state.Position(ids[2]));
        var restored = new ManualItemOrder(); restored.Set(JsonSerializer.Deserialize<List<Guid>>(JsonSerializer.Serialize(state.Ids)));
        Assert.Equal(state.Ids, restored.Ids);
    }
    [Fact]
    public void Notes_save_signature_covers_manual_order_even_when_content_and_dates_are_unchanged()
    {
        var notes = new[] { new NoteDocument(), new NoteDocument() };
        string Signature(IEnumerable<Guid> order) => MainViewModel.ComputeNotesStoreSignature(notes, [], 0, [], order);
        Assert.NotEqual(Signature(notes.Select(item => item.Id)), Signature(notes.Reverse().Select(item => item.Id)));
    }
    [Theory]
    [InlineData(DashboardMotionScope.Notes)] [InlineData(DashboardMotionScope.Flashcards)]
    [InlineData(DashboardMotionScope.MindMaps)] [InlineData(DashboardMotionScope.Quizzes)]
    public void Dashboard_moves_activate_manual_persist_and_keep_pin_and_group_boundaries(DashboardMotionScope scope) => Sta(() =>
    {
        using var fixture = new OrderingFixture(); var model = fixture.Model;
        object Create(int number, Guid? group = null, bool pinned = false)
        {
            var modified = new DateTime(2026, 1, 1).AddDays(number);
            object item = scope switch
            {
                DashboardMotionScope.Flashcards => new FlashcardSetViewModel(new FlashcardSetDocument { Title = number.ToString(), GroupId = group, IsPinned = pinned, LastModified = modified }),
                DashboardMotionScope.MindMaps => new MindMapViewModel(new MindMapDocument { Title = number.ToString(), GroupId = group, IsPinned = pinned, LastModified = modified }),
                DashboardMotionScope.Quizzes => new QuizViewModel(new QuizDocument { Title = number.ToString(), GroupId = group, IsPinned = pinned, LastModified = modified }),
                _ => model.AddNoteFromDocument(new NoteDocument { Title = number.ToString(), GroupId = group, IsPinned = pinned, LastModified = modified })
            };
            switch (item)
            {
                case FlashcardSetViewModel value: model.FlashcardSets.Add(value); break;
                case MindMapViewModel value: model.MindMaps.Add(value); break;
                case QuizViewModel value: model.Quizzes.Add(value); break;
            }
            return item;
        }
        var a = Create(1); var b = Create(2); var c = Create(3); var pin = Create(4, pinned: true);
        var dates = model.DashboardOrderItems(scope).ToDictionary(item => MainViewModel.DashboardOrderItem.From(item).Id,
            item => item switch { NoteCardViewModel n => n.Document.LastModified, FlashcardSetViewModel n => n.Document.LastModified,
                MindMapViewModel n => n.Document.LastModified, QuizViewModel n => n.Document.LastModified, _ => default });
        Assert.False(model.TryReorderDashboard([a], pin, false));
        Assert.NotEqual(MainViewModel.SortManual, model.DashboardSortKey(scope));
        Assert.False(model.TryReorderDashboard([a], b, true)); // Already adjacent in date-descending order.
        Assert.NotEqual(MainViewModel.SortManual, model.DashboardSortKey(scope));
        Assert.True(model.TryReorderDashboard([a], c, false));
        Assert.Equal(MainViewModel.SortManual, model.DashboardSortKey(scope));
        IEnumerable<object> View() => scope switch { DashboardMotionScope.Flashcards => model.FlashcardSetsView.Cast<object>(),
            DashboardMotionScope.MindMaps => model.MindMapsView.Cast<object>(), DashboardMotionScope.Quizzes => model.QuizzesView.Cast<object>(), _ => model.NotesView.Cast<object>() };
        Assert.Equal(new[] { pin, a, c, b }, View());
        foreach (var item in model.DashboardOrderItems(scope))
        {
            var entry = MainViewModel.DashboardOrderItem.From(item);
            var date = item switch { NoteCardViewModel n => n.Document.LastModified, FlashcardSetViewModel n => n.Document.LastModified,
                MindMapViewModel n => n.Document.LastModified, QuizViewModel n => n.Document.LastModified, _ => default };
            Assert.Equal(dates[entry.Id], date);
        }
        // Switch away and back: manual metadata survives automatic sorting.
        void Sort(string key)
        {
            switch (scope) { case DashboardMotionScope.Flashcards: model.SelectedFlashcardSortOptionKey = key; break;
                case DashboardMotionScope.MindMaps: model.SelectedMindMapSortOptionKey = key; break;
                case DashboardMotionScope.Quizzes: model.SelectedQuizSortOptionKey = key; break; default: model.SelectedSortOptionKey = key; break; }
        }
        Sort("title-asc"); Sort(MainViewModel.SortManual);
        Assert.Equal(new[] { pin, a, c, b }, View());
        var fresh = Create(7);
        Assert.Same(fresh, View().ElementAt(1));
        object Duplicate(object source)
        {
            var previous = model.DashboardOrderItems(scope).ToHashSet();
            switch (source)
            {
                case NoteCardViewModel note: note.DuplicateCommand.Execute(null); break;
                case FlashcardSetViewModel set: model.DuplicateFlashcardSet(set); break;
                case MindMapViewModel map: model.DuplicateMindMap(map); break;
                case QuizViewModel quiz: model.DuplicateQuiz(quiz); break;
            }
            return model.DashboardOrderItems(scope).Single(item => !previous.Contains(item));
        }
        var copy = Duplicate(a);
        var arranged = View().ToArray();
        Assert.Same(copy, arranged[Array.IndexOf(arranged, a) + 1]);
        var pinCopy = Duplicate(pin);
        Assert.True(MainViewModel.DashboardOrderItem.From(pinCopy).Pinned);
        Assert.Same(pinCopy, View().ElementAt(1));
        var group = Guid.NewGuid(); var g1 = Create(5, group); var g2 = Create(6, group);
        Assert.True(model.TryReorderDashboard([b, c], g1, false));
        Assert.Equal(group, MainViewModel.DashboardOrderItem.From(b).Group);
        Assert.Equal(group, MainViewModel.DashboardOrderItem.From(c).Group);
        fixture.FlushNotes();
        var file = scope switch { DashboardMotionScope.Flashcards => "flashcard-sets.json", DashboardMotionScope.MindMaps => "mind-maps.json", DashboardMotionScope.Quizzes => "quizzes.json", _ => "notes.json" };
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Documents, file)));
        var saved = json.RootElement.GetProperty("ManualOrder").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        Assert.True(Array.IndexOf(saved, MainViewModel.DashboardOrderItem.From(c).Id) < Array.IndexOf(saved, MainViewModel.DashboardOrderItem.From(b).Id));
        Assert.Contains(MainViewModel.DashboardOrderItem.From(g2).Id, saved);
    });
    private sealed class OrderingFixture : IDisposable
    {
        private readonly object? _cache;
        private readonly string? _settingsDirectory;
        private static readonly FieldInfo Cache = typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Scriblucent-manual-order-tests", Guid.NewGuid().ToString("N"));
        public string Documents => Path.Combine(Root, "documents");
        public MainViewModel Model { get; }
        public OrderingFixture()
        {
            AppSettingsService.Flush(); _cache = Cache.GetValue(null); _settingsDirectory = AppSettingsService.SettingsDirectoryOverride;
            AppSettingsService.SettingsDirectoryOverride = Path.Combine(Root, "settings");
            var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
            Cache.SetValue(null, settings);
            Model = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = Documents };
        }
        public void FlushNotes()
        {
            var pending = typeof(MainViewModel).GetField("_notesWriteInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while ((bool)pending.GetValue(Model)! && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Background);
                Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
            Assert.False((bool)pending.GetValue(Model)!);
        }
        public void Dispose()
        {
            FlushNotes(); AppSettingsService.Flush(); AppSettingsService.SettingsDirectoryOverride = _settingsDirectory; Cache.SetValue(null, _cache);
            // Isolated evidence is deliberately retained under this test's unique temp directory.
        }
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); action(); } catch (Exception caught) { error = caught; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
