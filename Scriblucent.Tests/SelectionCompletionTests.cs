using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[CollectionDefinition("Selection completion", DisableParallelization = true)]
public class SelectionCompletionCollection;

[Collection("Selection completion")]
public class SelectionCompletionTests : IDisposable
{
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Scriblucent-selection-completion-tests"));
    private readonly string _folder = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public SelectionCompletionTests() => Directory.CreateDirectory(_folder);
    public void Dispose()
    {
        var resolved = Path.GetFullPath(_folder);
        if (!resolved.StartsWith(TestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
    [Fact]
    public void Picker_ranges_keep_hidden_choices_and_matching_clear_is_not_global_clear()
    {
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new EditorSelectionController(); selection.SetItems(ids, ids);
        selection.Toggle(ids[7]); selection.Toggle(ids[0]);
        selection.SetItems(ids, ids[..5]); selection.Toggle(ids[1]); selection.Toggle(ids[4], true);
        Assert.Equal(new[] { ids[7], ids[1], ids[2], ids[3], ids[4] }, selection.SelectedIds);
        Assert.Equal(4, selection.MatchingSelectedCount);
        selection.SelectAll(); Assert.Equal(6, selection.Count); Assert.Equal(5, selection.MatchingSelectedCount);
        selection.ClearMatching(); Assert.Equal(new[] { ids[7] }, selection.SelectedIds);
        selection.Clear(); Assert.Empty(selection.SelectedIds);
    }
    [Fact]
    public void Picker_filter_resets_hidden_anchor_and_prunes_deleted_items()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new EditorSelectionController(); selection.SetItems(ids, ids); selection.Toggle(ids[0]);
        selection.SetItems(ids, ids[2..]); selection.Toggle(ids[4], range: true);
        Assert.Equal(new[] { ids[0], ids[4] }, selection.SelectedIds);
        selection.SetItems(ids[2..], ids[2..]); Assert.Equal(new[] { ids[4] }, selection.SelectedIds);
    }
    [Fact]
    public void Logical_select_all_does_not_depend_on_realized_rows()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new EditorSelectionController(); selection.SetItems(ids, ids.Where((_, index) => index % 2 == 0)); selection.SelectAll();
        Assert.Equal(10_000, selection.Count); Assert.Equal(10_000, selection.MatchingSelectedCount);
        selection.SetItems(ids, ids.Where((_, index) => index % 2 != 0));
        Assert.Equal(10_000, selection.Count); Assert.Equal(0, selection.MatchingSelectedCount);
    }
    [Fact]
    public void Delete_promotes_unselected_descendants_at_the_original_tree_position()
    {
        var first = new MindMapNode { Text = "first" }; var keep = new MindMapNode { Text = "keep" };
        var inner = new MindMapNode { Text = "inner", Children = [keep] };
        var outer = new MindMapNode { Text = "outer", Children = [inner] };
        var last = new MindMapNode { Text = "last", CrossLinkTargetIds = [inner.Id, keep.Id] };
        var root = new MindMapNode { Children = [first, outer, last] };
        MindMapSelectionOperations.DeleteNodes(root, new HashSet<MindMapNode> { outer, inner });
        Assert.Equal(new[] { first, keep, last }, root.Children); Assert.Equal(new[] { keep.Id }, last.CrossLinkTargetIds);
        Assert.Throws<InvalidOperationException>(() => MindMapSelectionOperations.DeleteNodes(root, new HashSet<MindMapNode> { root }));
        Assert.Equal(3, root.Children.Count);
    }
    [Fact]
    public void Copy_normalizes_branches_and_remaps_links_across_the_entire_forest()
    {
        var child = new MindMapNode { Text = "child", Note = "note", FontSize = 19 };
        var left = new MindMapNode { Text = "left", Children = [child] }; var right = new MindMapNode { Text = "right" };
        var external = new MindMapNode(); child.CrossLinkTargetIds = [right.Id, external.Id];
        var root = new MindMapNode { Children = [left, right, external] };
        var roots = MindMapSelectionOperations.Roots(root, new HashSet<MindMapNode> { left, child, right });
        Assert.Equal(new[] { left, right }, roots);
        var copied = MindMapSelectionOperations.CloneForest(roots);
        Assert.Equal(new[] { copied[1].Id, external.Id }, copied[0].Children[0].CrossLinkTargetIds);
        Assert.Equal("note", copied[0].Children[0].Note); Assert.Equal(19, copied[0].Children[0].FontSize);
        var pastedElsewhere = MindMapSelectionOperations.CloneForest(copied, false);
        Assert.Equal(new[] { pastedElsewhere[1].Id }, pastedElsewhere[0].Children[0].CrossLinkTargetIds);
        Assert.NotEqual(copied[0].Id, pastedElsewhere[0].Id); Assert.NotEqual(left.Id, copied[0].Id);
    }
    [Fact]
    public void Cut_removes_whole_branches_once_and_prunes_dangling_links()
    {
        var leaf = new MindMapNode(); var branch = new MindMapNode { Children = [leaf] };
        var keep = new MindMapNode { CrossLinkTargetIds = [leaf.Id] }; var root = new MindMapNode { Children = [branch, keep] };
        MindMapSelectionOperations.CutBranches(root, new HashSet<MindMapNode> { branch, leaf });
        Assert.Equal(new[] { keep }, root.Children); Assert.Empty(keep.CrossLinkTargetIds); Assert.Single(branch.Children);
    }
    [Fact]
    public void Duplicate_inserts_independent_branches_after_sources_and_remaps_links()
    {
        var leaf = new MindMapNode { Text = "leaf", Note = "authored note", LinkedNoteId = Guid.NewGuid(), Icons = ["Star"], FontSize = 18 };
        var left = new MindMapNode { Text = "left", IsExpanded = false, Children = [leaf] };
        var right = new MindMapNode { Text = "right" };
        var keep = new MindMapNode { Text = "keep" };
        leaf.CrossLinkTargetIds = [right.Id, keep.Id];
        var root = new MindMapNode { Children = [left, right, keep] };
        var copies = MindMapSelectionOperations.DuplicateBranches(root, new HashSet<MindMapNode> { left, leaf, right });
        Assert.Equal(2, copies.Count);
        Assert.Equal(new[] { left, copies[0], right, copies[1], keep }, root.Children);
        var copiedLeaf = Assert.Single(copies[0].Children);
        Assert.Equal(new[] { copies[1].Id, keep.Id }, copiedLeaf.CrossLinkTargetIds);
        Assert.False(copies[0].IsExpanded);
        Assert.Equal(leaf.LinkedNoteId, copiedLeaf.LinkedNoteId);
        Assert.Equal(leaf.Note, copiedLeaf.Note);
        Assert.Equal(18, copiedLeaf.FontSize);
        Assert.Equal(leaf.Icons, copiedLeaf.Icons);
        var all = MindMapSelectionOperations.Nodes(root).ToArray();
        Assert.Equal(all.Length, all.Select(node => node.Id).Distinct().Count());
        copiedLeaf.Text = "copy only"; copiedLeaf.Icons.Clear(); copiedLeaf.CrossLinkTargetIds.Clear();
        Assert.Equal("leaf", leaf.Text); Assert.Single(leaf.Icons); Assert.Equal(2, leaf.CrossLinkTargetIds.Count);
    }
    [Fact]
    public void Duplicate_keeps_each_parent_and_protects_the_unique_root()
    {
        var a = new MindMapNode { Text = "a" }; var b = new MindMapNode { Text = "b" };
        var left = new MindMapNode { Children = [a] }; var right = new MindMapNode { Children = [b] };
        var root = new MindMapNode { Children = [left, right] };
        Assert.Empty(MindMapSelectionOperations.DuplicateBranches(root, new HashSet<MindMapNode> { root, a }));
        Assert.Single(left.Children); Assert.Single(right.Children);
        var copies = MindMapSelectionOperations.DuplicateBranches(root, new HashSet<MindMapNode> { a, b });
        Assert.Equal(new[] { a, copies[0] }, left.Children);
        Assert.Equal(new[] { b, copies[1] }, right.Children);
        Assert.Empty(MindMapSelectionOperations.DuplicateBranches(root, new HashSet<MindMapNode>()));
    }
    [Fact]
    public void Card_subset_keeps_native_progress_media_and_filters_session_references()
    {
        var a = new FlashcardItem { Question = "keep", Repetitions = 7, QuestionAudioFileName = "clip.wav", SetIndex = 2 };
        var b = new FlashcardItem { Question = "omit" };
        var source = new FlashcardSetDocument { Cards = [a, b], SetNames = new() { [1] = "other", [2] = "chosen" }, StudySession = new() { CurrentCardId = b.Id, History = [a.Id, b.Id, a.Id], HistoryPosition = 2 } };
        var subset = (FlashcardSetDocument)SelectionExportService.Subset(source, new HashSet<Guid> { a.Id });
        Assert.Single(subset.Cards); Assert.Equal(7, subset.Cards[0].Repetitions); Assert.Equal("clip.wav", subset.Cards[0].QuestionAudioFileName);
        Assert.Equal(new[] { a.Id, a.Id }, subset.StudySession.History); Assert.Equal(1, subset.StudySession.HistoryPosition);
        Assert.Equal(a.Id, subset.StudySession.CurrentCardId); Assert.Equal(2, source.Cards.Count); Assert.Equal(b.Id, source.StudySession.CurrentCardId);
    }
    [Fact]
    public void Question_subset_preserves_historical_scores_and_only_selected_session_payloads()
    {
        var a = new QuizQuestion { Question = "keep", LastAttemptCorrect = true }; var b = new QuizQuestion { Question = "omit" };
        var source = new QuizDocument { Questions = [a, b], Attempts = [new() { TotalQuestions = 2, CorrectCount = 1 }], ProgressSession = new() { Questions = [b, a], CurrentQuestionIndex = 1, FlaggedQuestionIds = [b.Id, a.Id], PlainTextAnswersByQuestionId = new() { [b.Id] = "omit", [a.Id] = "keep" } } };
        var subset = (QuizDocument)SelectionExportService.Subset(source, new HashSet<Guid> { a.Id });
        Assert.True(subset.Questions.Single().LastAttemptCorrect); Assert.Equal(2, subset.Attempts.Single().TotalQuestions);
        Assert.Equal(a.Id, subset.ProgressSession!.Questions.Single().Id); Assert.Equal(0, subset.ProgressSession.CurrentQuestionIndex);
        Assert.Equal(new[] { a.Id }, subset.ProgressSession.FlaggedQuestionIds); Assert.Single(subset.ProgressSession.PlainTextAnswersByQuestionId);
        Assert.Equal(2, source.ProgressSession.Questions.Count);
    }
    [Fact]
    public async Task Archive_contains_round_trippable_native_files_and_collision_safe_names()
    {
        var documents = new object[] { new NoteDocument { Title = "same", Content = "note" }, new NoteDocument { Title = "SAME", Content = "second" },
            new FlashcardSetDocument { Title = "CON", Cards = [new() { Question = "Q", Answer = "A", Repetitions = 4 }] },
            new QuizDocument { Title = "quiz", Questions = [new() { Question = "question" }] }, new MindMapDocument { Title = "map", Root = new() { Text = "root" } } };
        var path = Path.Combine(_folder, "selection.zip");
        await SelectionExportService.ExportArchiveAsync(documents.Select(DocumentExportSnapshot.Capture).ToArray(), path, CancellationToken.None);
        using var zip = ZipFile.OpenRead(path); Assert.Equal(5, zip.Entries.Count);
        Assert.Contains(zip.Entries, entry => entry.FullName == "SAME (2).scribnote"); Assert.Contains(zip.Entries, entry => entry.FullName == "_CON.scribflash");
        foreach (var entry in zip.Entries)
        {
            var file = Path.Combine(_folder, entry.FullName); entry.ExtractToFile(file);
            switch (Path.GetExtension(file))
            {
                case ".scribnote": Assert.False(string.IsNullOrEmpty(NoteFileService.ReadNotePackage(file).Content)); break;
                case ".scribquiz": Assert.Single(QuizFileService.LoadScribQuizDocument(file).Questions); break;
                case ".scribmap": Assert.Equal("root", MindMapFileService.LoadScribmapDocument(file).Root.Text); break;
                case ".scribflash": Assert.Equal(4, FlashcardFileService.LoadScribFlashDocument(file).Cards.Single().Repetitions); break;
            }
        }
    }
    [Fact]
    public async Task Archive_embeds_file_backed_note_images_without_mutating_the_source()
    {
        var media = Path.Combine(_folder, "note-media"); Directory.CreateDirectory(media);
        File.WriteAllBytes(Path.Combine(media, "photo.png"), [1, 2, 3, 4]);
        var note = new NoteDocument { Title = "Pictures", Images = [new() { DataFilePath = "photo.png", CaptionText = "Authored caption" }] };
        var snapshot = DocumentExportSnapshot.Capture(note) with { NoteMediaDirectory = media };
        var path = Path.Combine(_folder, "pictures.zip");
        await SelectionExportService.ExportArchiveAsync([snapshot], path, CancellationToken.None);
        using var zip = ZipFile.OpenRead(path); var entry = Assert.Single(zip.Entries);
        var native = Path.Combine(_folder, entry.Name); entry.ExtractToFile(native);
        var roundTrip = NoteFileService.ReadNotePackage(native);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), roundTrip.Images[0].Data);
        Assert.Empty(roundTrip.Images[0].DataFilePath); Assert.Equal("Authored caption", roundTrip.Images[0].CaptionText);
        Assert.Equal("photo.png", note.Images[0].DataFilePath); Assert.Empty(note.Images[0].Data);
    }
    [Fact]
    public async Task Native_card_export_includes_referenced_media_only()
    {
        var previous = FlashcardMediaStorageService.MediaDirectoryOverride;
        try
        {
            FlashcardMediaStorageService.MediaDirectoryOverride = Path.Combine(_folder, "media"); Directory.CreateDirectory(FlashcardMediaStorageService.MediaDirectoryOverride);
            File.WriteAllBytes(Path.Combine(FlashcardMediaStorageService.MediaDirectoryOverride, "chosen.wav"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(FlashcardMediaStorageService.MediaDirectoryOverride, "omit.wav"), [4, 5]);
            var a = new FlashcardItem { QuestionAudioFileName = "chosen.wav" }; var b = new FlashcardItem { QuestionAudioFileName = "omit.wav" };
            var subset = SelectionExportService.Subset(new FlashcardSetDocument { Cards = [a, b] }, new HashSet<Guid> { a.Id });
            var path = Path.Combine(_folder, "subset.scribflash"); await SelectionExportService.ExportDocumentAsync(subset, path, CancellationToken.None);
            using var zip = ZipFile.OpenRead(path); Assert.Contains(zip.Entries, entry => entry.FullName.Replace('\\', '/') == "media/chosen.wav");
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("omit.wav"));
        }
        finally { FlashcardMediaStorageService.MediaDirectoryOverride = previous; }
    }
    [Fact]
    public async Task Cancelled_or_failed_export_preserves_destination_and_cleans_staging_files()
    {
        var path = Path.Combine(_folder, "keep.zip"); await File.WriteAllTextAsync(path, "existing");
        var source = new NoteDocument { Title = "keep", Content = "text" }; using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SelectionExportService.ExportArchiveAsync([DocumentExportSnapshot.Capture(source)], path, cancellation.Token, progress));
        Assert.Equal("existing", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectionExportService.ExportDocumentAsync(source, path, CancellationToken.None));
        Assert.Equal("existing", await File.ReadAllTextAsync(path)); Assert.DoesNotContain(Directory.GetFiles(_folder), name => name.EndsWith(".tmp"));
    }
    [Theory]
    [InlineData("CON.txt", "_CON.txt")] [InlineData("..", "Untitled")] [InlineData("abc/def", "abc_def")]
    public void Export_names_are_valid_and_stable(string title, string expected) => Assert.Equal(expected, SelectionExportService.SafeName(title));
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<string> { public void Report(string value) => source.Cancel(); }
}
