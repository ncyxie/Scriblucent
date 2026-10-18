using System.Reflection;
using System.Text.Json;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class GroupDisclosureTests
{
    [Fact]
    public void Legacy_preferences_and_new_groups_start_expanded() => Sta(() =>
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Dark\",\"EnableAutoSave\":false}")!;
        using var settings = new SettingsStore(legacy);
        var vm = new MainViewModel(legacy, false, false);
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
            Assert.True(vm.GetGroupDisclosure(scope, Guid.NewGuid()).IsExpanded);
        Assert.Empty(legacy.CollapsedDashboardGroups);
        Assert.Empty(Directory.GetFiles(settings.Directory));
    });

    [Fact]
    public void Disclosure_preferences_round_trip_by_group_and_dashboard_without_content_writes() => Sta(() =>
    {
        using var store = new SettingsStore(new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var initial = AppSettingsService.Load();
        var vm = new MainViewModel(initial, false, false) { PersistenceDirectoryOverride = Path.Combine(store.Directory, "documents") };
        var id = Guid.NewGuid();
        var notes = vm.GetGroupDisclosure(DashboardMotionScope.Notes, id);
        notes.IsExpanded = false;
        vm.GetGroupDisclosure(DashboardMotionScope.Flashcards, id).IsExpanded = false;
        Assert.Same(notes, vm.GetGroupDisclosure(DashboardMotionScope.Notes, id));
        Assert.True(vm.GetGroupDisclosure(DashboardMotionScope.Quizzes, id).IsExpanded);
        Assert.True(vm.GetGroupDisclosure(DashboardMotionScope.MindMaps, id).IsExpanded);
        AppSettingsService.Flush();
        Assert.False(Directory.Exists(vm.PersistenceDirectoryOverride));
        Assert.Equal("settings.json", Path.GetFileName(Assert.Single(Directory.GetFiles(store.Directory))));

        store.Reload();
        var restored = new MainViewModel(AppSettingsService.Load(), false, false);
        Assert.False(restored.GetGroupDisclosure(DashboardMotionScope.Notes, id).IsExpanded);
        Assert.False(restored.GetGroupDisclosure(DashboardMotionScope.Flashcards, id).IsExpanded);
        Assert.True(restored.GetGroupDisclosure(DashboardMotionScope.Notes, Guid.NewGuid()).IsExpanded);
        restored.GetGroupDisclosure(DashboardMotionScope.Notes, id).IsExpanded = true;
        AppSettingsService.Flush(); store.Reload();
        var reopened = new MainViewModel(AppSettingsService.Load(), false, false);
        Assert.True(reopened.GetGroupDisclosure(DashboardMotionScope.Notes, id).IsExpanded);
        Assert.False(reopened.GetGroupDisclosure(DashboardMotionScope.Flashcards, id).IsExpanded);
    });

    [Fact]
    public void Successful_drop_reopens_each_group_kind_and_retains_the_other_groups_state() => Sta(() =>
    {
        object[] groups =
        [
            new NoteGroupViewModel(Guid.NewGuid(), "Notes", "#F8FAFF", []),
            new FlashcardSetGroupViewModel(Guid.NewGuid(), "Cards", "#F8FAFF", []),
            new MindMapGroupViewModel(Guid.NewGuid(), "Maps", "#F8FAFF", []),
            new QuizGroupViewModel(Guid.NewGuid(), "Quizzes", "#F8FAFF", [])
        ];
        foreach (var group in groups)
        {
            var state = (GroupDisclosureState)group.GetType().GetProperty("Disclosure")!.GetValue(group)!;
            state.IsExpanded = false;
            MainWindow.ExpandDroppedGroup(group);
            Assert.True(state.IsExpanded);
            state.IsExpanded = false;
        }
        Assert.All(groups, g => Assert.False(((GroupDisclosureState)g.GetType().GetProperty("Disclosure")!.GetValue(g)!).IsExpanded));
    });

    private sealed class SettingsStore : IDisposable
    {
        private static readonly FieldInfo Cache = typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? _oldCache;
        private readonly string? _oldDirectory;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "Scriblucent-group-disclosure", Guid.NewGuid().ToString("N"));
        internal SettingsStore(AppSettings settings)
        {
            Assert.Null(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
            _oldCache = Cache.GetValue(null);
            _oldDirectory = AppSettingsService.SettingsDirectoryOverride;
            System.IO.Directory.CreateDirectory(Directory);
            AppSettingsService.SettingsDirectoryOverride = Directory;
            Cache.SetValue(null, settings);
        }
        internal void Reload() => Cache.SetValue(null, null);
        public void Dispose()
        {
            AppSettingsService.Flush();
            AppSettingsService.SettingsDirectoryOverride = _oldDirectory;
            Cache.SetValue(null, _oldCache);
        }
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                action();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Group disclosure test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
