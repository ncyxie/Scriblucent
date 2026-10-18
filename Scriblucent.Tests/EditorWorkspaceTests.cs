using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Animations;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class EditorWorkspaceTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void SameGuidAcrossDocumentTypesDoesNotCollide()
    {
        var id = Guid.NewGuid();
        var keys = Enum.GetValues<EditorDocumentKind>().Select(kind => new EditorDocumentKey(kind, id)).ToHashSet();
        Assert.Equal(4, keys.Count);
        Assert.Contains(new EditorDocumentKey(EditorDocumentKind.Note, id), keys);
    }

    [Fact]
    public void Document_arrival_keeps_the_connected_header_fixed_and_settles_inactive_content() => Sta(() =>
    {
        var preference = MotionPolicy.Preference;
        try
        {
            var root = new Grid();
            var header = new Border();
            var body = new Border(); Grid.SetRow(body, 1);
            var footer = new Border(); Grid.SetRow(footer, 2); WindowMotion.SetRole(footer, WindowMotionRole.Footer);
            root.Children.Add(header); root.Children.Add(body); root.Children.Add(footer);
            foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(mode, false);
                HostedEditorMotion.Prepare(root, true);
                Assert.Equal(1, root.Opacity); Assert.Equal(1, header.Opacity);
                Assert.True(root.RenderTransform.Value.IsIdentity); Assert.True(header.RenderTransform.Value.IsIdentity);
                Assert.Equal(mode == MotionPreference.Off ? 1 : .65, body.Opacity);
                Assert.Equal(mode == MotionPreference.Automatic ? 4 : 0, body.RenderTransform.Value.OffsetY);
                HostedEditorMotion.Settle(root);
                Assert.Equal(1, body.Opacity); Assert.Equal(1, footer.Opacity);
                Assert.True(body.RenderTransform.Value.IsIdentity); Assert.True(footer.RenderTransform.Value.IsIdentity);
            }
        }
        finally { MotionPolicy.Configure(preference, false); }
    });

    [Fact]
    public void WorkingCopiesAndPersistedSnapshotsDoNotShareMutableNodesOrAttempts()
    {
        var original = new MindMapDocument { Root = new MindMapNode { Text = "Root", Children = [new MindMapNode { Text = "Child" }] } };
        var working = EditorHosting.Clone(original);
        var saved = EditorHosting.Clone(working);
        working.Root.Children[0].Text = "Unsaved";
        Assert.Equal("Child", original.Root.Children[0].Text);
        Assert.Equal("Child", saved.Root.Children[0].Text);
        Assert.Equal(original.Root.Children[0].Id, working.Root.Children[0].Id);
        var quiz = new QuizDocument { Questions = [new QuizQuestion { Options = [new QuizOption { Text = "Answer" }] }] };
        var quizWorking = EditorHosting.Clone(quiz);
        quizWorking.Questions[0].Options[0].Text = "Uncommitted";
        Assert.Equal("Answer", quiz.Questions[0].Options[0].Text);
        Assert.NotSame(quiz.Attempts, quizWorking.Attempts);
        var note = new NoteDocument { Content = new string('x', 100_000), Images = [new NoteImageAttachment { Left = 10 }],
            EditHistory = [new NoteEditHistoryEntry { Content = "Previous" }], Schedules = [new NoteScheduleEntry { Note = "Reminder" }], Tags = ["Tag"] };
        var noteWorking = EditorHosting.Clone(note);
        Assert.Same(note.Content, noteWorking.Content);
        noteWorking.Images[0].Left = 99; noteWorking.EditHistory[0].Content = "Unsaved";
        noteWorking.Schedules[0].Note = "Unsaved"; noteWorking.Tags[0] = "Other";
        Assert.Equal(10, note.Images[0].Left);
        Assert.Equal("Previous", note.EditHistory[0].Content);
        Assert.Equal("Reminder", note.Schedules[0].Note);
        Assert.Equal("Tag", note.Tags[0]);
    }

    [Fact]
    public void FractionalCommandBandDoesNotWrapItsLastActionUntilRequested() => Sta(() =>
    {
        var panel = new EditorCommandPanel();
        foreach (var width in new[] { 37.6, 43.2, 38.4 }) panel.Children.Add(new Border { Width = width, Height = 32 });
        panel.Measure(new Size(double.PositiveInfinity, 100));
        panel.Arrange(new Rect(0, 0, panel.DesiredSize.Width - .4, panel.DesiredSize.Height));
        Assert.Equal(32, panel.DesiredSize.Height);
        Assert.Equal(0, panel.Children[2].TranslatePoint(new Point(), panel).Y);
        panel.CanWrap = true;
        panel.Measure(new Size(81, 100)); panel.Arrange(new Rect(0, 0, 81, panel.DesiredSize.Height));
        Assert.Equal(64, panel.DesiredSize.Height);
        Assert.Equal(32, panel.Children[2].TranslatePoint(new Point(), panel).Y);
    });

    [Fact]
    public void FailedWritesLeaveDashboardAtLastConfirmedVersionAndKeepDraftIdentity() => Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Scriblucent-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocker, "blocked");
        var model = new MainViewModel(new AppSettings { EnableAutoSave = false }, false, false)
            { PersistenceDirectoryOverride = directory };
        try
        {
            var flash = new FlashcardSetDocument { Title = "Saved cards", Cards = [new FlashcardItem { Question = "Question", Answer = "Answer" }] };
            var quiz = new QuizDocument { Title = "Saved quiz" };
            var map = new MindMapDocument { Title = "Saved map", Root = new MindMapNode { Text = "Saved root" } };
            Assert.True(model.CommitEditorFlashcards(flash));
            Assert.True(model.CommitEditorQuiz(quiz));
            Assert.True(model.CommitEditorMindMap(map));
            model.PersistenceDirectoryOverride = blocker;
            var changedFlash = EditorHosting.Clone(flash); changedFlash.Title = "Unsaved cards";
            var changedQuiz = EditorHosting.Clone(quiz); changedQuiz.Title = "Unsaved quiz";
            var changedMap = EditorHosting.Clone(map); changedMap.Root.Text = "Unsaved root";
            Assert.False(model.CommitEditorFlashcards(changedFlash));
            Assert.False(model.CommitEditorQuiz(changedQuiz));
            Assert.False(model.CommitEditorMindMap(changedMap));
            Assert.Equal("Saved cards", model.FlashcardSets.Single().Document.Title);
            Assert.Equal("Saved quiz", model.Quizzes.Single().Document.Title);
            Assert.Equal("Saved root", model.MindMaps.Single().Document.Root.Text);
            Assert.False(model.CommitEditorFlashcards(new FlashcardSetDocument { Title = "Draft" }));
            Assert.Single(model.FlashcardSets);
            model.PersistenceDirectoryOverride = directory;
            Assert.True(model.CommitEditorFlashcards(changedFlash));
            Assert.Single(model.FlashcardSets);
            Assert.Equal(flash.Id, model.FlashcardSets[0].Document.Id);
            Assert.Contains("Unsaved cards", File.ReadAllText(Path.Combine(directory, "flashcard-sets.json")));
        }
        finally
        {
            // The test owns this generated temporary directory; no app data is used.
            Directory.Delete(directory, recursive: true);
        }
    });
}
