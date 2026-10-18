using System.Reflection;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class QuickAddTests
{
    [Fact]
    public void Saving_during_an_active_write_coalesces_even_when_the_current_state_matches_the_previous_save() => Sta(() =>
    {
        WithLibrary(EditorDocumentKind.Note, (vm, _) =>
        {
            var signature = MainViewModel.ComputeNotesStoreSignature([], [], 0, []);
            typeof(MainViewModel).GetField("_lastSavedNotesSignature", Flags)!.SetValue(vm, signature);
            typeof(MainViewModel).GetField("_notesWriteInFlight", Flags)!.SetValue(vm, true);
            try
            {
                vm.SaveNotes();
                Assert.True((bool)typeof(MainViewModel).GetField("_notesWritePending", Flags)!.GetValue(vm)!);
            }
            finally
            {
                typeof(MainViewModel).GetField("_notesWriteInFlight", Flags)!.SetValue(vm, false);
                typeof(MainViewModel).GetField("_notesWritePending", Flags)!.SetValue(vm, false);
            }
        });
    });

    [Theory]
    [InlineData(EditorDocumentKind.Note)]
    [InlineData(EditorDocumentKind.Flashcards)]
    [InlineData(EditorDocumentKind.MindMap)]
    [InlineData(EditorDocumentKind.Quiz)]
    public void Rapid_additions_are_distinct_persisted_documents_with_usable_defaults(EditorDocumentKind kind) => Sta(() =>
    {
        WithLibrary(kind, (vm, directory) =>
        {
            for (var i = 0; i < 30; i++) vm.QuickAddDashboardItemCommand.Execute(null);
            Assert.Equal(30, Ids(vm, kind).Distinct().Count());
            WaitUntil(() => Settled(vm));
            var json = File.ReadAllText(Path.Combine(directory, FileName(kind)));
            foreach (var id in Ids(vm, kind)) Assert.Contains(id.ToString(), json);
            if (kind == EditorDocumentKind.Note)
                Assert.All(vm.Notes, n => { Assert.False(string.IsNullOrWhiteSpace(n.Title)); Assert.True(n.Document.FontSize > 0); });
            if (kind == EditorDocumentKind.Flashcards) Assert.All(vm.FlashcardSets, n => Assert.Empty(n.Document.Cards));
            if (kind == EditorDocumentKind.Quiz) Assert.All(vm.Quizzes, n => Assert.Empty(n.Document.Questions));
            if (kind == EditorDocumentKind.MindMap)
                Assert.All(vm.MindMaps, n => { Assert.False(string.IsNullOrWhiteSpace(n.Document.Root.Text)); Assert.Empty(n.Document.Root.Children); });
        });
    });

    [Theory]
    [InlineData(EditorDocumentKind.Note)]
    [InlineData(EditorDocumentKind.Flashcards)]
    [InlineData(EditorDocumentKind.MindMap)]
    [InlineData(EditorDocumentKind.Quiz)]
    public void Failed_additions_roll_back_only_new_items_and_allow_retry(EditorDocumentKind kind) => Sta(() =>
    {
        WithLibrary(kind, (vm, directory) =>
        {
            vm.QuickAddDashboardItemCommand.Execute(null);
            WaitUntil(() => Settled(vm));
            var original = Ids(vm, kind).Single();
            var saved = File.ReadAllText(Path.Combine(directory, FileName(kind)));
            var blocker = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocker, "blocked");
            vm.PersistenceDirectoryOverride = blocker;
            int errors = 0;
            vm.QuickAddFailed += (_, _) => errors++;
            for (var i = 0; i < 8; i++) vm.QuickAddDashboardItemCommand.Execute(null);
            WaitUntil(() => Settled(vm));
            Assert.True(errors > 0);
            Assert.Equal(original, Ids(vm, kind).Single());
            Assert.Equal(saved, File.ReadAllText(Path.Combine(directory, FileName(kind))));
            vm.PersistenceDirectoryOverride = directory;
            vm.QuickAddDashboardItemCommand.Execute(null);
            WaitUntil(() => Settled(vm));
            Assert.Equal(2, Ids(vm, kind).Count());
            Assert.Contains(original, Ids(vm, kind));
        });
    });

    [Theory]
    [InlineData(EditorDocumentKind.Note)]
    [InlineData(EditorDocumentKind.Flashcards)]
    [InlineData(EditorDocumentKind.MindMap)]
    [InlineData(EditorDocumentKind.Quiz)]
    public void Creation_is_guarded_during_loading_and_failure_but_allowed_during_selection(EditorDocumentKind kind) => Sta(() =>
    {
        WithLibrary(kind, (vm, _) =>
        {
            var ready = typeof(MainViewModel).GetField(ReadyField(kind), Flags)!;
            ready.SetValue(vm, false);
            Assert.False(vm.CanCreateDashboardItem);
            vm.QuickAddDashboardItemCommand.Execute(null);
            Assert.Empty(Ids(vm, kind));
            ready.SetValue(vm, true);
            var failures = (HashSet<string>)typeof(MainViewModel).GetField("_dashboardLoadFailures", Flags)!.GetValue(vm)!;
            failures.Add(Dashboard(kind));
            Assert.False(vm.QuickAddDashboardItemCommand.CanExecute(null));
            vm.QuickAddDashboardItemCommand.Execute(null);
            Assert.Empty(Ids(vm, kind));
            failures.Clear();
            vm.DashboardSelection.Begin();
            Assert.True(vm.CanCreateDashboardItem);
            vm.QuickAddDashboardItemCommand.Execute(null);
            WaitUntil(() => Settled(vm));
            Assert.Single(Ids(vm, kind));
            Assert.True(vm.DashboardSelection.IsActive);
            Assert.False(vm.DashboardSelection.Contains(Ids(vm, kind).Single()));
            vm.DashboardSelection.SelectAll();
            Assert.Equal(Ids(vm, kind).Count(), vm.DashboardSelection.Count);
        });
    });

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string Dashboard(EditorDocumentKind kind) => kind switch
    { EditorDocumentKind.Note => "Notes", EditorDocumentKind.Flashcards => "Flashcards", EditorDocumentKind.MindMap => "MindMaps", _ => "Quizzes" };
    private static string ReadyField(EditorDocumentKind kind) => "_is" + Dashboard(kind) + "DashboardReady";
    private static string FileName(EditorDocumentKind kind) => kind switch
    { EditorDocumentKind.Note => "notes.json", EditorDocumentKind.Flashcards => "flashcard-sets.json", EditorDocumentKind.MindMap => "mind-maps.json", _ => "quizzes.json" };
    private static IEnumerable<Guid> Ids(MainViewModel vm, EditorDocumentKind kind) => kind switch
    {
        EditorDocumentKind.Note => vm.Notes.Select(n => n.Document.Id),
        EditorDocumentKind.Flashcards => vm.FlashcardSets.Select(n => n.Document.Id),
        EditorDocumentKind.MindMap => vm.MindMaps.Select(n => n.Document.Id),
        _ => vm.Quizzes.Select(n => n.Document.Id)
    };
    private static bool Settled(MainViewModel vm)
        => !new[] { "_saveNotesQueued", "_notesWriteInFlight", "_notesWritePending" }.Any(name => (bool)typeof(MainViewModel).GetField(name, Flags)!.GetValue(vm)!)
            && ((Dictionary<EditorDocumentKind, HashSet<Guid>>)typeof(MainViewModel).GetField("_pendingQuickAdds", Flags)!.GetValue(vm)!).Values.All(ids => ids.Count == 0);
    private static void WithLibrary(EditorDocumentKind kind, Action<MainViewModel, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Scriblucent-quick-add", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vm = new MainViewModel(new AppSettings { EnableAutoSave = false, EnableAiFeatures = false }, false, false)
            { PersistenceDirectoryOverride = directory };
        typeof(MainViewModel).GetField("_activeDashboard", Flags)!.SetValue(vm, Dashboard(kind));
        // This fixture has no Application dispatcher. Real library loading is exercised by
        // MotionAudit's shell probe; these tests start from a ready, empty in-memory library.
        typeof(MainViewModel).GetField(ReadyField(kind), Flags)!.SetValue(vm, true);
        typeof(MainViewModel).GetField("_notesLoadComplete", Flags)!.SetValue(vm, true);
        try { action(vm, directory); }
        finally { WaitUntil(() => Settled(vm)); Directory.Delete(directory, recursive: true); }
    }
    private static void WaitUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(ready(), "Creation did not settle.");
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
