using System.Windows;
using System.Windows.Controls;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeEditorSpacing(EditorWorkspaceWindow window, EditorSession session, string theme, string language)
    {
        var status = (EditorTitleStatus)session.Content.FindName("EditedIndicatorText");
        var title = (TextBox)session.Content.FindName("TitleTextBox");
        var header = ControlTree(session.Content).OfType<Border>()
            .Single(border => border.Style == session.Content.FindResource("EditorChromeStyle"));
        UiMotion.SetInlineStatusVisible(status, false, animate: false); window.UpdateLayout();
        var freeWidth = title.ActualWidth;
        Require(status.DesiredSize.Width == 0, "Dismissed title status still reserves space.");
        foreach (var message in new[] { LocalizationService.GetString("EditedIndicator"), LocalizationService.GetString("Saved"), "Saving changes…" })
        {
            status.Text = message;
            UiMotion.SetInlineStatusVisible(status, true, animate: false); window.UpdateLayout();
            Require(status.MinWidth == 0 && title.ActualWidth < freeWidth, "Title status has a fixed reservation or overlaps the title.");
            var usedWidth = freeWidth - title.ActualWidth;
            Require(Math.Abs(usedWidth - status.ActualWidth - 12) < 1,
                $"{session.Key.Kind} reserves more than its message: used={usedWidth}, status={status.ActualWidth}.");
            SaveLocalControlCapture(header, $"status-{session.Key.Kind}-{theme}-{language}-{message.Length}");
        }
        UiMotion.SetInlineStatusVisible(status, false, animate: false); window.UpdateLayout();
        Require(Math.Abs(title.ActualWidth - freeWidth) < 1, "Title space did not return after dismissing status.");
        SaveLocalControlCapture(header, $"status-clear-{session.Key.Kind}-{theme}-{language}");

        if (session.Key.Kind == EditorDocumentKind.Quiz)
        {
            var questions = (ItemsControl)session.Content.FindName("QuestionsItemsControl");
            questions.UpdateLayout();
            Require(questions.Items.Count > 0, "Quiz fixture has no question.");
            Require(!ControlTree(questions).OfType<CardShadowLayer>().Any(), "Quiz question backgrounds still have a shadow.");
            SaveLocalControlCapture(questions, $"questions-{theme}-{language}");
        }
        if (session.Key.Kind is not (EditorDocumentKind.Flashcards or EditorDocumentKind.MindMap)) return;
        var panel = (Border)session.Content.FindName(session.Key.Kind == EditorDocumentKind.Flashcards ? "FlashcardsOptionsPanel" : "ArrangePanel");
        var toggle = (Button)session.Content.FindName(session.Key.Kind == EditorDocumentKind.Flashcards ? "OptionsPanelToggleButton" : "ArrangePanelToggleButton");
        if (!panel.IsVisible) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(250); window.UpdateLayout();
        foreach (var width in new[] { 1080d, 860d })
        {
            window.Width = width; Pump(40); window.UpdateLayout();
            var commands = ControlTree(panel).OfType<EditorCommandPanel>().Single();
            var children = commands.Children.OfType<FrameworkElement>().Where(child => child.IsVisible).ToArray();
            var top = children.Min(child => child.TranslatePoint(new Point(), panel).Y);
            var bottom = panel.ActualHeight - children.Max(child => child.TranslatePoint(new Point(0, child.ActualHeight), panel).Y);
            Require(Math.Abs(top - bottom) < 1 && Math.Abs(top - 10) < 1,
                $"{session.Key.Kind} options are unbalanced at {width}: top={top}, bottom={bottom}.");
            SaveLocalControlCapture(panel, $"options-{session.Key.Kind}-{theme}-{language}-{width}");
        }
        window.Width = 1080; window.UpdateLayout();
    }
}
