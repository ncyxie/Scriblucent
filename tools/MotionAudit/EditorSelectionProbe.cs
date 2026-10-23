using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeEditorSelection(string? only = null)
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            if (only is not null && !only.StartsWith($"{theme}/{mode}")) continue;
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(theme == "Light" ? "en" : "lt");
            MotionPolicy.Configure(mode, false);
            ProbeFlashcardSelection(theme, mode, checks);
            ProbeQuestionSelection(theme, mode, checks);
        }
        if (only is null) { ProbeCompactSelectionIsland(checks); ProbeHostedSelection(checks); }
        Pump(300);
        Require(!RenderTransitionClock.IsAnyRunning, "Selection editors left active motion tracks after disposal.");
        File.WriteAllLines(Path.Combine(_output, "selection-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void ProbeIslandClearance(CollectionSelectionFrame last, CollectionSelectionDock dock, Window window)
    {
        double lastBottom = last.TranslatePoint(new Point(0, last.ActualHeight), window).Y;
        double islandTop = dock.TranslatePoint(new Point(), window).Y;
        Require(lastBottom < islandTop - 8, $"Final card is covered by selection island: last={lastBottom}, island={islandTop}, clearance={dock.ScrollClearance}, parentMargin={(last.Parent as FrameworkElement)?.Margin}.");
    }
    private static void ProbeSelectionAttachment(CollectionSelectionFrame frame)
    {
        var window = Window.GetWindow(frame)!;
        var face = ControlTree(frame).OfType<Border>().First(border => border.Name == frame.FaceName && border.IsVisible);
        var checkbox = ControlTree(frame).OfType<CollectionSelectionToggle>().Single();
        var feedback = ControlTree(frame).OfType<CollectionSelectionFeedback>().Single();
        // Drive the exact hover routine without moving the user's global desktop pointer.
        var hover = typeof(CollectionSelectionFrame).GetMethod("PlayHover", BindingFlags.Instance | BindingFlags.NonPublic)!;
        hover.Invoke(frame, [false, false]); Pump(40);
        var restFace = face.TranslatePoint(new Point(), window);
        var restCheck = checkbox.TranslatePoint(new Point(), window);
        var restRim = feedback.TranslatePoint(new Point(), window);
        hover.Invoke(frame, [true, true]);
        for (int i = 0; i < 4; i++)
        {
            Pump(65);
            var delta = face.TranslatePoint(new Point(), window) - restFace;
            var checkDelta = checkbox.TranslatePoint(new Point(), window) - restCheck;
            var rimDelta = feedback.TranslatePoint(new Point(), window) - restRim;
            Require((delta - checkDelta).Length < .15 && (delta - rimDelta).Length < .15, $"Card/checkbox/rim separated during hover: face={delta}, check={checkDelta}, rim={rimDelta}.");
        }
        WaitLoading(() => !NativeSurfaceMotion.IsRunning(frame, MotionChannel.Interaction));
        double finalLift = face.TranslatePoint(new Point(), window).Y - restFace.Y;
        Require(Math.Abs(finalLift - (MotionPolicy.Spatial ? UiMotion.CardHoverLiftY : 0)) < .2, $"Card hover did not respect shared motion policy: {finalLift}.");
        Require(CollectionSelectionFrame.For(checkbox) == frame && CollectionSelectionFrame.For(face) == frame, "Checkbox and face resolve different hover owners.");
        hover.Invoke(frame, [false, true]); Pump(220);
    }
    private static void SelectionEnterFromCard(CollectionSelectionFrame frame)
    {
        frame.Focus(); Pump(30);
        var toggle = ControlTree(frame).OfType<CollectionSelectionToggle>().Single();
        Require(frame.Selection?.CanSelect == true && !frame.IsSelecting && !toggle.IsVisible, "Ordinary focused card exposed an unnecessary checkbox.");
        frame.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(frame), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        Pump(30);
        Require(frame.IsSelecting && toggle.IsVisible, "Explicit keyboard selection did not reveal checkboxes.");
        var peer = new CheckBoxAutomationPeer(toggle);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
    }
    private static void SelectionClick(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static KeyEventArgs SelectionKey(UIElement source, Window owner, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        return args;
    }
    private static void CancelSelectionConfirmation(CollectionSelectionDock bar)
    {
        bool cancelled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        int tries = 0;
        timer.Tick += (_, _) =>
        {
            var dialog = _app.Windows.OfType<ModernMessageDialog>().FirstOrDefault();
            if (dialog is not null) { timer.Stop(); cancelled = true; SelectionClick((Button)dialog.FindName("CancelButton")); }
            else if (++tries > 100) { timer.Stop(); throw new InvalidOperationException("Delete confirmation did not open."); }
        };
        timer.Start();
        try { SelectionClick((Button)bar.FindName("DeleteButton")); }
        finally { timer.Stop(); }
        Require(cancelled, "Delete did not offer a cancellable confirmation.");
    }
    private static void ProbeFlashcardSelection(string theme, MotionPreference mode, List<string> checks)
    {
        var originals = Enumerable.Range(0, 168).Select(i => new FlashcardItem
        {
            Question = $"Question {i}", Answer = $"Answer {i}", Hint = "Authored hint", Category = "Original",
            SetIndex = i < 160 ? 1 : 2, IsStarred = i % 2 == 0,
            LinkedNoteId = Guid.NewGuid(), Repetitions = 3, IntervalDays = 8,
            EaseFactor = 2.7, IsKnown = true, DueDate = DateTime.Today.AddDays(8),
            LastReviewedAt = DateTime.Today, Stability = 4, Difficulty = 3,
            QuestionAudioFileName = i == 0 ? "selection-fixture.mp3" : null
        }).ToArray();
        var window = new FlashcardsPreviewWindow(originals, setNames: new Dictionary<int, string> { [1] = "Practice", [2] = "Archive" }, studyDueCardsOnly: false)
            { Width = theme == "Light" ? 760 : 980, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); Pump(300);
            var grid = (FlashcardGridItemsControl)window.FindName("FlashcardsItemsControl");
            var viewer = ControlTree(grid).OfType<ScrollViewer>().First();
            var first = ControlTree(grid).OfType<CollectionSelectionFrame>().First();
            var size = first.RenderSize; var viewportSize = grid.RenderSize;
            SelectionEnterFromCard(first); window.BulkSelection.Clear(); Pump(180);
            Require(window.BulkSelection.IsActive && !window.WorkspaceDirty, "Entering flashcard selection dirtied content.");
            Require(first.IsKeyboardFocusWithin, "Checkbox entry unexpectedly moved focus into the floating island.");
            Require(first.IsSelecting && first.RenderSize == size, "Selection changed flashcard card dimensions.");
            ProbeSelectionAttachment(first);
            var visibleFace = ControlTree(first).OfType<Border>().First(face => face.Name == first.FaceName && face.IsVisible);
            var starControl = ControlTree(visibleFace).OfType<Button>().Single(button => button.Style == window.FindResource("FlashcardStarButtonStyle"));
            var selectionToggle = ControlTree(first).OfType<CollectionSelectionToggle>().Single();
            double starCenter = starControl.TranslatePoint(new Point(0, starControl.ActualHeight / 2), first).Y;
            double checkboxCenter = selectionToggle.TranslatePoint(new Point(0, selectionToggle.ActualHeight / 2), first).Y;
            Require(Math.Abs(starCenter - checkboxCenter) < .6, $"Flashcard checkbox is not centered with header controls: star={starCenter}, checkbox={checkboxCenter}.");
            var box = (FrameworkElement)selectionToggle.Template.FindName("SelectionBox", selectionToggle);
            double boxLeft = box.TranslatePoint(new Point(), first).X;
            double starRight = starControl.TranslatePoint(new Point(starControl.ActualWidth, 0), first).X;
            Require(Math.Abs(boxLeft - starRight - 6) < .6, $"Flashcard checkbox has unequal header spacing: {boxLeft - starRight} DIP.");
            Require(grid.RenderSize == viewportSize, "Selection island consumed the card viewport.");
            Require(!SelectionKey((TextBox)window.FindName("TitleTextBox"), window, Key.Delete).Handled && window.GetFlashcardItems().Count == 168,
                "Selection stole a text field's Delete key.");
            var flip = ControlTree(first).OfType<FlashcardFlipHost>().First();
            bool flipped = flip.IsFlipped;
            Require(SelectionKey(first, window, Key.Space).Handled && window.BulkSelection.Contains(first.ItemId) && flip.IsFlipped == flipped,
                "Selection Space did not select independently of flipping.");
            window.BulkSelection.Clear();
            var toggle = ControlTree(first).OfType<CheckBox>().Single();
            var peer = new CheckBoxAutomationPeer(toggle);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle(); Pump(20);
            Require(window.BulkSelection.Contains(first.ItemId), "Automation checkbox did not select the card.");
            Pump(220);
            var glyph = (System.Windows.Shapes.Path)toggle.Template.FindName("CheckGlyph", toggle);
            Require(glyph.Opacity > .99 && Math.Abs(glyph.StrokeDashOffset) < .01,
                $"Selected checkbox has no painted tick before recycling: opacity={glyph.Opacity}, dash={glyph.StrokeDashOffset}.");
            window.BulkSelection.Clear();
            window.BulkSelection.Toggle(originals[0].Id); window.BulkSelection.Toggle(originals[1].Id);
            Require(((ContextMenu)RemainingCall(window, "BuildFlashcardSelectionMenu")!).Items.OfType<MenuItem>().Any(item => Equals(item.Header, LocalizationService.GetString("SelectionExportSelected"))), "Editor More menu has no selected export.");
            var beforeCancel = window.GetFlashcardItems().Count;
            CancelSelectionConfirmation((CollectionSelectionDock)window.FindName("BulkSelectionBar"));
            Require(window.GetFlashcardItems().Count == beforeCancel && window.BulkSelection.Count == 2 && !window.WorkspaceDirty,
                "Cancelled flashcard deletion changed content or selection.");
            var star = ControlTree(first).OfType<Button>().First(button => button.IsVisible && button.Style == window.FindResource("FlashcardStarButtonStyle"));
            Require(star.IsEnabled && star.IsHitTestVisible && EditorSelectionInput.IsInteractive(star, first), "Selection blocked the individual star control.");
            bool starred = window.GetFlashcardItems()[0].IsStarred;
            SelectionClick(star);
            Require(window.GetFlashcardItems()[0].IsStarred != starred && window.BulkSelection.Count == 2 && flip.IsFlipped == flipped,
                "A card action changed bulk selection or flipped the card.");
            SelectionClick(star); window.MarkCurrentStateAutoSaved();
            int updates = 0; window.WorkspaceStateChanged += () => updates++;
            window.StarSelectedFlashcards(true);
            Require(updates == 1, "Flashcard bulk star published more than one dirty update.");
            Require(window.GetFlashcardItems().Take(2).All(card => card.IsStarred), "Bulk star missed cards.");
            window.StarSelectedFlashcards(false);
            window.ChangeSelectedFlashcardCategory("Grouped");
            Require(window.GetFlashcardItems().Take(2).All(card => !card.IsStarred && card.Category == "Grouped"), "Bulk category/unstar missed cards.");
            window.DuplicateSelectedFlashcards();
            var copies = window.GetFlashcardItems().Where(card => !originals.Any(source => source.Id == card.Id)).ToArray();
            Require(copies.Length == 2 && window.BulkSelection.SelectedIds.ToHashSet().SetEquals(copies.Select(card => card.Id)), "Copies were not selected.");
            Require(copies.All(card => card.Repetitions == 0 && !card.IsKnown && card.DueDate is null && card.Stability == 0 && card.EaseFactor == 2.5), "Copies inherited review progress.");
            Require(copies[0].LinkedNoteId == originals[0].LinkedNoteId && copies[0].Hint == originals[0].Hint && copies[0].QuestionAudioFileName == originals[0].QuestionAudioFileName, "Copies lost authored metadata.");
            window.MoveSelectedFlashcards(2);
            Require(window.GetFlashcardItems().Where(card => copies.Any(copy => copy.Id == card.Id)).All(card => card.SetIndex == 2) && window.BulkSelection.Count == 0,
                "Bulk move lost cards or kept hidden selection.");
            for (int burst = 0; burst < 4; burst++)
            {
                window.BulkSelection.SelectOnly([originals[0].Id, originals[1].Id]);
                window.DuplicateSelectedFlashcards();
                window.DeleteSelectedFlashcards(window.BulkSelection.SelectedIds.ToHashSet());
            }
            Require(window.GetFlashcardItems().Count == 170, "Repeated bulk copy/delete drifted the card count.");
            var status = (ComboBox)window.FindName("StatusFilterComboBox");
            status.SelectedIndex = 2; Pump(30);
            window.BulkSelection.SelectOnly([originals[0].Id, originals[1].Id]);
            window.DuplicateSelectedFlashcards();
            Require(window.BulkSelection.Count == 0 && !string.IsNullOrEmpty(window.BulkSelection.StatusText),
                "New copies hidden by a maturity filter received no feedback or remained invisibly selected.");
            var hidden = window.GetFlashcardItems().Where(card => originals.All(source => source.Id != card.Id) && copies.All(copy => copy.Id != card.Id)).Select(card => card.Id).ToHashSet();
            window.DeleteSelectedFlashcards(hidden);
            status.SelectedIndex = 0; Pump(30);
            window.BulkSelection.SelectAll();
            Require(window.BulkSelection.Count == 160 && ControlTree(grid).OfType<CollectionSelectionFrame>().Count() < 50, "Select all realized offscreen cards or selected another set.");
            viewer.ScrollToEnd(); Pump(180);
            Require(ControlTree(grid).OfType<CollectionSelectionFrame>().All(frame => frame.IsItemSelected), "Recycled cards lost logical selection.");
            ProbeIslandClearance(ControlTree(grid).OfType<CollectionSelectionFrame>().Last(), (CollectionSelectionDock)window.FindName("BulkSelectionBar"), window);
            viewer.ScrollToTop(); Pump(150);
            window.SetWorkspaceActive(false); window.SetWorkspaceActive(true);
            Require(window.BulkSelection.Count == 160, "Temporarily inactive flashcards lost selection.");
            if (mode == MotionPreference.Full) SaveControlCapture((FrameworkElement)window.Content, $"flashcard-selection-{theme}");
            var category = (ComboBox)window.FindName("CategoryFilterComboBox");
            category.IsDropDownOpen = true; Pump(40);
            Require(SelectionKey(category, window, Key.Escape).Handled && !category.IsDropDownOpen && window.BulkSelection.IsActive,
                "Escape exited selection before dismissing the open dropdown.");
            category.SelectedIndex = 1; Pump(80);
            Require(window.BulkSelection.Count == 2, "Filtering retained hidden flashcard selections.");
            window.BulkSelection.End();
            Require(ControlTree(grid).OfType<CollectionSelectionFrame>().All(frame => !frame.IsSelecting && !frame.IsItemSelected), "Leaving selection left stale decoration.");
            window.BulkSelection.SelectAll(); var deleted = window.BulkSelection.SelectedIds.ToHashSet();
            window.DeleteSelectedFlashcards(deleted);
            Require(!window.GetFlashcardItems().Any(card => deleted.Contains(card.Id)) && window.BulkSelection.Count == 0, "Bulk deletion left removed cards.");
            Require(SelectionKey(window, window, Key.Escape).Handled && !window.BulkSelection.IsActive && window.IsVisible,
                "Escape did not leave selection before window/fullscreen handling.");
            checks.Add($"PASS {theme}/{mode}: flashcards selection, automation, cancellation, one dirty update, star/category, fresh copies, move, filters, bounded/recycled cards and delete.");
        }
        finally { window.DisposeWorkspaceEditor(); Pump(80); }
    }
    private static void ProbeQuestionSelection(string theme, MotionPreference mode, List<string> checks)
    {
        var original = Enumerable.Range(0, 10).Select(i => new QuizQuestion
        {
            Question = $"Question {i}", Explanation = "Explanation", Hint = "Hint", LastAttemptCorrect = true,
            Type = i == 1 ? QuizQuestionType.PlainText : QuizQuestionType.SingleChoice,
            PlainTextAnswer = "Expected", PlainTextCharLimit = 77, PlainTextMatchCase = true, PlainTextIgnorePunctuation = true,
            Options = [new QuizOption { Text = "Correct", IsCorrect = true }, new QuizOption { Text = "Alternative" }]
        }).ToArray();
        var window = new QuizPreviewWindow(new QuizDocument { Title = "Selection practice", Questions = original.ToList() }, noteOptions: null)
            { Width = theme == "Light" ? 760 : 980, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate(); Pump(250);
            Require(!window.BulkSelection.CanSelect, "Quiz solving permits structural selection.");
            window.IsEditMode = true; Pump(150); window.MarkCurrentStateAutoSaved();
            SelectionEnterFromCard(ControlTree((ItemsControl)window.FindName("QuestionsItemsControl")).OfType<CollectionSelectionFrame>().First()); window.BulkSelection.Clear(); Pump(180);
            Require(window.BulkSelection.IsActive && !window.WorkspaceDirty, "Quiz selection dirtied the document.");
            var questionFrame = ControlTree((ItemsControl)window.FindName("QuestionsItemsControl")).OfType<CollectionSelectionFrame>().First();
            ProbeSelectionAttachment(questionFrame);
            var headerButtons = ControlTree(questionFrame).OfType<Button>().Where(button => button.IsVisible && button.Name == "DeleteQuestionButton").ToArray();
            var selector = ControlTree(questionFrame).OfType<CollectionSelectionToggle>().Single();
            double selectorY = selector.TranslatePoint(new Point(0, selector.ActualHeight / 2), questionFrame).Y;
            if (mode == MotionPreference.Full) SaveControlCapture((FrameworkElement)window.Content, $"quiz-header-{theme}");
            Require(headerButtons.Length == 1 && headerButtons.All(button => button.ActualHeight == 32 && Math.Abs(button.TranslatePoint(new Point(0, button.ActualHeight / 2), questionFrame).Y - selectorY) < 1), "Quiz header button/checkbox have unequal size or vertical centers.");
            window.BulkSelection.Toggle(original[1].Id); window.BulkSelection.Toggle(original[2].Id); window.BulkSelection.Toggle(original[6].Id);
            Require(((ContextMenu)RemainingCall(window, "BuildQuestionSelectionMenu")!).Items.OfType<MenuItem>().Any(item => Equals(item.Header, LocalizationService.GetString("SelectionExportSelected"))), "Quiz More menu has no selected export.");
            CancelSelectionConfirmation((CollectionSelectionDock)window.FindName("BulkSelectionBar"));
            Require(window.ToDocument().Questions.Count == 10 && !window.WorkspaceDirty, "Cancelled quiz deletion changed content.");
            var editable = ControlTree((ItemsControl)window.FindName("QuestionsItemsControl")).OfType<TextBox>()
                .First(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Question");
            Require(editable.IsEnabled && editable.IsHitTestVisible && EditorSelectionInput.IsInteractive(editable), "Selection blocked question editing.");
            var questionText = editable.Text;
            editable.Text = questionText + " edited"; editable.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Require(window.ToDocument().Questions[0].Question.EndsWith(" edited") && window.BulkSelection.Count == 3,
                "Inline question editing failed or changed selection.");
            editable.Text = questionText; editable.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); window.MarkCurrentStateAutoSaved();
            int updates = 0; window.WorkspaceStateChanged += () => updates++;
            window.MoveSelectedQuestions(false);
            Require(updates == 1, "Quiz bulk reorder published repeated dirty updates.");
            var order = window.ToDocument().Questions.Select(question => question.Id).ToArray();
            Require(order.SequenceEqual(new[] { original[1].Id, original[2].Id, original[6].Id, original[0].Id, original[3].Id, original[4].Id, original[5].Id, original[7].Id, original[8].Id, original[9].Id }), "Question selection did not move as one ordered block.");
            window.MoveSelectedQuestions(true);
            Require(window.ToDocument().Questions.Select(question => question.Id).SequenceEqual(new[] { original[0].Id, original[1].Id, original[2].Id, original[6].Id, original[3].Id, original[4].Id, original[5].Id, original[7].Id, original[8].Id, original[9].Id }), "Question block did not move down one position with relative order intact.");
            window.DuplicateSelectedQuestions();
            var doc = window.ToDocument();
            var copies = doc.Questions.Where(question => original.All(source => source.Id != question.Id)).ToArray();
            Require(copies.Length == 3 && copies.All(question => question.LastAttemptCorrect is null && question.Hint == "Hint" &&
                (question.Type == QuizQuestionType.PlainText ? question.PlainTextAnswer == "Expected" && question.PlainTextCharLimit == 77 && question.PlainTextMatchCase && question.PlainTextIgnorePunctuation : question.Options[0].IsCorrect)), "Quiz copies lost authored content or inherited results.");
            Require(window.BulkSelection.SelectedIds.ToHashSet().SetEquals(copies.Select(question => question.Id)), "Quiz copies were not selected.");
            if (mode == MotionPreference.Full)
            {
                var questionViewer = ControlTree((ItemsControl)window.FindName("QuestionsItemsControl")).OfType<ScrollViewer>().First();
                questionViewer.ScrollToVerticalOffset(900); Pump(180);
                SaveControlCapture((FrameworkElement)window.Content, $"quiz-selection-{theme}");
            }
            var list = (ItemsControl)window.FindName("QuestionsItemsControl");
            var tailViewer = ControlTree(list).OfType<ScrollViewer>().First();
            tailViewer.ScrollToEnd(); Pump(260);
            tailViewer.ScrollToEnd(); Pump(160);
            ProbeIslandClearance(ControlTree(list).OfType<CollectionSelectionFrame>().Last(), (CollectionSelectionDock)window.FindName("BulkSelectionBar"), window);
            var formerLast = list.Items.Cast<QuizPreviewWindow.QuizPreviewQuestion>().Last();
            window.BulkSelection.SelectOnly([formerLast.Id]); window.MoveSelectedQuestions(false); Pump(100);
            tailViewer.ScrollToEnd(); Pump(180); tailViewer.ScrollToEnd(); Pump(100);
            var tailFrames = ControlTree(list).OfType<CollectionSelectionFrame>().ToArray();
            var newLast = list.Items.Cast<QuizPreviewWindow.QuizPreviewQuestion>().Last();
            var finalFrame = tailFrames.Single(frame => frame.ItemId == newLast.Id);
            Require(((FrameworkElement)finalFrame.Parent).Margin.Bottom > 90 && ((FrameworkElement)tailFrames.Single(frame => frame.ItemId == formerLast.Id).Parent).Margin.Bottom == 17,
                "Quiz reorder left clearance attached to the former last question.");
            ProbeIslandClearance(finalFrame, (CollectionSelectionDock)window.FindName("BulkSelectionBar"), window);
            var removed = list.Items.Cast<QuizPreviewWindow.QuizPreviewQuestion>().First(question => copies.Any(copy => copy.Id == question.Id));
            window.BulkSelection.SelectOnly(copies.Select(question => question.Id));
            window.DeleteSelectedQuestions(window.BulkSelection.SelectedIds.ToHashSet()); window.MarkCurrentStateAutoSaved();
            updates = 0; removed.Question = "Detached item";
            Require(updates == 0 && !window.WorkspaceDirty, "Deleted quiz question retained its editor subscription.");
            window.BulkSelection.SelectAll();
            Require(!window.CanMoveSelectedQuestions(false) && !window.CanMoveSelectedQuestions(true), "All-selected movement should be unavailable.");
            window.DeleteSelectedQuestions(window.BulkSelection.SelectedIds.ToHashSet());
            Require(window.ToDocument().Questions.Count == 1 && window.BulkSelection.Count == 0, "Deleting every question did not leave one fresh question.");
            window.BulkSelection.SelectAll(); window.IsEditMode = false;
            Require(!window.BulkSelection.IsActive && window.BulkSelection.Count == 0, "Leaving quiz edit mode retained structural selection.");
            checks.Add($"PASS {theme}/{mode}: quiz edit gating, cancellation, one dirty update, stable block reorder, duplicate metadata, detached subscriptions, delete-all and mode exit.");
        }
        finally { window.DisposeWorkspaceEditor(); Pump(80); }
    }
    private static void ProbeCompactSelectionIsland(List<string> checks)
    {
        var scope = new EditorSelectionController(); scope.SetItems(Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()));
        var dock = new CollectionSelectionDock();
        int duplicates = 0;
        dock.Initialize(scope, () => { }, () => new ContextMenu(), () => duplicates++);
        var slot = new LayoutRevealHost { Child = dock, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16) };
        var root = new Grid { Children = { slot } };
        var window = new Window { Content = root, Width = 360, Height = 220, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(80); var size = root.RenderSize;
            scope.SelectAll(); Pump(160);
            Require(root.RenderSize == size && dock.IsVisible && dock.ActualWidth <= root.ActualWidth - 32 && !((Button)dock.FindName("DuplicateButton")).IsVisible,
                "Compact island overflowed, reserved a row, or retained its secondary action.");
            SelectionClick((Button)dock.FindName("MoreButton")); Pump(40);
            var menu = (ContextMenu)typeof(CollectionSelectionDock).GetField("_openMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dock)!;
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(duplicates == 1, "Compact island lost Duplicate in its overflow menu."); menu.IsOpen = false;
            window.Width = 720; Pump(80);
            Require(((Button)dock.FindName("DuplicateButton")).IsVisible && dock.ActualHeight < 64, "Wide island did not restore the single-line action.");
            scope.End(); Pump(120);
            checks.Add("PASS compact island: fixed viewport, one row of commands, Duplicate moves into More and returns after resize.");
        }
        finally { dock.Dispose(); window.Close(); }
    }
    private static void ProbeHostedSelection(List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        var cards = new FlashcardsPreviewWindow(Enumerable.Range(0, 24).Select(i => new FlashcardItem { Question = "Hosted " + i, Answer = "Answer" }), studyDueCardsOnly: false);
        var quiz = new QuizPreviewWindow(new QuizDocument { Questions = [new QuizQuestion { Question = "Hosted question" }] }, noteOptions: null);
        var owner = new Window { Width = 980, Height = 760, ShowInTaskbar = false };
        var tabs = new TabControl();
        var cardRoot = cards.AttachToWorkspace(owner, () => true, () => { });
        var quizRoot = quiz.AttachToWorkspace(owner, () => true, () => { });
        tabs.Items.Add(new TabItem { Header = "Cards", Content = cardRoot });
        tabs.Items.Add(new TabItem { Header = "Quiz", Content = quizRoot });
        owner.Content = tabs;
        try
        {
            owner.Show(); Pump(100); cards.BulkSelection.SelectAll(); Pump(100);
            Require(ControlTree(cardRoot).OfType<CollectionSelectionFrame>().All(frame => frame.Selection == cards.BulkSelection && frame.IsItemSelected), "Hosted flashcards lost controller binding.");
            tabs.SelectedIndex = 1; cards.SetWorkspaceActive(false); quiz.SetWorkspaceActive(true); Pump(100);
            quiz.IsEditMode = true; quiz.BulkSelection.SelectAll(); Pump(100);
            Require(cards.BulkSelection.Count == 24 && quiz.BulkSelection.Count == 1, "Workspace tabs shared selection.");
            tabs.SelectedIndex = 0; quiz.SetWorkspaceActive(false); cards.SetWorkspaceActive(true); Pump(100);
            Require(ControlTree(cardRoot).OfType<CollectionSelectionFrame>().All(frame => frame.IsItemSelected), "Returning workspace lost selection decoration.");
            var cardFrame = ControlTree(cardRoot).OfType<CollectionSelectionFrame>().First();
            Require(SelectionKey(cardFrame, owner, Key.Space).Handled && cards.BulkSelection.Count == 23,
                "Workspace TabControl intercepted the editor selection shortcut.");
            Require(SelectionKey(cardRoot, owner, Key.Escape).Handled && cards.BulkSelection.Count == 0 && quiz.BulkSelection.Count == 1,
                "Hosted Escape cleared the wrong tab or reached close handling.");
            checks.Add("PASS hosted tabs: independent controllers, selection survives unload/return, inherited bindings and keyboard routing.");
        }
        finally { tabs.Items.Clear(); cards.DisposeWorkspaceEditor(); quiz.DisposeWorkspaceEditor(); owner.Close(); Pump(100); }
    }
}
