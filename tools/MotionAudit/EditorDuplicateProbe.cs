using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeEditorDuplicate()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme); LocalizationService.SetCulture(language);
            DuplicateFlashcardProbe(); DuplicateQuestionProbe(); DuplicateBranchProbe();
            checks.Add($"{theme}/{language}: direct menus, repeated duplication, bulk copies, fresh progress, retained content, independent options, shortcut guards, branch placement and undo/redo passed.");
        }
        File.WriteAllLines(Path.Combine(_output, "editor-duplicate-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void InvokeDuplicateMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target; menu.IsOpen = true; Pump(70);
        var action = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == LocalizationService.GetString("Duplicate"));
        Require(action.IsEnabled && action.InputGestureText == "Ctrl+D", "Direct duplicate was missing, disabled or lacked its shortcut.");
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; Pump(250);
    }
    private static bool DuplicateKey(UIElement source, Window owner, Func<bool> duplicate, ModifierKeys modifiers = ModifierKeys.Control)
    {
        bool handled = false;
        KeyEventHandler handler = (_, e) => handled = EditorSelectionInput.HandleDuplicateKey(e, modifiers, duplicate);
        source.PreviewKeyDown += handler;
        try
        {
            source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, Key.D)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        finally { source.PreviewKeyDown -= handler; }
        Pump(150); return handled;
    }
    private static void DuplicateFlashcardProbe()
    {
        var note = Guid.NewGuid();
        var original = new FlashcardItem { Question = "Question", Answer = "Answer", Hint = "Hint", Category = "Category", LinkedNoteId = note,
            IsStarred = true, Repetitions = 4, IsKnown = true, IntervalDays = 8, DueDate = DateTime.Today.AddDays(8), LastReviewedAt = DateTime.Today, Stability = 3, Difficulty = 2,
            QuestionImageFileName = "question.png", AnswerAudioFileName = "answer.wav" };
        var window = new FlashcardsPreviewWindow([original, new() { Question = "Second", Answer = "Second answer" }], studyDueCardsOnly: false)
            { Width = 920, Height = 740, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(350);
            var list = (ItemsControl)window.FindName("FlashcardsItemsControl");
            var face = ControlTree(list).OfType<Border>().First(border => border.Name == "CardSurface" && border.IsVisible && border.ContextMenu is not null);
            InvokeDuplicateMenu(face.ContextMenu, face);
            var cards = window.GetFlashcardItems(); var copy = cards[1];
            Require(cards.Count == 3 && cards[0].Id == original.Id && copy.Id != original.Id, "Direct card duplicate was not adjacent and independent.");
            Require(copy.Question == original.Question && copy.Answer == original.Answer && copy.Hint == original.Hint && copy.Category == original.Category
                && copy.LinkedNoteId == note && copy.IsStarred && copy.QuestionImageFileName == original.QuestionImageFileName && copy.AnswerAudioFileName == original.AnswerAudioFileName,
                "Card duplicate dropped authored content, media or links.");
            Require(copy.Repetitions == 0 && !copy.IsKnown && copy.DueDate is null && copy.Stability == 0 && cards[0].Repetitions == 4,
                "Card duplicate inherited or changed review progress.");
            Require(!window.BulkSelection.IsActive, "Single-card duplication forced multiselect.");
            var frame = ControlTree(list).OfType<CollectionSelectionFrame>().Single(frame => frame.ItemId == copy.Id);
            Require(frame.IsKeyboardFocusWithin, "New card did not receive focus for another duplicate.");
            Require(DuplicateKey(frame, window, () => (bool)RemainingCall(window, "DuplicateCurrentFlashcards", frame)!), "Card duplicate shortcut did not execute.");
            Require(window.GetFlashcardItems().Count == 4, "Repeated card duplication did not add exactly one copy.");
            int attempts = 0;
            Require(!DuplicateKey((TextBox)window.FindName("TitleTextBox"), window, () => { attempts++; return true; }) && attempts == 0,
                "Duplicate shortcut stole input from a card title field.");
            window.BulkSelection.Begin(original.Id); window.BulkSelection.Toggle(cards[2].Id);
            window.DuplicateSelectedFlashcards(); Pump(100);
            Require(window.GetFlashcardItems().Count == 6 && window.BulkSelection.Count == 2, "Bulk duplication lost copies or selection.");
            window.BulkSelection.End();
            var visible = CompletionField<BulkObservableCollection<FlashcardsPreviewWindow.FlashcardPreviewItem>>(window, "_items");
            visible.ReplaceAll(visible.Reverse().ToArray());
            var displayOrder = visible.Select(card => card.Id).ToArray(); Pump(100);
            face = ControlTree(list).OfType<Border>().First(border => border.Name == "CardSurface" && border.IsVisible && border.ContextMenu is not null);
            InvokeDuplicateMenu(face.ContextMenu, face);
            var adoptedOrder = window.GetFlashcardItems();
            Require(adoptedOrder[0].Id == displayOrder[0] && adoptedOrder.Skip(2).Select(card => card.Id).SequenceEqual(displayOrder.Skip(1)),
                "Duplication reset the shuffled card order.");
            Require(adoptedOrder[1].LinkedNoteId == adoptedOrder[0].LinkedNoteId, "Shuffled duplication copied another card's note link.");
            var status = (ComboBox)window.FindName("StatusFilterComboBox"); status.SelectedIndex = 2; Pump(100);
            face = ControlTree(list).OfType<Border>().First(border => border.Name == "CardSurface" && border.IsVisible && border.ContextMenu is not null);
            InvokeDuplicateMenu(face.ContextMenu, face);
            Require(status.SelectedIndex == 0 && visible.Any(card => card.Id == CompletionField<FlashcardsPreviewWindow.FlashcardPreviewItem>(window, "_selectedItem").Id),
                "Fresh single copy disappeared under the learning filter.");
        }
        finally { window.DisposeWorkspaceEditor(); Pump(80); }
    }
    private static void DuplicateQuestionProbe()
    {
        var original = new QuizQuestion { Question = "Question", Hint = "Hint", Explanation = "Explanation", LastAttemptCorrect = true,
            Options = [new() { Text = "Correct", IsCorrect = true }, new() { Text = "Other" }] };
        var plain = new QuizQuestion { Question = "Plain", Type = QuizQuestionType.PlainText, PlainTextAnswer = "Expected", PlainTextCharLimit = 77,
            PlainTextMatchCase = true, PlainTextIgnorePunctuation = true };
        var window = new QuizPreviewWindow(new QuizDocument { Questions = [original, plain] }, noteOptions: null)
            { Width = 920, Height = 740, ShowInTaskbar = false };
        try
        {
            window.Show(); window.IsEditMode = true; Pump(300);
            var list = (ItemsControl)window.FindName("QuestionsItemsControl");
            var frame = ControlTree(list).OfType<CollectionSelectionFrame>().First();
            InvokeDuplicateMenu(frame.ContextMenu, frame);
            var questions = window.ToDocument().Questions; var copy = questions[1];
            Require(questions.Count == 3 && questions[0].Id == original.Id && copy.Id != original.Id && copy.LastAttemptCorrect is null,
                "Question duplicate reused identity/progress or was not adjacent.");
            Require(copy.Hint == original.Hint && copy.Explanation == original.Explanation && copy.Options[0].IsCorrect && copy.Options[0].Text == "Correct",
                "Question duplicate lost options, hint or explanation.");
            Require(!window.BulkSelection.IsActive, "Single-question duplication forced multiselect.");
            var copiedFrame = ControlTree(list).OfType<CollectionSelectionFrame>().Single(frame => frame.ItemId == copy.Id);
            Require(copiedFrame.IsKeyboardFocusWithin, "New question did not receive focus.");
            var copiedItem = (QuizPreviewWindow.QuizPreviewQuestion)copiedFrame.DataContext;
            copiedItem.Options[0].Text = "Changed copy";
            Require(window.ToDocument().Questions[0].Options[0].Text == "Correct", "Copied question options aliased the original.");
            Require(DuplicateKey(copiedFrame, window, () => (bool)RemainingCall(window, "DuplicateCurrentQuestions", copiedFrame)!), "Question duplicate shortcut did not execute.");
            int attempts = 0;
            foreach (var field in ControlTree(copiedFrame).OfType<TextBox>().Take(2))
                Require(!DuplicateKey(field, window, () => { attempts++; return true; }), "Question duplicate stole text input.");
            Require(attempts == 0, "Question text guards invoked duplication.");
            window.BulkSelection.Begin(plain.Id); window.DuplicateSelectedQuestions(); Pump(100);
            var plainCopy = window.ToDocument().Questions.Last();
            Require(window.BulkSelection.Count == 1 && plainCopy.Id != plain.Id && plainCopy.Type == QuizQuestionType.PlainText
                && plainCopy.PlainTextAnswer == plain.PlainTextAnswer && plainCopy.PlainTextCharLimit == 77 && plainCopy.PlainTextMatchCase && plainCopy.PlainTextIgnorePunctuation,
                "Bulk plain-text question duplication lost grading settings.");
            window.BulkSelection.End(); window.IsEditMode = false; Pump(100);
            frame = ControlTree(list).OfType<CollectionSelectionFrame>().First();
            frame.ContextMenu.PlacementTarget = frame; frame.ContextMenu.IsOpen = true; Pump(50);
            Require(!frame.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "Duplicate").IsEnabled,
                "Solving mode exposed an enabled duplicate action."); frame.ContextMenu.IsOpen = false;
        }
        finally { window.DisposeWorkspaceEditor(); Pump(80); }
    }
    private static void DuplicateBranchProbe()
    {
        var child = new MindMapNode { Text = "Child", Note = "Note", ManualX = 400, ManualY = 100 };
        var branch = new MindMapNode { Text = "Branch", Children = [child], ManualX = 160, ManualY = 100 };
        var other = new MindMapNode { Text = "Other", ManualX = 160, ManualY = 260 };
        child.CrossLinkTargetIds = [other.Id];
        var root = new MindMapNode { Text = "Root", Children = [branch, other], ManualX = 32, ManualY = 100 };
        var window = new MindMapPreviewWindow(root, useManualPositions: true) { Width = 920, Height = 740, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(350); RemainingCall(window, "SelectSingleNode", branch);
            var canvas = (Canvas)window.FindName("MapCanvas");
            var face = canvas.Children.OfType<Border>().Single(border => ReferenceEquals(border.Tag, branch));
            var undo = CompletionField<List<MindMapNode>>(window, "_undoStack"); int baseline = undo.Count;
            InvokeDuplicateMenu(face.ContextMenu, face);
            Require(root.Children.Count == 3 && root.Children[0] == branch && root.Children[2] == other && undo.Count == baseline + 1,
                "Branch duplication lost sibling order or its one-step undo.");
            var copy = root.Children[1];
            Require(copy.Id != branch.Id && copy.Children.Single().Id != child.Id && copy.Children[0].Note == child.Note
                && copy.Children[0].CrossLinkTargetIds.SequenceEqual(new[] { other.Id }), "Branch duplication lost independent children or links.");
            var originals = new[] { root, branch, child, other }.Select(node => canvas.Children.OfType<Border>().Single(border => ReferenceEquals(border.Tag, node)))
                .Select(border => new Rect(Canvas.GetLeft(border), Canvas.GetTop(border), border.ActualWidth, border.ActualHeight)).ToArray();
            foreach (var node in MindMapSelectionOperations.Nodes(copy))
            {
                var border = canvas.Children.OfType<Border>().Single(border => ReferenceEquals(border.Tag, node));
                var bounds = new Rect(Canvas.GetLeft(border), Canvas.GetTop(border), border.ActualWidth, border.ActualHeight);
                Require(!originals.Any(bounds.IntersectsWith), "Manual branch duplication overlapped an existing node.");
            }
            RemainingCall(window, "Undo"); Pump(100); Require(root.Children.Count == 2, "Branch duplicate undo failed.");
            RemainingCall(window, "Redo"); Pump(100); Require(root.Children.Count == 3 && root.Children[1].Id == copy.Id, "Branch duplicate redo changed identity.");
            var scope = CompletionField<ICollectionSelectionScope>(window, "_nodeSelectionScope"); scope.Begin(root.Id); Pump(50);
            var dock = (CollectionSelectionDock)window.FindName("NodeSelectionDock");
            Require(!((Button)dock.FindName("DuplicateButton")).IsEnabled, "Unique map root allowed duplication.");
            scope.Begin(branch.Id); scope.Toggle(other.Id); Pump(80); baseline = undo.Count;
            SelectionClick((Button)dock.FindName("DuplicateButton")); Pump(200);
            Require(root.Children.Count == 5 && scope.Count == 2 && undo.Count == baseline + 1, "Bulk branch duplication was not one selected operation.");
            var selected = CompletionField<HashSet<MindMapNode>>(window, "_selectedNodes");
            var copiedBranch = selected.Single(node => node.Text == "Branch"); var copiedOther = selected.Single(node => node.Text == "Other");
            Require(copiedBranch.Children[0].CrossLinkTargetIds.SequenceEqual(new[] { copiedOther.Id }), "Bulk duplication did not remap cross-branch links.");
            RemainingCall(window, "Undo"); Pump(80); Require(root.Children.Count == 3, "Bulk branch duplication could not be undone in one step.");
        }
        finally { window.DisposeWorkspaceEditor(); Pump(80); }
    }
}
