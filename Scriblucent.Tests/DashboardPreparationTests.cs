using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DashboardPreparationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Loading_publishes_one_reset_then_finalizes_before_readiness(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard);
        var json = library.WriteStore();
        var active = library.Collection(); var trash = library.Collection(true);
        int resets = 0, trashResets = 0; bool inputRan = false;
        ((INotifyCollectionChanged)active).CollectionChanged += (_, args) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action); resets++;
            Assert.False(library.Ready); Assert.False(library.Save());
            Dispatcher.CurrentDispatcher.BeginInvoke(() => inputRan = true, DispatcherPriority.Input);
        };
        ((INotifyCollectionChanged)trash).CollectionChanged += (_, args) =>
        { Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action); trashResets++; };
        library.Load(); Assert.False(library.Ready); Assert.False(library.Save());
        Until(() => library.Ready);
        Assert.True(inputRan); Assert.Equal(1, resets); Assert.Equal(1, trashResets);
        Assert.Same(active, library.Collection()); Assert.Same(trash, library.Collection(true));
        Assert.Equal(24, active.Count); Assert.Single(trash.Cast<object>());
        var first = active[0]!;
        Assert.True((bool)first.GetType().GetProperty("IsPinned")!.GetValue(first)!);
        Assert.All(active.Cast<object>(), item =>
        {
            var title = (string)item.GetType().GetProperty("Title")!.GetValue(item)!;
            Assert.Equal(title.Trim(), title);
        });
        var groups = (IList)typeof(MainViewModel).GetProperty(dashboard switch
        { "Flashcards" => "FlashcardSetGroups", "MindMaps" => "MindMapGroups", _ => "QuizGroups" })!.GetValue(library.Vm)!;
        Assert.Single(groups.Cast<object>());
        Assert.Equal(json, File.ReadAllText(library.Path));
        Assert.True(library.Save());
    });

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Closing_a_load_prevents_late_publication_and_saving_an_empty_library(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard); var json = library.WriteStore();
        library.Load(); library.Vm.CancelPendingDashboardLoads();
        for (int i = 0; i < 15; i++) Pump();
        Assert.False(library.Ready); Assert.Empty(library.Collection().Cast<object>());
        Assert.False(library.Save()); Assert.Equal(json, File.ReadAllText(library.Path));
    });

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Failed_read_can_retry_without_overwriting_the_original_file(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard);
        File.WriteAllText(library.Path, "{ broken"); library.Load();
        Until(() => library.Vm.IsActiveDashboardLoadFailed);
        Assert.False(library.Ready); Assert.False(library.Save());
        Assert.Equal("{ broken", File.ReadAllText(library.Path));
        var valid = library.WriteStore();
        library.Vm.RetryActiveDashboardLoad(); Until(() => library.Ready);
        Assert.False(library.Vm.IsActiveDashboardLoadFailed);
        Assert.Equal(24, library.Collection().Count); Assert.Equal(valid, File.ReadAllText(library.Path));
    });

    [Fact]
    public void Preparation_failure_preserves_the_store_and_does_not_publish_partial_models() => Sta(() =>
    {
        using var library = new Library("Flashcards");
        var invalid = library.WriteStore().Replace("\"History\":[]", "\"History\":null");
        Assert.Contains("\"History\":null", invalid);
        File.WriteAllText(library.Path, invalid); library.Load();
        Until(() => library.Vm.IsActiveDashboardLoadFailed);
        Assert.False(library.Ready); Assert.Empty(library.Vm.FlashcardSets);
        Assert.False(library.Save()); Assert.Equal(invalid, File.ReadAllText(library.Path));
    });

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Cancellation_between_publication_and_finalization_never_announces_ready(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard); var json = library.WriteStore();
        ((INotifyCollectionChanged)library.Collection()).CollectionChanged += (_, _) =>
            Dispatcher.CurrentDispatcher.BeginInvoke(library.Vm.CancelPendingDashboardLoads, DispatcherPriority.Input);
        library.Load(); Until(() => library.Collection().Count == 24);
        for (int i = 0; i < 10; i++) Pump();
        Assert.False(library.Ready); Assert.False(library.Save());
        Assert.Equal(json, File.ReadAllText(library.Path));
    });

    [Fact]
    public void Legacy_flashcards_migrate_only_after_the_prepared_set_is_complete() => Sta(() =>
    {
        using var library = new Library("Flashcards");
        File.WriteAllText(System.IO.Path.Combine(library.Directory, "flashcards.json"),
            JsonSerializer.Serialize(new[] { new FlashcardItem { Question = " question ", Answer = " answer " } }));
        library.Load(); Until(() => library.Ready);
        Assert.Single(library.Vm.Flashcards); Assert.Single(library.Vm.FlashcardSets);
        var saved = JsonSerializer.Deserialize<FlashcardSetStoreData>(File.ReadAllText(library.Path))!;
        Assert.Single(saved.Sets); Assert.Single(saved.Sets[0].Cards);
        Assert.Equal("question", saved.Sets[0].Cards[0].Question);
    });

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Bulk_backing_preserves_mass_delete_and_restore_behaviour(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard); library.WriteStore(count: 130);
        library.Load(); Until(() => library.Ready);
        Guid Id(object item)
        {
            var document = item.GetType().GetProperty("Document")!.GetValue(item)!;
            return (Guid)document.GetType().GetProperty("Id")!.GetValue(document)!;
        }
        var originals = library.Collection().Cast<object>().Select(Id).ToHashSet();
        switch (library.Collection()[0])
        {
            case FlashcardSetViewModel set: library.Vm.EnterMassSelect(set); break;
            case MindMapViewModel map: library.Vm.EnterMassSelect(map); break;
            case QuizViewModel quiz: library.Vm.EnterMassSelect(quiz); break;
        }
        library.Vm.SelectAllVisibleNotesCommand.Execute(null);
        library.Vm.DeleteSelectedNotesCommand.Execute(null);
        var removed = library.Vm.TrashedItems.Where(item => originals.Contains(item.Id)).ToArray();
        Assert.True(removed.Length >= 128);
        library.Vm.ExitMassSelectCommand.Execute(null);
        library.Vm.RestoreTrashItems(removed);
        Assert.Equal(130, library.Collection().Count);
        Assert.True(originals.SetEquals(library.Collection().Cast<object>().Select(Id)));
        Assert.Single(library.Collection(true).Cast<object>()); Assert.True(library.Save());
    });

    [Theory]
    [InlineData("Flashcards")] [InlineData("MindMaps")] [InlineData("Quizzes")]
    public void Expired_trash_is_persisted_after_the_active_library_and_groups_are_complete(string dashboard) => Sta(() =>
    {
        using var library = new Library(dashboard); library.WriteStore(expired: true);
        library.Load(); Until(() => library.Ready);
        Assert.Empty(library.Collection(true).Cast<object>());
        using var saved = JsonDocument.Parse(File.ReadAllText(library.Path));
        var liveName = dashboard switch { "Flashcards" => "Sets", "MindMaps" => "Maps", _ => "Quizzes" };
        var trashName = dashboard switch { "Flashcards" => "TrashedFlashcardSets", "MindMaps" => "TrashedMindMaps", _ => "TrashedQuizzes" };
        Assert.Equal(24, saved.RootElement.GetProperty(liveName).GetArrayLength());
        Assert.Equal(0, saved.RootElement.GetProperty(trashName).GetArrayLength());
        Assert.Equal(1, saved.RootElement.GetProperty("Groups").GetArrayLength());
    });

    private sealed class Library : IDisposable
    {
        internal readonly MainViewModel Vm;
        internal readonly string Dashboard, Directory, Path;
        private readonly string? _oldOverride;
        private readonly object? _oldSettings;
        private static readonly FieldInfo Cached = typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        internal Library(string dashboard)
        {
            Dashboard = dashboard;
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Scriblucent-preparation-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, dashboard switch
            { "Flashcards" => "flashcard-sets.json", "MindMaps" => "mind-maps.json", _ => "quizzes.json" });
            _oldOverride = AppSettingsService.SettingsDirectoryOverride; _oldSettings = Cached.GetValue(null);
            AppSettingsService.SettingsDirectoryOverride = Directory;
            var settings = new AppSettings { LastView = "Notes", EnableAutoSave = false, EnableAiFeatures = false };
            Cached.SetValue(null, settings);
            Vm = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = Directory };
            typeof(MainViewModel).GetField("_activeDashboard", Hidden)!.SetValue(Vm, dashboard);
        }
        internal bool Ready => Vm.IsActiveDashboardReady;
        internal IList Collection(bool trash = false) => (IList)typeof(MainViewModel).GetProperty(
            (trash ? "Trashed" : "") + (Dashboard == "Flashcards" ? "FlashcardSets" : Dashboard))!.GetValue(Vm)!;
        internal bool Save() => Dashboard switch
        { "Flashcards" => Vm.TrySaveFlashcardSets(), "MindMaps" => Vm.TrySaveMindMaps(), _ => Vm.TrySaveQuizzes() };
        internal void Load() => typeof(MainViewModel).GetMethod("Load" + Dashboard + "PersistedContent", Hidden)!.Invoke(Vm, null);
        internal string WriteStore(bool expired = false, int count = 24)
        {
            var group = Guid.NewGuid(); var deleted = DateTime.UtcNow.AddDays(expired ? -40 : -1);
            object store = Dashboard switch
            {
                "Flashcards" => new FlashcardSetStoreData
                {
                    Sets = Enumerable.Range(0, count).Select(i => new FlashcardSetDocument { Title = $" title {i:D2} ", GroupId = i < 2 ? group : null,
                        IsPinned = i == 3, Cards = [new() { Question = " q ", Answer = " a " }] }).ToList(),
                    TrashedFlashcardSets = [new() { Title = "Trash", DeletedAt = deleted }],
                    Groups = [new() { GroupId = group, Name = "Group", SortOrder = 0 }]
                },
                "MindMaps" => new MindMapStoreData
                {
                    Maps = Enumerable.Range(0, count).Select(i => new MindMapDocument { Title = $" title {i:D2} ", GroupId = i < 2 ? group : null, IsPinned = i == 3 }).ToList(),
                    TrashedMindMaps = [new() { Title = "Trash", DeletedAt = deleted }],
                    Groups = [new() { GroupId = group, Name = "Group", SortOrder = 0 }]
                },
                _ => new QuizStoreData
                {
                    Quizzes = Enumerable.Range(0, count).Select(i => new QuizDocument { Title = $" title {i:D2} ", GroupId = i < 2 ? group : null,
                        IsPinned = i == 3, Questions = [new() { Type = QuizQuestionType.PlainText, Question = " q ", PlainTextAnswer = " a " }] }).ToList(),
                    TrashedQuizzes = [new() { Title = "Trash", DeletedAt = deleted }],
                    Groups = [new() { GroupId = group, Name = "Group", SortOrder = 0 }]
                }
            };
            var json = JsonSerializer.Serialize(store, store.GetType()); File.WriteAllText(Path, json); return json;
        }
        public void Dispose()
        {
            Vm.CancelPendingDashboardLoads(); AppSettingsService.Flush();
            AppSettingsService.SettingsDirectoryOverride = _oldOverride; Cached.SetValue(null, _oldSettings);
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Scriblucent-preparation-tests")) + System.IO.Path.DirectorySeparatorChar;
            Assert.StartsWith(root, System.IO.Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase);
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew(); while (!condition() && clock.ElapsedMilliseconds < 5000) Pump();
        Assert.True(condition());
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
