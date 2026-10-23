using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeEditorSaveStatus()
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
            Require(vm.TrySaveNotes(), "Could not initialize the isolated Notes store.");
            vm.EnsureEditorStoreLoaded(EditorDocumentKind.Note);
            var ready = System.Diagnostics.Stopwatch.StartNew();
            while (!vm.IsNotesDashboardReady && ready.ElapsedMilliseconds < 5000) Pump(25);
            Require(vm.IsNotesDashboardReady, "The synthetic Notes store did not load.");
            var dashboard = new Window { DataContext = vm, Width = 800, Height = 600, ShowInTaskbar = false, ShowActivated = false };
            _app.MainWindow = dashboard; dashboard.Show();
            var service = new EditorWorkspaceService(dashboard, vm);
            service.OpenNote(vm.AddNoteFromDocument(new NoteDocument { Title = "Research notes", Content = "A saved idea." }));
            service.OpenFlashcards(new FlashcardSetDocument { Title = "Review cards", Cards = [new FlashcardItem { Question = "Question", Answer = "Answer" }] }, draft: true);
            service.OpenQuiz(new QuizDocument { Title = "Knowledge check", Questions = [new QuizQuestion { Question = "Question", Type = QuizQuestionType.SingleChoice,
                Options = [new QuizOption { Text = "Answer", IsCorrect = true }] }] }, draft: true);
            service.OpenMindMap(new MindMapDocument { Title = "Connections", Root = new MindMapNode { Text = "Central idea" } }, draft: true);
            var window = service.Window!; window.ShowInTaskbar = false;
            var blockedPath = Path.Combine(store, "blocked-store"); File.WriteAllText(blockedPath, "fixture");
            try
            {
                foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(mode, false);
                    foreach (var session in window.Sessions)
                    {
                        window.FocusDocument(session.Key); Pump(100);
                        var status = (EditorTitleStatus)session.Content.FindName("EditedIndicatorText");
                        var title = (TextBox)session.Content.FindName("TitleTextBox");
                        var chrome = ControlTree(session.Content).OfType<Border>().Single(b => b.Style == session.Content.FindResource("EditorChromeStyle"));
                        title.Text += " edit"; Pump(260);
                        Require(status.Feedback == EditorSaveFeedback.None && status.Text == LocalizationService.GetString("EditedIndicator"), "Ordinary editing was reported as a save failure.");
                        Require(window.SaveSession(session, false), "Saving to the synthetic store failed.");
                        Pump(35);
                        Require(status.Feedback == EditorSaveFeedback.Saved && status.Text == LocalizationService.GetString("Saved"), $"{session.Key.Kind} did not show success.");
                        Require(status.Label.Opacity == 1 && status.Glyph.Opacity == 1
                            && !NativeSurfaceMotion.IsRunning(status.Glyph, MotionChannel.Interaction)
                            && !NativeSurfaceMotion.IsRunning(status.Label, MotionChannel.Presence), "Save glyph and label have separate animation clocks.");
                        Pump(260); window.UpdateLayout();
                        Require(status.Glyph.IsVisible && status.Label.IsVisible && status.ActualWidth > 26, "Saved glyph/label did not fit their content.");
                        var suffix = $"{session.Key.Kind}-{theme}-{language}-{mode}";
                        SaveLocalControlCapture(chrome, "saved-" + suffix);
                        title.Text += " retry";
                        vm.PersistenceDirectoryOverride = blockedPath;
                        Require(!window.SaveSession(session, false) && session.IsDirty, "Failed write cleared pending edits.");
                        Pump(260); window.UpdateLayout();
                        Require(status.Feedback == EditorSaveFeedback.Failed && status.Text == LocalizationService.GetString("NotSaved") && status.Glyph.IsVisible,
                            $"{session.Key.Kind} did not show the failure X.");
                        SaveLocalControlCapture(chrome, "failed-" + suffix);
                        vm.PersistenceDirectoryOverride = store;
                        Require(window.SaveSession(session, false), "Retry after failure did not save.");
                        Pump(300);
                        Require(status.Feedback == EditorSaveFeedback.Saved && !session.IsDirty, "Successful retry did not replace failure feedback.");
                        var other = window.Sessions.First(s => s != session);
                        window.FocusDocument(other.Key); Pump(80);
                        Require(status.Feedback == EditorSaveFeedback.None, "Unloaded tab retained a stale success flash.");
                        window.FocusDocument(session.Key); Pump(100);
                        Require(status.Visibility == Visibility.Collapsed, "Returning to a saved tab replayed stale feedback.");
                        title.Text += " inactive retry";
                        vm.PersistenceDirectoryOverride = blockedPath;
                        Require(!window.SaveSession(session, false), "The blocked-store fixture unexpectedly saved.");
                        window.FocusDocument(other.Key); Pump(80);
                        window.FocusDocument(session.Key); Pump(80);
                        Require(status.Feedback == EditorSaveFeedback.Failed && status.IsVisible, "Tab switching dismissed an unresolved save failure.");
                        window.FocusDocument(other.Key); Pump(80);
                        vm.PersistenceDirectoryOverride = store;
                        Require(window.SaveSession(session, false) && status.Feedback == EditorSaveFeedback.None, "Saving an inactive tab did not clear its previous failure.");
                        window.FocusDocument(session.Key); Pump(80);
                        Require(status.Visibility == Visibility.Collapsed, "An inactive success replayed old failure text.");
                        checks.Add($"PASS {suffix}: ordinary edit; confirmed success; shared glyph/text clock; failed write and X; successful retry; tab unload; persistent failure across switches; inactive retry.");
                    }
                    Console.WriteLine($"PASS save feedback {theme}/{language}/{mode}");
                    File.WriteAllLines(Path.Combine(_output, "save-status-checks.txt"), checks);
                }
            }
            finally { vm.PersistenceDirectoryOverride = store; window.CloseAfterPreflight(); dashboard.Close(); Pump(80); }
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "The probe queued a preferences write.");
        File.WriteAllText(Path.Combine(_output, "README.txt"), "Compiled editor save feedback: all four editors, Light/Dark, EN/LT, Full/Reduced/Off. Writes use generated isolated stores. PNGs are raster captures at 100/125/150/200 percent; these are not physical-monitor DPI or FPS measurements.\n");
    }
}
