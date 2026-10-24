using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeFlashcardLayout(bool quick)
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var modes = quick ? new[] { MotionPreference.Off } : new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off };
        foreach (var theme in new[] { "Dark", "Light" })
        foreach (var language in new[] { "lt", "en" })
        foreach (var mode in modes)
        foreach (var media in new[] { false, true })
            CheckFlashcardLayout(theme, language, mode, media);
    }

    private static void CheckFlashcardLayout(string theme, string language, MotionPreference mode, bool media)
    {
        LoadNativeControlResources(theme);
        Scriblucent.ThemeManager.SetTheme(theme);
        LocalizationService.SetCulture(language);
        MotionPolicy.Configure(mode, false);
        var category = media ? "Ilgesnė kategorija mokymosi kortelėms" : "Biologija";
        var originals = new[]
        {
            new FlashcardItem { Question = "Mokymosi klausimas", Answer = "Teisingas atsakymas", Category = category,
                Hint = media ? string.Join("\n\n", Enumerable.Repeat("Ilgesnė užuomina padeda prisiminti ir suprasti atsakymą. Šis tekstas turi visas lietuviškas raides: ąčęėįšųūž.", 12)) : "Trumpa užuomina.",
                QuestionImageFileName = media ? "layout-fixture.png" : null, AnswerImageFileName = media ? "layout-fixture.png" : null,
                QuestionAudioFileName = media ? "layout-fixture.wav" : null, AnswerAudioFileName = media ? "layout-fixture.wav" : null },
            new FlashcardItem { Question = "Kitas klausimas", Answer = "Kitas atsakymas" }
        };
        var editor = new FlashcardsPreviewWindow(originals, studyDueCardsOnly: false);
        var items = (List<FlashcardsPreviewWindow.FlashcardPreviewItem>)RemainingField(editor, "_allItems")!;
        var first = items[0]; var second = items[1];
        if (media)
        {
            typeof(FlashcardsPreviewWindow.FlashcardPreviewItem).GetField("_questionImageSource", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, PhotoFixture());
            typeof(FlashcardsPreviewWindow.FlashcardPreviewItem).GetField("_answerImageSource", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, PhotoFixture());
        }
        Window owner;
        FrameworkElement root;
        if (media)
        {
            owner = editor;
            owner.ShowActivated = false; owner.ShowInTaskbar = false;
            root = (FrameworkElement)editor.Content;
        }
        else
        {
            var workspace = new Scriblucent.EditorWorkspaceWindow { ShowActivated = false, ShowInTaskbar = false };
            owner = workspace;
            root = editor.AttachToWorkspace(owner, () => true, () => { });
            workspace.AddSession(new EditorSession { Key = new EditorDocumentKey(EditorDocumentKind.Flashcards, Guid.NewGuid()),
                Editor = editor, Content = root, Save = () => true }, false);
        }
        string suffix = $"{theme}-{language}-{mode}-{(media ? "media" : "text")}";
        SurfaceMaterialBorder Face(DependencyObject parent) => ControlTree(parent).OfType<SurfaceMaterialBorder>()
            .First(face => face.Name == "CardSurface" && face.IsVisible && ReferenceEquals(face.DataContext, first));
        try
        {
            owner.Show(); Pump(mode == MotionPreference.Off ? 140 : 500);
            var editorFace = Face(root);
            var grip = ControlTree(editorFace).OfType<ItemReorderGrip>().Single();
            var gripPosition = grip.TranslatePoint(new Point(), editorFace);
            Require(grip.IsVisible && ItemReorderBehavior.GetIsHandle(grip) && gripPosition.X <= 16 && gripPosition.Y <= 16,
                "Drag handle is not in the leading corner");
            var front = FlashcardAnchors(editorFace, category, false);
            SaveFlashcardLayout(editorFace, "editor-front-" + suffix);
            editor.BulkSelection.SelectAll(); Pump(80);
            var frame = ControlTree(root).OfType<CollectionSelectionFrame>().First(f => f.ItemId == first.Id);
            var checkbox = ControlTree(frame).OfType<CollectionSelectionToggle>().Single();
            Require(checkbox.IsVisible && checkbox.IsChecked == true && frame.IsItemSelected, "Selection state is missing");
            var checkPosition = checkbox.TranslatePoint(new Point(), editorFace);
            Require(checkPosition.X > 260 && checkPosition.Y <= 16, "Selection checkbox left its trailing corner");
            foreach (var label in ControlTree(editorFace).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)))
                Require(!new Rect(label.TranslatePoint(new Point(), editorFace), label.RenderSize).IntersectsWith(new Rect(checkPosition, checkbox.RenderSize)),
                    "Selection checkbox overlaps card text: " + label.Text + " " + new Rect(label.TranslatePoint(new Point(), editorFace), label.RenderSize) + " / " + new Rect(checkPosition, checkbox.RenderSize));
            SaveFlashcardLayout(editorFace, "editor-selected-" + suffix);
            editor.BulkSelection.End(); Pump(80);
            Require(editor.TryReorderCards([first], second, true) && editor.TryReorderCards([first], second, false), "Reordering failed");
            Pump(100);
            frame = ControlTree(root).OfType<CollectionSelectionFrame>().First(f => f.ItemId == first.Id);
            var flip = ControlTree(frame).OfType<FlashcardFlipHost>().First();
            var flipButton = ControlTree(Face(root)).OfType<Button>().Single(b => b.IsVisible && b.ToolTip as string == LocalizationService.GetString("FlashcardsTapHint"));
            flipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitForFlashcardFlip(flip);
            Require(flip.IsFlipped, $"Header flip command did not flip the editor card (animating={flip.IsAnimating}, model={first.IsFlipped}, hostModel={((FlashcardsPreviewWindow.FlashcardPreviewItem)flip.DataContext).Id == first.Id}, duration={RemainingCall(editor, "GetFlipAnimationDurationMs")})");
            var back = FlashcardAnchors(Face(root), category, true);
            SaveFlashcardLayout(Face(root), "editor-back-" + suffix);
            RemainingCall(editor, "StartStudyModeButton_Click", editor, new RoutedEventArgs()); Pump(500);
            var card = (FrameworkElement)editor.FindName("StudyModeCard");
            var studyFace = Face(card);
            CompareFlashcardAnchors(front, FlashcardAnchors(studyFace, category, false));
            Require(!ControlTree(studyFace).OfType<ItemReorderGrip>().Single().IsVisible, "Study exposes a reorder handle");
            Require(ControlTree(studyFace).OfType<Button>().Any(b => b.IsVisible && b.ToolTip as string == LocalizationService.GetString("Edit")), "Study edit command missing");
            var scroll = (ScrollViewer)editor.FindName("StudyModeScrollViewer");
            Require(scroll.ScrollableHeight < 1, "Study no longer fits the default workspace");
            SaveFlashcardLayout((FrameworkElement)owner.Content, "study-window-" + suffix);
            var hint = ControlTree(studyFace).OfType<ToggleButton>().Single(b => b.IsVisible);
            var popup = (Popup)editor.FindName("FlashcardHintPopup");
            hint.Focus();
            foreach (var routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
                hint.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner)!, Environment.TickCount, Key.Space) { RoutedEvent = routed });
            Pump(180);
            Require(popup.IsOpen && hint.IsChecked == true, "Hint did not open");
            Require(scroll.ScrollableHeight < 1 && card.ActualHeight == 248, "Hint changed card or window height");
            var hintScroll = (ScrollViewer)editor.FindName("FlashcardHintScrollViewer");
            Require(hintScroll.ActualHeight <= 134.5 && hintScroll.ActualWidth <= 260.5, "Hint surface exceeded its bounds");
            if (media)
            {
                Require(hintScroll.ScrollableHeight > 0, "Long hint cannot be scrolled");
                hintScroll.ScrollToBottom(); Pump(80); Require(hintScroll.VerticalOffset > 0, "Hint scrolling did not move");
            }
            SaveFlashcardLayout((FrameworkElement)popup.Child, "hint-" + suffix);
            RemainingCall(editor, "FlashcardHintPopup_PreviewKeyDown", popup.Child,
                new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Require(!popup.IsOpen && hint.IsChecked == false, "Escape did not reset hint state");
            hint.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(80);
            owner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Require(!popup.IsOpen, "Outside click did not dismiss hint");
            var more = ControlTree(studyFace).OfType<Button>().Single(b => b.IsVisible && b.ToolTip as string == LocalizationService.GetString("MassSelectMoreActions"));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(160);
            Require(more.ContextMenu is { IsOpen: true } menu && ReferenceEquals(menu.DataContext, first), "More menu lost its card context");
            var commands = more.ContextMenu!.Items.OfType<MenuItem>().Select(command => command.Header as string).ToArray();
            Require(commands.Contains(LocalizationService.GetString("MoveToSet")) && !commands.Contains(LocalizationService.GetString("MassSelectModeTitle")), "Study menu has the wrong actions");
            Require(commands.Contains(LocalizationService.GetString("FlashcardRestartAudio")) == media, "Restart audio availability is wrong");
            more.ContextMenu.IsOpen = false;
            var menuClosing = System.Diagnostics.Stopwatch.StartNew();
            while (more.ReadLocalValue(FrameworkElement.ContextMenuProperty) != DependencyProperty.UnsetValue && menuClosing.ElapsedMilliseconds < 1500) Pump(20);
            Require(more.ReadLocalValue(FrameworkElement.ContextMenuProperty) == DependencyProperty.UnsetValue, "Closed menu retained stale card actions");
            hint.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(80);
            editor.SetWorkspaceActive(false);
            Require(!popup.IsOpen, "Leaving the editor did not dismiss the hint");
            editor.SetWorkspaceActive(true);
            hint.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(80);
            ((Button)editor.FindName("StudyModeNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(450);
            Require(!popup.IsOpen && ReferenceEquals(card.DataContext, second), "Changing cards did not dismiss the hint");
            var edit = ControlTree(card).OfType<Button>().First(button => button.IsVisible && button.ToolTip as string == LocalizationService.GetString("Edit"));
            edit.Focus();
            edit.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner)!, Environment.TickCount, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(450);
            Require(ReferenceEquals(card.DataContext, first), "Card navigation shortcuts failed with a command focused");
            hint = ControlTree(Face(card)).OfType<ToggleButton>().Single(button => button.IsVisible);
            hint.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(80);
            var studyHost = (FlashcardFlipHost)editor.FindName("StudyModeFlipHost");
            RemainingCall(editor, "AnimateFlashcardFlip", studyHost); WaitForFlashcardFlip(studyHost);
            Require(!popup.IsOpen && studyHost.IsFlipped, "Flip did not dismiss hint");
            CompareFlashcardAnchors(back, FlashcardAnchors(Face(card), category, true));
            SaveFlashcardLayout(Face(card), "study-back-" + suffix);
            RemainingCall(editor, "StartStudyModeButton_Click", editor, new RoutedEventArgs()); Pump(400);
            RemainingCall(editor, "CramModeButton_Click", editor, new RoutedEventArgs()); Pump(500);
            Require(((FrameworkElement)editor.FindName("CramModeBanner")).IsVisible && scroll.ScrollableHeight < 1, "Practice notice adds scrolling");
            SaveFlashcardLayout((FrameworkElement)owner.Content, "practice-window-" + suffix);
            Console.WriteLine("Passed flashcard layout, corners, selection, reorder, flip, hint, menus and practice fit: " + suffix);
        }
        finally { editor.DisposeWorkspaceEditor(); if (!ReferenceEquals(owner, editor)) owner.Close(); Pump(80); }
    }

    private static Dictionary<string, Point> FlashcardAnchors(FrameworkElement face, string category, bool answer)
    {
        Require(face.ActualWidth == 316 && face.ActualHeight == 224, "Card dimensions changed");
        var labels = ControlTree(face).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).ToArray();
        foreach (var label in labels)
        {
            var position = label.TranslatePoint(new Point(), face);
            Require(position.X >= 0 && position.Y >= 0 && position.X + label.ActualWidth <= 316.5 && position.Y + label.ActualHeight <= 224.5,
                "Card text outside its surface: " + label.Text);
        }
        return new[] { LocalizationService.GetString(answer ? "FlashcardSideAnswer" : "FlashcardSideQuestion"),
            LocalizationService.GetString("FlashcardStatusNew"), category, answer ? "Teisingas atsakymas" : "Mokymosi klausimas" }
            .ToDictionary(text => text, text => labels.Single(label => label.Text == text).TranslatePoint(new Point(), face));
    }

    private static void CompareFlashcardAnchors(Dictionary<string, Point> editor, Dictionary<string, Point> study)
    {
        foreach (var (text, position) in editor)
            Require((position - study[text]).Length <= 1, $"Shared card position moved: {text}: {position} / {study[text]}");
    }

    private static void SaveFlashcardLayout(FrameworkElement element, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Snapshot(element, 1.25)));
        using var file = File.Create(Path.Combine(_output, name + ".png")); encoder.Save(file);
    }

    private static void WaitForFlashcardFlip(FlashcardFlipHost host)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (host.IsAnimating && deadline.ElapsedMilliseconds < 3000) Pump(20);
        Require(!host.IsAnimating, "Flashcard flip did not complete");
        Pump(30);
    }
}
