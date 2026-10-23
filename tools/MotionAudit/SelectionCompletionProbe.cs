using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static T CompletionField<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void CompletionSet(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void CompletionAwait(Task task)
    {
        WaitLoading(() => task.IsCompleted, 30000); task.GetAwaiter().GetResult();
    }
    private static void ProbeSelectionCompletion(string? only)
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        FlashcardMediaStorageService.MediaDirectoryOverride = Path.Combine(_output, "media");
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var checks = new List<string>();
        foreach (var (theme, mode) in new[] { ("Light", MotionPreference.Full), ("Dark", MotionPreference.Reduced), ("Light", MotionPreference.Off) })
        {
            if (only is not null && only != $"{theme}/{mode}") continue;
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(theme == "Dark" ? "lt" : "en"); MotionPolicy.Configure(mode, false);
            var suffix = $"{theme}-{mode}";
            var vm = new MainViewModel(settings, loadPersistedContent: false, applyThemeAndCulture: false)
            { PersistenceDirectoryOverride = Path.Combine(_output, "stores", suffix) };
            Directory.CreateDirectory(vm.PersistenceDirectoryOverride);
            CompletionMindMap(suffix, checks);
            CompletionPickers(vm, suffix, checks);
            CompletionTrash(suffix, checks);
            if (mode == MotionPreference.Full) CompletionExports(vm, suffix, checks);
        }
        Pump(500); Require(!RenderTransitionClock.IsAnyRunning, "Selection completion left running motion after disposal.");
        File.WriteAllLines(Path.Combine(_output, "completion-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void CompletionMindMap(string suffix, List<string> checks)
    {
        var child = new MindMapNode { Text = "Child to preserve" };
        var left = new MindMapNode { Text = "Left branch", Children = [child], BackgroundColor = "#D8E8FF", FontSize = 19 };
        var right = new MindMapNode { Text = "Right branch", Note = "Keep this note" };
        var folded = new MindMapNode { Text = "Folded", IsExpanded = false, Children = [new() { Text = "Hidden child" }] };
        var root = new MindMapNode { Text = "Selection fixture", Children = [left, right, folded] };
        child.CrossLinkTargetIds = [right.Id];
        var map = new MindMapPreviewWindow(root, useManualPositions: true) { Width = 850, Height = 740, ShowInTaskbar = false };
        try
        {
            map.Show(); map.Activate(); Pump(450);
            var canvas = (Canvas)map.FindName("MapCanvas");
            var scope = CompletionField<ICollectionSelectionScope>(map, "_nodeSelectionScope");
            var dock = (CollectionSelectionDock)map.FindName("NodeSelectionDock");
            var undo = CompletionField<List<MindMapNode>>(map, "_undoStack");
            scope.SelectAll(); Pump(280);
            Require(scope.Count == 4 && scope.TotalCount == 4 && dock.IsVisible, "Mind-map all did not select visible non-root logical nodes.");
            scope.Clear(); scope.Toggle(left.Id); scope.Toggle(child.Id); scope.Toggle(right.Id); Pump(180);
            Require(dock.IsVisible && ((CheckBox)dock.FindName("AllToggle")).IsChecked is null, "Mind-map partial selection has wrong island state.");
            int baseline = undo.Count;
            SelectionClick((Button)dock.FindName("DuplicateButton"));
            Require(undo.Count == baseline && !map.WorkspaceDirty, "Copy changed content or undo.");
            scope.Begin(folded.Id); // Begin intentionally selects one target for paste.
            RemainingCall(map, "PasteSelectedBranches", folded); Pump(200);
            Require(folded.Children.Count == 3 && undo.Count == baseline + 1, "Forest paste did not make one undo step.");
            var pastedLeft = folded.Children[1]; var pastedRight = folded.Children[2];
            Require(pastedLeft.Children[0].CrossLinkTargetIds.SequenceEqual(new[] { pastedRight.Id }) && pastedLeft.Id != left.Id,
                "Forest paste lost internal cross-branch links or reused IDs.");
            Require(scope.Count == 2, "Pasted roots were not selected.");
            RemainingCall(map, "Undo"); Pump(150);
            Require(root.Children.Single(node => node.Id == folded.Id).Children.Count == 1, "Paste undo did not restore original tree.");
            RemainingCall(map, "Redo"); Pump(150);
            Require(root.Children.Single(node => node.Id == folded.Id).Children.Count == 3, "Paste redo failed.");
            RemainingCall(map, "Undo"); Pump(100);
            left = root.Children.Single(node => node.Id == left.Id); right = root.Children.Single(node => node.Id == right.Id); child = left.Children.Single();
            scope.Clear(); scope.Toggle(left.Id); scope.Toggle(right.Id);
            CancelSelectionConfirmation(dock);
            Require(root.Children.Contains(left) && scope.Count == 2, "Cancelled node delete changed tree or selection.");
            ConfirmCompletionDelete(dock); Pump(180);
            Require(root.Children[0].Id == child.Id && !root.Children.Any(node => node.Id == left.Id || node.Id == right.Id), "Node delete did not promote the unselected child.");
            RemainingCall(map, "Undo"); Pump(100);
            left = root.Children.Single(node => node.Id == left.Id); right = root.Children.Single(node => node.Id == right.Id);
            scope.Clear(); scope.Toggle(root.Id); scope.Toggle(left.Id); Pump(160);
            Require(!((Button)dock.FindName("DeleteButton")).IsEnabled && !((Button)dock.FindName("SecondaryButton")).IsEnabled, "Root protection did not disable Cut/Delete.");
            ((Button)dock.FindName("MoreButton")).Focus();
            var protectedDelete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(map), Environment.TickCount, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent };
            dock.RaiseEvent(protectedDelete);
            Require(protectedDelete.Handled && root.Children.Contains(left), "Island keyboard Delete bypassed root protection.");
            scope.Clear(); scope.Toggle(left.Id); scope.Toggle(right.Id);
            baseline = undo.Count; SelectionClick((Button)dock.FindName("SecondaryButton")); Pump(100);
            Require(undo.Count == baseline + 1 && !root.Children.Any(node => node.Id == left.Id || node.Id == right.Id), "Cut failed or created multiple undo steps.");
            RemainingCall(map, "Undo"); Pump(100);
            left = root.Children.Single(node => node.Id == left.Id); right = root.Children.Single(node => node.Id == right.Id);
            scope.Clear(); scope.Toggle(left.Id); scope.Toggle(right.Id);
            RemainingCall(map, "CopyNodeStyle", left); baseline = undo.Count;
            RemainingCall(map, "PasteNodeStyle", right); Pump(100);
            Require(right.BackgroundColor == left.BackgroundColor && right.Note == "Keep this note" && undo.Count == baseline + 1, "Bulk style lost metadata or transaction boundary.");
            // Begin/move/finalize are the actual drag pipeline, without moving the desktop pointer.
            var border = canvas.Children.OfType<Border>().Single(item => ReferenceEquals(item.Tag, left));
            var other = canvas.Children.OfType<Border>().Single(item => ReferenceEquals(item.Tag, right));
            var start = new Point(Canvas.GetLeft(border), Canvas.GetTop(border));
            var otherStart = new Point(Canvas.GetLeft(other), Canvas.GetTop(other));
            CompletionSet(map, "_dragStartMousePos", new Point(100, 100)); CompletionSet(map, "_dragStartNodePos", start);
            baseline = undo.Count;
            RemainingCall(map, "BeginManualNodeDrag", left, border);
            RemainingCall(map, "UpdateManualNodeDragPosition", new Point(134, 121));
            RemainingCall(map, "FinalizeManualNodeDrag", left, border, new Point(134, 121)); Pump(160);
            Require(Math.Abs(left.ManualX!.Value - start.X - 34) < .1 && Math.Abs(right.ManualX!.Value - otherStart.X - 34) < .1 &&
                Math.Abs(left.ManualY!.Value - start.Y - 21) < .1 && Math.Abs(right.ManualY!.Value - otherStart.Y - 21) < .1 && undo.Count == baseline + 1,
                "Manual group drag changed relative positions or undo boundary.");
            SaveControlCapture((FrameworkElement)map.Content, "mindmap-multiselect-" + suffix);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(map), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
            ((ScrollViewer)map.FindName("MapScrollViewer")).RaiseEvent(escape);
            Require(escape.Handled && scope.Count == 0, "Canvas Escape did not clear selection.");
            checks.Add(suffix + ": node forest copy/cut/paste, IDs/cross-links, one-step undo/redo, delete cancellation/child promotion, root protection, bulk style, group drag and Escape passed.");
        }
        finally { map.DisposeWorkspaceEditor(); Pump(160); }
    }
    private static void ConfirmCompletionDelete(CollectionSelectionDock dock)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (_app.Windows.OfType<ModernMessageDialog>().FirstOrDefault() is { } dialog)
            { timer.Stop(); SelectionClick((Button)dialog.FindName("PrimaryButton")); }
        };
        timer.Start(); try { SelectionClick((Button)dock.FindName("DeleteButton")); } finally { timer.Stop(); }
    }
    private static void CompletionPickers(MainViewModel vm, string suffix, List<string> checks)
    {
        foreach (int index in Enumerable.Range(0, 180))
        {
            vm.FlashcardSets.Add(new(new FlashcardSetDocument { Title = $"Fixture {index:D3}", Cards = [new() { Question = $"Q{index}", Answer = "A" }] }));
            vm.Quizzes.Add(new(new QuizDocument { Title = $"Fixture {index:D3}", Questions = [new() { Question = $"Q{index}" }] }));
        }
        using (var fixture = new CompletionWindow(new FlashcardCombineWindow(vm)))
        {
            var window = (FlashcardCombineWindow)fixture.Window;
            var binding = CompletionField<PickerSelectionBinding<FlashcardCombineWindow.FlashcardCombineItem>>(window, "_pickerSelection");
            CompletionPicker(window, binding.Selection, window.Sets.Select(item => item.Document.Id).ToArray(),
                () => window.SearchText = "Fixture 00", () => window.SearchText = "", () => window.SelectedSets.Count, () => window.VisibleSets.Count, checks, suffix + "/combine");
            Require(window.Sets.All(item => item.IsSelected == binding.Selection.Contains(item.Document.Id)), "Combine checkbox flags diverged from basket.");
            binding.Selection.Toggle(window.Sets[0].Document.Id);
            CompletionSet(binding, "_syncing", true); window.Sets[0].IsSelected = false; CompletionSet(binding, "_syncing", false);
            binding.Selection.Toggle(window.Sets[0].Document.Id, range: true);
            Require(window.Sets[0].IsSelected && window.SelectedSets.Count == 1, "Shift checkbox left a retained logical item visually unchecked.");
        }
        using (var fixture = new CompletionWindow(new QuizLibraryWindow(vm)))
        {
            var window = (QuizLibraryWindow)fixture.Window;
            var binding = CompletionField<PickerSelectionBinding<QuizLibraryItemViewModel>>(window, "_pickerSelection");
            CompletionPicker(window, binding.Selection, window.Quizzes.Select(item => item.Document.Id).ToArray(),
                () => window.QuizSearchQuery = "Fixture 00", () => window.QuizSearchQuery = "", () => window.SelectedQuizzes.Count, () => window.VisibleQuizzes.Count, checks, suffix + "/library");
            Require(window.Quizzes.All(item => item.IsSelected == binding.Selection.Contains(item.Document.Id)), "Library checkbox flags diverged from basket.");
        }
    }
    private static void CompletionPicker(Window window, EditorSelectionController scope, Guid[] ids, Action filter, Action unfilter,
        Func<int> selected, Func<int> visible, List<string> checks, string suffix)
    {
        scope.Toggle(ids[^1]); filter(); WaitLoading(() => visible() == 10); Pump(120);
        Require(selected() == 1 && scope.MatchingSelectedCount == 0, "Filter cleared the off-filter basket.");
        scope.Toggle(ids[0]); scope.Toggle(ids[3], range: true); Pump(100);
        Require(scope.Count == 5 && selected() == 5 && scope.MatchingSelectedCount == 4, "Picker range or basket count failed.");
        ((ICombineWorkspaceHost)window).Workspace.SelectAllCommand.Execute(null); Pump(100);
        Require(selected() == 11 && scope.MatchingSelectedCount == 10, "Matching select-all ignored logical/offscreen rows.");
        ((ICombineWorkspaceHost)window).Workspace.SelectAllCommand.Execute(null); Pump(100);
        Require(selected() == 1 && scope.SelectedIds.Single() == ids[^1], "Matching clear discarded hidden selection.");
        unfilter(); WaitLoading(() => visible() == ids.Length); Pump(150);
        var checkbox = ControlTree(window).OfType<CheckBox>().First(item => item.DataContext is FlashcardCombineWindow.FlashcardCombineItem or QuizLibraryItemViewModel);
        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(checkbox);
        ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle(); Pump(120);
        Require(selected() == 2 && checkbox.IsChecked == true, "Picker's native checkbox did not update selection exactly once.");
        var all = (bool?)window.GetType().GetProperty("PickerAllChecked")!.GetValue(window);
        Require(all is null, "Picker header lost its mixed state.");
        var header = ControlTree(window).OfType<CheckBox>().Single(item => item.DataContext == window && item.Style == window.FindResource("CollectionSelectionCheckBoxStyle"));
        Require(header.IsChecked is null && ((FrameworkElement)header.Template.FindName("MixedGlyph", header)).IsVisible, "Picker mixed state did not paint.");
        Require(ControlTree(window).OfType<ContentPresenter>().Count(item => item.DataContext is FlashcardCombineWindow.FlashcardCombineItem or QuizLibraryItemViewModel) < 60, "Picker realized its entire library.");
        window.Width = 680; window.Height = 480; Pump(200); window.UpdateLayout();
        SaveControlCapture((FrameworkElement)window.Content, "picker-" + suffix.Replace('/', '-'));
        var key = SelectionKey((UIElement)window.Content, window, Key.Escape);
        Require(key.Handled && selected() == 0, "Picker Escape did not clear basket.");
        checks.Add(suffix + ": retained filtered basket, matching-all/mixed-clear, ordered range, real checkbox, keyboard clear and bounded realization passed at 680x480.");
    }
    private sealed class CompletionWindow : IDisposable
    {
        internal Window Window { get; }
        internal CompletionWindow(Window window) { Window = window; window.ShowInTaskbar = false; window.Show(); window.Activate(); Pump(300); }
        public void Dispose() { Window.Close(); Pump(200); }
    }
    private static void CompletionTrash(string suffix, List<string> checks)
    {
        var panel = new TrashPanel { Visibility = Visibility.Visible };
        var motion = (FrameworkElement)panel.FindName("PanelMotionHost"); motion.Opacity = 1; motion.RenderTransform = System.Windows.Media.Transform.Identity;
        var list = (ListBox)panel.FindName("TrashedItemsListBox");
        var items = new LazyTrashItemList(Enumerable.Range(0, 12_000).Select(i => new TrashItemEntry(TrashItemKind.Note, Guid.NewGuid(), "Deleted " + i, DateTime.UtcNow, new object())).ToArray(), 30);
        list.ItemsSource = items;
        using var fixture = new CompletionWindow(new Window { Content = panel, Width = 850, Height = 740 });
        RemainingCall(panel, "SelectAllTrashItems"); Pump(100);
        var dock = (CollectionSelectionDock)panel.FindName("TrashSelectionDock");
        var scope = CompletionField<ICollectionSelectionScope>(panel, "_trashSelection");
        var count = (TextBlock)dock.FindName("CountText");
        Require(panel.IsLogicalAllSelected && list.SelectedItems.Count == 0 && dock.IsVisible && count.Text.Contains("12"), "Trash island/select-all failed.");
        Require(SelectionKey((Button)dock.FindName("AllButton"), fixture.Window, Key.Escape).Handled && !panel.IsLogicalAllSelected && !scope.IsActive, "Trash Escape did not end logical selection.");
        list.SelectedItems.Add(items[0]); list.SelectedItems.Add(items[1]); Pump(100);
        Require(dock.IsVisible && count.Text.Contains('2'), "Trash physical selection count failed.");
        scope.Clear();
        Require(list.SelectedItems.Count == 0 && !((Button)dock.FindName("DeleteButton")).IsEnabled && scope.IsActive, "Trash Clear did not retain its mode and reset commands.");
        scope.End(); Pump(300);
        Require(items.CreatedItemCount < 200, "Trash select-all materialized all logical rows.");
        checks.Add(suffix + ": Trash count, clear and Escape passed for native selection and 12k logical select-all without materializing the library.");
    }
    private static void CompletionExports(MainViewModel vm, string suffix, List<string> checks)
    {
        vm.TrySaveNotes(); vm.EnsureEditorStoreLoaded(EditorDocumentKind.Note); WaitLoading(() => vm.IsNotesDashboardReady);
        using var fixture = new CompletionWindow(new Window { DataContext = vm, Width = 850, Height = 740 });
        _app.MainWindow = fixture.Window;
        var service = new EditorWorkspaceService(fixture.Window, vm);
        var note = vm.AddNoteFromDocument(new NoteDocument { Title = "Saved title", Content = "Saved content" });
        service.OpenNote(note); var workspace = service.Window!; workspace.ShowInTaskbar = false; Pump(300);
        try
        {
            var session = workspace.Sessions.Single();
            var rich = (RichTextBox)session.Content.FindName("ContentTextBox"); rich.CaretPosition = rich.Document.ContentEnd; rich.CaretPosition.InsertTextInRun(" Unsaved export."); Pump(80);
            bool dirty = session.IsDirty; note.Document.IsPinned = true;
            var snapshot = service.CaptureExportSnapshot(session.Key);
            Require(new NoteCardViewModel((NoteDocument)snapshot.Document, _ => { }).Content.Contains("Unsaved export.") && note.Document.Content == "Saved content" && ((NoteDocument)snapshot.Document).IsPinned && session.IsDirty == dirty,
                "Export snapshot ignored unsaved text or changed save state.");
            var snapshots = new List<DocumentExportSnapshot> { snapshot };
            foreach (var kind in new[] { EditorDocumentKind.Flashcards, EditorDocumentKind.Quiz, EditorDocumentKind.MindMap })
                vm.EnsureEditorStoreLoaded(kind);
            WaitLoading(() => CompletionField<bool>(vm, "_isFlashcardsDashboardReady") && CompletionField<bool>(vm, "_isQuizzesDashboardReady") && CompletionField<bool>(vm, "_isMindMapsDashboardReady"));
            var cards = new FlashcardSetDocument { Title = "Saved cards", IsPinned = true, Cards = [new() { Question = "Authored", Answer = "Answer", Repetitions = 7 }] };
            vm.FlashcardSets.Add(new(cards)); service.OpenFlashcards(cards); Pump(200);
            var savedQuiz = new QuizDocument { Title = "Saved quiz", IsPinned = true, Questions = [new() { Question = "Authored", Type = QuizQuestionType.PlainText, PlainTextAnswer = "Answer" }] };
            vm.Quizzes.Add(new(savedQuiz)); service.OpenQuiz(savedQuiz); Pump(200);
            var savedMap = new MindMapDocument { Title = "Saved map", IsPinned = true, Root = new() { Text = "Authored", Children = [new() { Text = "Child" }] } };
            vm.MindMaps.Add(new(savedMap)); service.OpenMindMap(savedMap); Pump(200);
            foreach (var item in workspace.Sessions.Where(item => item.Key.Kind != EditorDocumentKind.Note))
            {
                workspace.FocusDocument(item.Key); Pump(100);
                var titleBox = (TextBox)item.Content.FindName("TitleTextBox"); titleBox.Text = "Unsaved " + item.Key.Kind; titleBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); Pump(100);
                bool wasDirty = item.IsDirty;
                string? mapBefore = item.Editor is MindMapPreviewWindow mapEditor ? System.Text.Json.JsonSerializer.Serialize(CompletionField<MindMapNode>(mapEditor, "_root")) : null;
                var captured = service.CaptureExportSnapshot(item.Key); snapshots.Add(captured);
                Require(captured.Title == "Unsaved " + item.Key.Kind && item.IsDirty == wasDirty, "Working export snapshot changed save state or lost the edited title.");
                if (item.Editor is MindMapPreviewWindow map)
                {
                    Require(System.Text.Json.JsonSerializer.Serialize(CompletionField<MindMapNode>(map, "_root")) == mapBefore, "Map export changed live positions.");
                    var mapRoot = CompletionField<MindMapNode>(map, "_root");
                    var nodes = CompletionField<ICollectionSelectionScope>(map, "_nodeSelectionScope");
                    nodes.Begin(mapRoot.Id); nodes.Toggle(mapRoot.Children[0].Id); Pump(100);
                    var nodeDock = (CollectionSelectionDock)map.FindName("NodeSelectionDock");
                    ((Button)nodeDock.FindName("MoreButton")).Focus();
                    var delete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(workspace), Environment.TickCount, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent };
                    nodeDock.RaiseEvent(delete); Require(delete.Handled && mapRoot.Children.Count == 1, "Hosted node keyboard routing lost root protection.");
                    ((ScrollViewer)map.FindName("MapScrollViewer")).Focus();
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(workspace), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                    ((ScrollViewer)map.FindName("MapScrollViewer")).RaiseEvent(escape);
                    Require(escape.Handled && nodes.Count == 0, "Hosted map Escape did not clear node selection.");
                }
            }
            Require(cards.Title == "Saved cards" && savedQuiz.Title == "Saved quiz" && savedMap.Title == "Saved map", "Working exports mutated the dashboard models.");
            Require(((FlashcardSetDocument)snapshots.Single(item => item.Kind == EditorDocumentKind.Flashcards).Document).Cards[0].Repetitions == 7, "Live export lost card review progress.");
            var batch = service.CaptureExportSnapshotsAsync(workspace.Sessions.Select(item => item.Key).ToArray(), CancellationToken.None);
            CompletionAwait(batch); Require(batch.Result.Count == 4 && batch.Result.All(item => item.Title.StartsWith("Unsaved") || item.Kind == EditorDocumentKind.Note), "Batch export lost working snapshots.");
            CompletionAwait(SelectionExportService.ExportArchiveAsync(batch.Result, Path.Combine(_output, "working-documents.zip"), CancellationToken.None));
            var quiz = new QuizDocument { Title = "Only selected", Questions = [new() { Question = "Include me" }, new() { Question = "Omit me" }] };
            var subset = (QuizDocument)SelectionExportService.Subset(quiz, new HashSet<Guid> { quiz.Questions[0].Id });
            var printable = QuizPrintableExport.Build(subset, 12, 24, 16);
            var text = new TextRange(printable.ContentStart, printable.ContentEnd).Text;
            Require(text.Contains("Include me") && !text.Contains("Omit me"), "Printable subset included unselected questions.");
            string path = Path.Combine(_output, "selected-quiz.xps");
            CompletionAwait(SelectionExportService.ExportDocumentAsync(subset, path, CancellationToken.None, (stage, token) => QuizPrintableExport.SaveAsync(subset, stage, token)));
            Require(File.Exists(path) && new FileInfo(path).Length > 0, "Background STA printable export failed.");
            var scope = new EditorSelectionController(); scope.SetItems([Guid.NewGuid()]); scope.SelectAll();
            var dock = new CollectionSelectionDock(); dock.Initialize(scope, () => { }, () => new ContextMenu(), () => { });
            using var cancellationWindow = new CompletionWindow(new Window { Content = new LayoutRevealHost { Child = dock }, Width = 680, Height = 480 });
            var task = dock.RunOperationAsync(async (token, progress) => { progress.Report("Fixture export"); await Task.Delay(Timeout.Infinite, token); });
            Pump(80); Require(dock.IsOperationRunning && !((Button)dock.FindName("DeleteButton")).IsEnabled, "Export did not lock destructive island actions.");
            Require(SelectionKey(dock, cancellationWindow.Window, Key.Escape).Handled, "Export Escape did not cancel."); CompletionAwait(task);
            Require(!dock.IsOperationRunning && ((TextBlock)dock.FindName("StatusText")).Text == LocalizationService.GetString("SelectionExportCancelled"), "Export cancel status failed.");
            scope.Clear(); Require(((TextBlock)dock.FindName("StatusText")).Visibility == Visibility.Collapsed, "Export status survived the next selection."); dock.Dispose();
            checks.Add(suffix + ": all four working-document snapshots without saving/mutating sources, native ZIP, selected printable content, background STA XPS, export busy state/Escape cancellation and completion-status cleanup passed.");
        }
        finally { workspace.CloseAfterPreflight(); Pump(200); }
    }
}
