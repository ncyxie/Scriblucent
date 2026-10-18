using System.Reflection;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class BulkRenameTests
{
    [Fact]
    public void Rename_to_sets_the_complete_name_without_numbering_and_preserves_sources()
    {
        var targets = new[] { new RenameTarget(Guid.NewGuid(), "One"), new RenameTarget(Guid.NewGuid(), "Two") };
        var preview = BulkRename.Preview(targets, new(BulkRenameMode.RenameTo, "", "  New name  ", "Ignored", "Ignored", int.MaxValue));
        Assert.All(preview, change => Assert.Equal("New name", change.After));
        Assert.Equal(targets.Select(target => target.Id), preview.Select(change => change.Id));
        Assert.Equal(new[] { "One", "Two" }, targets.Select(target => target.Name));
        Assert.Throws<ArgumentException>(() => BulkRename.Preview(targets, new(BulkRenameMode.RenameTo, "", " \r\n ", "", "")));
    }
    [Fact]
    public void Preview_is_literal_case_insensitive_and_does_not_mutate_sources()
    {
        var targets = new[] { new RenameTarget(Guid.NewGuid(), "Topic [old]"), new RenameTarget(Guid.NewGuid(), "[OLD] detail") };
        var preview = BulkRename.Preview(targets, new(BulkRenameMode.Replace, "[old]", "$new", "", ""));
        Assert.Equal(new[] { "Topic $new", "$new detail" }, preview.Select(change => change.After));
        Assert.Equal("Topic [old]", targets[0].Name); Assert.Equal(targets.Select(item => item.Id), preview.Select(item => item.Id));
    }
    [Fact]
    public void Prefixes_suffixes_and_numbered_names_preserve_selection_order()
    {
        var targets = new[] { new RenameTarget(Guid.NewGuid(), "One"), new RenameTarget(Guid.NewGuid(), "Two") };
        Assert.Equal(new[] { "[One]", "[Two]" }, BulkRename.Preview(targets, new(BulkRenameMode.Affixes, "", "", "[", "]")).Select(change => change.After));
        Assert.Equal(new[] { "Chapter 8", "Chapter 9" }, BulkRename.Preview(targets, new(BulkRenameMode.Numbered, "", "Chapter", "", "", 8)).Select(change => change.After));
        Assert.Single(BulkRename.Preview(targets[..1], new(BulkRenameMode.Numbered, "", "Topic", "", "", int.MaxValue)));
    }
    [Fact]
    public void Invalid_rules_never_return_a_partial_plan()
    {
        var targets = new[] { new RenameTarget(Guid.NewGuid(), "One"), new RenameTarget(Guid.NewGuid(), "Two") };
        Assert.Throws<ArgumentException>(() => BulkRename.Preview(targets, new(BulkRenameMode.Replace, "", "new", "", "")));
        Assert.Throws<ArgumentException>(() => BulkRename.Preview(targets, new(BulkRenameMode.Replace, "Two", "", "", "")));
        Assert.Throws<ArgumentException>(() => BulkRename.Preview(targets, new(BulkRenameMode.Numbered, "", "Topic", "", "", int.MaxValue)));
    }
    [Theory]
    // Notes' coalesced writer needs the real Application dispatcher; the window probe covers it.
    [InlineData(EditorDocumentKind.Flashcards)]
    [InlineData(EditorDocumentKind.MindMap)] [InlineData(EditorDocumentKind.Quiz)]
    public void Dashboard_rename_persists_the_complete_selection_and_rolls_back_failed_writes(EditorDocumentKind kind) => Sta(() =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Scriblucent-rename-tests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var vm = new MainViewModel(new AppSettings { EnableAutoSave = false, EnableAiFeatures = false }, false, false) { PersistenceDirectoryOverride = directory };
        string dashboard = kind switch { EditorDocumentKind.Note => "Notes", EditorDocumentKind.Flashcards => "Flashcards", EditorDocumentKind.MindMap => "MindMaps", _ => "Quizzes" };
        typeof(MainViewModel).GetField("_activeDashboard", flags)!.SetValue(vm, dashboard);
        typeof(MainViewModel).GetField("_is" + dashboard + "DashboardReady", flags)!.SetValue(vm, true);
        typeof(MainViewModel).GetField("_notesLoadComplete", flags)!.SetValue(vm, true);
        try
        {
            for (int i = 0; i < 3; i++) vm.QuickAddDashboardItemCommand.Execute(null);
            PumpUntil(() => !new[] { "_saveNotesQueued", "_notesWriteInFlight", "_notesWritePending" }.Any(name => (bool)typeof(MainViewModel).GetField(name, flags)!.GetValue(vm)!));
            vm.DashboardSelection.SelectAll();
            var targets = vm.SelectedDashboardRenameTargets(); Assert.Equal(3, targets.Count);
            var plan = BulkRename.Preview(targets, new(BulkRenameMode.Numbered, "", "Chosen", "", "", 3));
            Assert.True(vm.TryRenameDashboardSelection(plan));
            Assert.Equal(plan.Select(change => change.After).Order(), vm.SelectedDashboardRenameTargets().Select(target => target.Name).Order());
            var filename = kind switch { EditorDocumentKind.Note => "notes.json", EditorDocumentKind.Flashcards => "flashcard-sets.json", EditorDocumentKind.MindMap => "mind-maps.json", _ => "quizzes.json" };
            var before = File.ReadAllText(Path.Combine(directory, filename));
            foreach (var change in plan) Assert.Contains(change.After, before);
            var blocker = Path.Combine(directory, "blocked"); File.WriteAllText(blocker, "fixture"); vm.PersistenceDirectoryOverride = blocker;
            var failed = BulkRename.Preview(vm.SelectedDashboardRenameTargets(), new(BulkRenameMode.Affixes, "", "", "Failed ", ""));
            Assert.False(vm.TryRenameDashboardSelection(failed));
            Assert.Equal(plan.Select(change => change.After).Order(), vm.SelectedDashboardRenameTargets().Select(target => target.Name).Order());
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, filename)));
            Assert.Equal(3, vm.DashboardSelection.Count);
        }
        finally
        {
            vm.PersistenceDirectoryOverride = directory; vm.CancelPendingDashboardLoads();
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(resolved, true);
        }
    });
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(ready());
    }
}
