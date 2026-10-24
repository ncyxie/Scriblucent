using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DashboardGroupNamingTests
{
    [Fact]
    public void Next_number_reserves_existing_numbers_in_both_languages()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            LocalizationService.SetCulture("en");
            NoteGroupData[] groups = [new() { Name = "Group 1" }, new() { Name = "Grupė 3" }, new() { Name = "Research" }];
            Assert.Equal("Group 4", DashboardGroupNaming.NextName(groups));
            Assert.Equal("Group 1", DashboardGroupNaming.NextName(Array.Empty<NoteGroupData>()));
            LocalizationService.SetCulture("lt");
            Assert.Equal("Grupė 4", DashboardGroupNaming.NextName(groups));
        }
        finally { LocalizationService.SetCulture(original.Name); }
    }

    [Fact]
    public void Old_defaults_are_numbered_in_saved_order_without_touching_custom_names()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            LocalizationService.SetCulture("en");
            var a = new NoteGroupData { GroupId = Guid.Parse("abcd0000-0000-0000-0000-000000000001"), Name = "Group ABCD", SortOrder = 4 };
            var b = new NoteGroupData { GroupId = Guid.Parse("12340000-0000-0000-0000-000000000002"), Name = "Grupė 1234", SortOrder = 0 };
            var custom = new NoteGroupData { Name = "Group BEEF", SortOrder = 1 };
            var reserved = new NoteGroupData { Name = "Group 2", SortOrder = 3 };
            NoteGroupData[] groups = [a, b, custom, reserved];
            Assert.True(DashboardGroupNaming.Normalize(groups));
            Assert.Equal("Group 1", b.Name); Assert.Equal("Group 3", a.Name);
            Assert.Equal("Group BEEF", custom.Name); Assert.Equal("Group 2", reserved.Name);
            Assert.False(DashboardGroupNaming.Normalize(groups.Reverse()));
            Assert.Equal("Group 4", DashboardGroupNaming.NextName(groups));
        }
        finally { LocalizationService.SetCulture(original.Name); }
    }

    [Fact]
    public void Numeric_names_that_coincide_with_the_GUID_are_not_migrated_twice()
    {
        var group = new NoteGroupData
        { GroupId = Guid.Parse("12340000-0000-0000-0000-000000000000"), Name = "Group 1234", NameVersion = DashboardGroupNaming.Version };
        Assert.False(DashboardGroupNaming.Normalize(new[] { group }));
        Assert.Equal("Group 1234", group.Name);
    }

    [Fact]
    public void Maximum_integer_names_do_not_overflow_number_generation()
    {
        var name = DashboardGroupNaming.NextName(new[] { new NoteGroupData { Name = "Group 2147483647" }, new NoteGroupData { Name = "Group 1" } });
        Assert.EndsWith(" 2", name);
    }

    [Theory]
    [InlineData(DashboardMotionScope.Notes)] [InlineData(DashboardMotionScope.Flashcards)]
    [InlineData(DashboardMotionScope.MindMaps)] [InlineData(DashboardMotionScope.Quizzes)]
    public void Each_dashboard_generates_numbered_groups_and_preserves_renames(DashboardMotionScope scope) => Sta(() =>
    {
        var original = CultureInfo.CurrentUICulture;
        var cache = typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousSettings = cache.GetValue(null); var previousDirectory = AppSettingsService.SettingsDirectoryOverride;
        var root = Path.Combine(Path.GetTempPath(), "Scriblucent-group-naming-tests", Guid.NewGuid().ToString("N"));
        MainViewModel? model = null;
        try
        {
            AppSettingsService.Flush(); LocalizationService.SetCulture("en");
            AppSettingsService.SettingsDirectoryOverride = Path.Combine(root, "settings");
            var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
            cache.SetValue(null, settings);
            model = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = root };
            Guid AddPair()
            {
                switch (scope)
                {
                    case DashboardMotionScope.Flashcards:
                        var fa = new FlashcardSetViewModel(new FlashcardSetDocument()); var fb = new FlashcardSetViewModel(new FlashcardSetDocument());
                        model.FlashcardSets.Add(fa); model.FlashcardSets.Add(fb);
                        Assert.True(model.TryGroupFlashcardSets(fa, fb)); return fb.Document.GroupId!.Value;
                    case DashboardMotionScope.MindMaps:
                        var ma = new MindMapViewModel(new MindMapDocument()); var mb = new MindMapViewModel(new MindMapDocument());
                        model.MindMaps.Add(ma); model.MindMaps.Add(mb);
                        Assert.True(model.TryGroupMindMaps(ma, mb)); return mb.Document.GroupId!.Value;
                    case DashboardMotionScope.Quizzes:
                        var qa = new QuizViewModel(new QuizDocument()); var qb = new QuizViewModel(new QuizDocument());
                        model.Quizzes.Add(qa); model.Quizzes.Add(qb);
                        Assert.True(model.TryGroupQuizzes(qa, qb)); return qb.Document.GroupId!.Value;
                    default:
                        var na = model.AddNoteFromDocument(new NoteDocument()); var nb = model.AddNoteFromDocument(new NoteDocument());
                        Assert.True(model.TryGroupNotes(na, nb)); return nb.Document.GroupId!.Value;
                }
            }
            var first = AddPair(); var second = AddPair();
            Assert.Equal("Group 1", model.GetDashboardGroupName(scope, first));
            Assert.Equal("Group 2", model.GetDashboardGroupName(scope, second));
            switch (scope)
            {
                case DashboardMotionScope.Flashcards: Assert.True(model.RenameFlashcardSetGroup(model.FlashcardSetGroups.Single(g => g.GroupId == first), "Research")); break;
                case DashboardMotionScope.MindMaps: Assert.True(model.RenameMindMapGroup(model.MindMapGroups.Single(g => g.GroupId == first), "Research")); break;
                case DashboardMotionScope.Quizzes: Assert.True(model.RenameQuizGroup(model.QuizGroups.Single(g => g.GroupId == first), "Research")); break;
                default: Assert.True(model.RenameGroup(model.NoteGroups.Single(g => g.GroupId == first), "Research")); break;
            }
            var third = AddPair();
            Assert.Equal("Group 3", model.GetDashboardGroupName(scope, third));
            Assert.Equal("Research", model.GetDashboardGroupName(scope, first));
            Assert.Equal("Group 2", model.GetDashboardGroupName(scope, second));
        }
        finally
        {
            // Let the note writer complete before restoring settings used by other fixtures.
            var pending = typeof(MainViewModel).GetField("_notesWriteInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (model is not null && (bool)pending.GetValue(model)! && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Background);
                Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
            AppSettingsService.Flush(); AppSettingsService.SettingsDirectoryOverride = previousDirectory; cache.SetValue(null, previousSettings);
            LocalizationService.SetCulture(original.Name);
        }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
