using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeEditorToolbar(bool includeTabScroll = true, bool includeSpacing = false)
    {
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var checks = new List<string>();
        var runId = Guid.NewGuid().ToString("N")[..8];
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var store = Path.Combine(_output, "stores", runId, theme + "-" + language);
            Directory.CreateDirectory(store);
            var vm = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = store };
            var linked = new NoteDocument { Title = "Research notes — a long linked document title" };
            var note = vm.AddNoteFromDocument(linked);
            var dashboard = new Window { DataContext = vm, Width = 800, Height = 600,
                ShowActivated = false, ShowInTaskbar = false };
            _app.MainWindow = dashboard; dashboard.Show();
            var service = new EditorWorkspaceService(dashboard, vm);
            if (includeSpacing) service.OpenNote(note);
            service.OpenFlashcards(new FlashcardSetDocument { Title = "Review cards", SourceNoteId = linked.Id });
            service.OpenQuiz(new QuizDocument { Title = "Knowledge check", SourceNoteId = linked.Id,
                Questions = [new QuizQuestion { Question = "A useful question", Type = QuizQuestionType.SingleChoice,
                    Options = [new QuizOption { Text = "First answer", IsCorrect = true }, new QuizOption { Text = "Second answer" }] }] });
            service.OpenMindMap(new MindMapDocument { Title = "Connections", SourceNoteId = linked.Id,
                Root = new MindMapNode { Text = "Central idea" } });
            var window = service.Window!; window.ShowInTaskbar = false;
            try
            {
                foreach (var session in window.Sessions)
                {
                    window.FocusDocument(session.Key); Pump(80); window.UpdateLayout();
                    if (includeSpacing) ProbeEditorSpacing(window, session, theme, language);
                    if (session.Key.Kind == EditorDocumentKind.Note) continue;
                    var button = (Button)session.Content.FindName("LinkedNoteButton");
                    var combo = ControlTree(session.Content).OfType<ComboBox>()
                        .First(control => control.DisplayMemberPath == "Title" && control.SelectedValuePath == "Id");
                    // Quiz editing shows its selector in the same slot as the preview link action.
                    if (session.Editor is Scriblucent.Views.QuizPreviewWindow quiz) quiz.IsEditMode = false;
                    window.UpdateLayout();
                    Require(button.Visibility == Visibility.Visible, "A linked-note action did not appear for its linked note.");
                    Require(button.ActualWidth >= 96 && button.ActualWidth <= 180 && button.ActualHeight == 32 && button.FontSize == 14,
                        $"{session.Key.Kind} linked-note action has a different toolbar footprint: {button.ActualWidth} x {button.ActualHeight}.");
                    Require(button.Content?.ToString() == linked.Title, "The linked-note action lost its title binding.");
                    combo.ApplyTemplate(); Pump(40);
                    var label = (TextBlock)combo.Template.FindName("LinkedNoteLabel", combo);
                    Require(label.Text == linked.Title, $"{session.Key.Kind} selector still binds to the unshown controller window.");
                    Require(combo.Width == button.MaxWidth && combo.Height == button.Height && combo.FontSize == button.FontSize,
                        "A linked-note selector and action use different density rules.");
                    var toggle = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("DropDownToggle", combo);
                    Require(toggle.HorizontalAlignment == HorizontalAlignment.Stretch,
                        "The linked-note selector only opens from its small arrow.");
                    var header = ControlTree(session.Content).OfType<Border>()
                        .Single(border => border.Style == session.Content.FindResource("EditorChromeStyle"));
                    SaveControlCapture(header, $"linked-header-{session.Key.Kind}-{theme}-{language}");
                    button.Content = "Ideas"; window.UpdateLayout();
                    Require(button.ActualWidth >= 96 && button.ActualWidth < 150,
                        $"A short linked title still reserves a long blank button: {button.ActualWidth}.");
                    SaveControlCapture(header, $"linked-short-{session.Key.Kind}-{theme}-{language}");
                    button.Content = linked.Title;
                    if (session.Editor is Scriblucent.Views.QuizPreviewWindow quizSelector) quizSelector.IsEditMode = true;
                    else if (!combo.IsVisible) ((Button)session.Content.FindName(session.Key.Kind == EditorDocumentKind.Flashcards
                        ? "OptionsPanelToggleButton" : "ArrangePanelToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(40); window.UpdateLayout();
                    Require(combo.IsVisible, $"The actual {session.Key.Kind} linked-note selector could not be shown.");
                    var chevron = (FrameworkElement)combo.Template.FindName("Chevron", combo);
                    var center = new Point(chevron.ActualWidth / 2, chevron.ActualHeight / 2);
                    var point = chevron.TranslatePoint(center, combo);
                    SaveLocalControlCapture(combo, $"linked-selector-{session.Key.Kind}-{theme}-{language}");
                    Require(Math.Abs(combo.ActualWidth - point.X - chevron.ActualWidth / 2 - 8) < 1
                        && Math.Abs(point.Y - combo.ActualHeight / 2) < .5,
                        $"The selector arrow is misplaced: {point}, {chevron.RenderSize}, selector {combo.RenderSize}.");
                    combo.IsDropDownOpen = true; Pump(40); window.UpdateLayout();
                    var openPoint = chevron.TranslatePoint(center, combo);
                    Require((openPoint - point).Length < .1, "Opening the dropdown moved its arrow.");
                    SaveLocalControlCapture(combo, $"linked-selector-open-{session.Key.Kind}-{theme}-{language}");
                    combo.IsDropDownOpen = false;
                    combo.SelectedItem = null; Pump(40);
                    Require(label.Text == LocalizationService.GetString("QuizLinkedNoteNone"), "The empty selector lost its localized placeholder.");
                    Require(button.Visibility == Visibility.Collapsed, "An unlinked document still shows a placeholder link action.");
                    if (session.Editor is Scriblucent.Views.QuizPreviewWindow quizEditor)
                    {
                        quizEditor.IsEditMode = true; Pump(40);
                        Require(combo.Visibility == Visibility.Visible && combo.IsVisible,
                            "Hiding an unlinked action also hid the quiz's note selector in edit mode.");
                        quizEditor.IsEditMode = false;
                    }
                    SaveControlCapture(header, $"linked-none-{session.Key.Kind}-{theme}-{language}");
                }
            }
            finally { window.CloseAfterPreflight(); dashboard.Close(); Pump(80); }
            if (includeTabScroll) ProbeWorkspaceTabs(theme, language, checks);
            checks.Add($"PASS {theme}/{language}: actual Flashcards/Quiz/MindMap controllers share 32 DIP linked-note controls and 14 DIP text; navigation fits short titles up to 180 DIP; selector is 180 DIP; compact trailing spacing; arrow centered at an 8 DIP trailing inset, stationary on opening; retained linked title and localized empty selection; full selector hit area; unlinked action collapses while edit selector stays available. Tab-scroll checks included: {includeTabScroll}.");
            if (includeSpacing) checks.Add($"PASS {theme}/{language}: all four title statuses release unused space and fit changing messages; note checkmark occupies space only during saved feedback; options panels have equal top/bottom insets at 1080/860 DIP; quiz question surfaces have no shadow layers.");
            File.WriteAllLines(Path.Combine(_output, "toolbar-checks.txt"), checks);
            Console.WriteLine($"PASS editor toolbar/scroll {theme}/{language}");
        }
    }

    private static void SaveLocalControlCapture(FrameworkElement control, string name)
    {
        // A nested selector's parent offset can lie outside its own bitmap bounds.
        // Capture its local visual bounds rather than the original parent position.
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(control.RenderSize);
            context.DrawRectangle((System.Windows.Media.Brush)_app.FindResource("WindowBackground"), null, bounds);
            // A header may draw only at its left edge. Auto viewbox would stretch
            // that small drawing across the entire layout slot and distort icons.
            context.DrawRectangle(new System.Windows.Media.VisualBrush(control)
            {
                ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewbox = bounds,
                ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewport = bounds
            }, null, bounds);
        }
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(control.ActualWidth * scale),
                (int)(control.ActualHeight * scale), scale * 96, scale * 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_output, $"{name}-{(int)(scale * 100)}.png"));
            encoder.Save(file);
        }
    }
}
