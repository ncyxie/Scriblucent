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
    private static void ProbeUiUnification(bool quick)
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme); LocalizationService.SetCulture(language);
            if (!quick) foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
            foreach (var width in new[] { 920d, 520d })
            {
                MotionPolicy.Configure(mode, false);
                var suffix = $"{theme}-{language}-{mode}-{width}";
                var owner = new Window { Width = width, Height = 820, ShowInTaskbar = false, ShowActivated = false };
                owner.Show(); Pump(35); _app.MainWindow = owner;
                void Capture(Window dialog, string name)
                {
                    if (mode == MotionPreference.Automatic) SaveControlCapture((FrameworkElement)dialog.Content, name + "-" + suffix);
                }
                void Show(Window dialog)
                {
                    dialog.Owner = owner; dialog.Show(); Pump(400); dialog.UpdateLayout();
                    var shell = (OverlayDialogShell)dialog.Content;
                    var card = (FrameworkElement)shell.Template.FindName("DialogCard", shell);
                    var bounds = card.TransformToAncestor(shell).TransformBounds(new Rect(card.RenderSize));
                    Require(bounds.Left >= 23 && bounds.Right <= shell.ActualWidth - 23,
                        "Dialog card or shadow reservation overflowed: " + suffix + " " + bounds);
                }
                var input = new SimpleInputDialog("Synthetic input", language == "lt" ? "Pavadinimas" : "Title", "",
                    value => value.Length >= 3, language == "lt" ? "Įrašykite bent tris simbolius." : "Enter at least three characters.");
                Show(input); Capture(input, "input-error");
                ((TextBox)input.FindName("InputBox")).Text = "Readable"; Pump(400);
                Require(((Button)input.FindName("ConfirmButton")).IsEnabled && !((InlineValidationPresenter)input.FindName("ValidationText")).IsError,
                    "Input correction failed: " + suffix); input.Close(); Pump(35);

                var link = new InsertHyperlinkDialog("Synthetic link", Array.Empty<NoteCardViewModel>());
                Show(link); RemainingCall(link, "Insert_Click", link, new RoutedEventArgs()); Pump(400); Capture(link, "link-error");
                Require(((InlineValidationPresenter)link.FindName("ValidationText")).IsError, "Link error missing: " + suffix);
                RemainingCall(link, "HideValidationMessage"); Pump(400); link.Close(); Pump(35);

                var flashcard = new EditFlashcardDialog(isNew: true, categoryOptions: new[] { "Research" });
                Show(flashcard); RemainingCall(flashcard, "SaveButton_Click", flashcard, new RoutedEventArgs()); Pump(400);
                Require(((InlineValidationPresenter)flashcard.FindName("QuestionValidation")).IsError
                    && ((InlineValidationPresenter)flashcard.FindName("AnswerValidation")).IsError, "Inline flashcard errors missing: " + suffix);
                Capture(flashcard, "flashcard-errors"); flashcard.Question = "A useful question"; flashcard.Answer = "A clear answer";
                for (var i = 0; i < 10; i++)
                {
                    flashcard.QuestionImageFileName = i % 2 == 0 ? "synthetic-preview.png" : null;
                    flashcard.QuestionAudioFileName = i % 2 == 0 ? "synthetic-audio.wav" : null;
                    Pump(20);
                }
                flashcard.QuestionImageFileName = "synthetic-preview.png"; flashcard.QuestionAudioFileName = "synthetic-audio.wav";
                Pump(500); flashcard.UpdateLayout(); Capture(flashcard, "flashcard-attachments");
                foreach (var name in new[] { "QuestionAttachmentsHost", "AnswerAttachmentsHost" })
                {
                    var row = (LayoutRevealHost)flashcard.FindName(name);
                    Require(row.IsEnabled && row.IsHitTestVisible && !NativeSurfaceMotion.IsRunning(row, MotionChannel.Layout),
                        "Attachment row did not settle interactively: " + suffix);
                    foreach (var button in ControlTree(row).OfType<Button>().Where(b => b.IsVisible))
                    {
                        var bounds = button.TransformToAncestor(row).TransformBounds(new Rect(button.RenderSize));
                        Require(bounds.Left >= -.5 && bounds.Right <= row.ActualWidth + .5 && bounds.Bottom <= row.ActualHeight + .5,
                            "Attachment actions overflowed: " + suffix + " " + bounds);
                    }
                }
                Require(!((InlineValidationPresenter)flashcard.FindName("QuestionValidation")).IsError
                    && !((InlineValidationPresenter)flashcard.FindName("AnswerValidation")).IsError, "Editing did not clear flashcard errors.");
                flashcard.Close(); Pump(35);

                var quiz = new QuizStartOptionsDialog(300, 70); Show(quiz);
                foreach (var box in ControlTree(quiz).OfType<CheckBox>())
                foreach (var label in ControlTree(box).OfType<TextBlock>())
                {
                    var bounds = label.TransformToAncestor(box).TransformBounds(new Rect(label.RenderSize));
                    Require(bounds.Right <= box.ActualWidth + .5, "Form checkbox label overflowed: " + suffix);
                }
                var options = (ComboBox)quiz.FindName("TimeLimitComboBox");
                var custom = options.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "custom");
                for (var i = 0; i < 8; i++)
                {
                    options.SelectedIndex = 1; options.SelectedItem = custom;
                    Require(((TextBox)quiz.FindName("CustomTimeTextBox")).IsEnabled, "Custom-time reveal blocked input.");
                    Pump(20);
                }
                ((TextBox)quiz.FindName("CustomTimeTextBox")).Text = "0";
                Require(RemainingCall(quiz, "TryCommitValues") is false, "Invalid custom time was committed."); Pump(500); Capture(quiz, "quiz-time-error");
                ((TextBox)quiz.FindName("CustomTimeTextBox")).Text = "20";
                ((TextBox)quiz.FindName("PassingScoreTextBox")).Text = "101";
                Require(RemainingCall(quiz, "TryCommitValues") is false, "Invalid passing score was committed."); Pump(400); Capture(quiz, "quiz-score-error");
                ((TextBox)quiz.FindName("PassingScoreTextBox")).Text = "70";
                Require(RemainingCall(quiz, "TryCommitValues") is true && quiz.TimeLimitSeconds == 1200, "Valid quiz settings changed semantics.");
                Pump(500); Require(((Grid)quiz.FindName("CustomTimePanel")).IsEnabled, "Custom time stayed disabled.");
                quiz.Close(); Pump(35); owner.Close(); Pump(250);
                Require(!RenderTransitionClock.IsAnyRunning, "UI unification left active clocks: " + suffix);
                checks.Add(suffix + ": compiled forms, inline error correction, attachment interruption/containment and custom quiz-time input passed.");
                Console.WriteLine(checks.Last());
            }
            MotionPolicy.Configure(MotionPreference.Off, false);
            var editor = new Scriblucent.NoteEditorWindow(); editor.Show(); Pump(550);
            RemainingCall(editor, "SetFontPanelOpen", true, false, false); editor.UpdateLayout();
            RemainingCall(editor, "OpenTableSizePicker"); Pump(100);
            var picker = (TableSizePicker)editor.FindName("TableSizePickerGrid");
            Require(picker.IsKeyboardFocused && picker.Rows == 3 && picker.Columns == 3, "Table popup did not focus its initial selection.");
            picker.Select(4, 6); SaveControlCapture((FrameworkElement)((Popup)editor.FindName("TableSizePickerPopup")).Child, "table-picker-" + theme + "-" + language);
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(100);
            Require(!((Popup)editor.FindName("TableSizePickerPopup")).IsOpen && ((Button)editor.FindName("InsertTableButton")).IsKeyboardFocused,
                "Table dismissal did not restore toolbar focus.");
            RemainingCall(editor, "OpenTableSizePicker"); Pump(100); picker.Select(2, 4);
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            var document = ((RichTextBox)editor.FindName("ContentTextBox")).Document;
            var table = document.Blocks.OfType<Table>().Single();
            Require(table.RowGroups.Single().Rows.Count == 2 && table.Columns.Count == 4, "Table insertion used animated instead of logical dimensions.");
            typeof(Scriblucent.NoteEditorWindow).GetField("_allowCloseWithoutPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, true);
            editor.Close(); Pump(300);
            checks.Add(theme + "-" + language + ": compiled table popup, focus restoration and real document insertion passed.");
        }
        File.WriteAllLines(Path.Combine(_output, "ui-unification-checks.txt"), checks);
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Synthetic checks queued a preference write.");
    }
}
