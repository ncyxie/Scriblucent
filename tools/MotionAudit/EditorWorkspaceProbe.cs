using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using Scriblucent.Localization;

internal static partial class Program
{
    private static void ProbeEditorWorkspace()
    {
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var checks = new List<string>();
        var runId = Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(Path.Combine(_output, "workspace-checks.txt"), "Running compiled workspace checks.\n");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Full, false);
            var store = Path.Combine(_output, "stores", runId, theme + "-" + language);
            Directory.CreateDirectory(store);
            var vm = new MainViewModel(settings, loadPersistedContent: false, applyThemeAndCulture: false)
                { PersistenceDirectoryOverride = store };
            Require(vm.TrySaveNotes(), "Could not initialize the isolated note store.");
            vm.EnsureEditorStoreLoaded(EditorDocumentKind.Note);
            var startup = System.Diagnostics.Stopwatch.StartNew();
            while (!vm.IsNotesDashboardReady && startup.ElapsedMilliseconds < 5000) Pump(25);
            Require(vm.IsNotesDashboardReady, "The isolated Notes library did not become ready.");
            var dashboard = new Window { DataContext = vm, Content = new TextBlock { Text = "Synthetic dashboard" }, Width = 800, Height = 600,
                ShowActivated = false, ShowInTaskbar = false };
            _app.MainWindow = dashboard;
            dashboard.Show(); Pump(50);
            var service = new EditorWorkspaceService(dashboard, vm);
            var sessions = new List<EditorSession>();
            var original = new NoteDocument { Title = "Research notes", Content = "An idea worth keeping." };
            var note = vm.AddNoteFromDocument(original);
            service.OpenNote(note);
            var window = service.Window!;
            window.ShowInTaskbar = false;
            var tabs = (TabControl)window.FindName("EditorTabs");
            Pump(550); window.UpdateLayout();
            var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs);
            var noteRoot = (FrameworkElement)host.Content;
            var rich = (RichTextBox)noteRoot.FindName("ContentTextBox");
            rich.CaretPosition = rich.Document.ContentEnd;
            rich.CaretPosition.InsertTextInRun(" Added text.");
            Require(rich.CanUndo, "Notes undo was lost on mounting.");
            var flashDoc = new FlashcardSetDocument { Id = original.Id, Title = "Review cards", SourceNoteId = original.Id,
                Cards = [new FlashcardItem { Question = "What makes recall stick?", Answer = "Practice over time.", LinkedNoteId = original.Id }] };
            service.OpenFlashcards(flashDoc, draft: true); Pump(380);
            var flashSession = window.Sessions.Single(s => s.Key.Kind == EditorDocumentKind.Flashcards);
            var flash = (FlashcardsPreviewWindow)flashSession.Editor; sessions.Add(flashSession);
            var quizDoc = new QuizDocument { Title = "Knowledge check", Questions = [new QuizQuestion { Question = "A useful question", Type = QuizQuestionType.SingleChoice,
                Options = [new QuizOption { Text = "First answer", IsCorrect = true }, new QuizOption { Text = "Second answer" }] }] };
            service.OpenQuiz(quizDoc, draft: true); Pump(380);
            var quizSession = window.Sessions.Single(s => s.Key.Kind == EditorDocumentKind.Quiz);
            var quiz = (QuizPreviewWindow)quizSession.Editor; sessions.Add(quizSession);
            var mapDoc = new MindMapDocument { Title = "Connections", Root = new MindMapNode { Text = "Central idea", Children = [new MindMapNode { Text = "Explore" }, new MindMapNode { Text = "Connect" }] } };
            service.OpenMindMap(mapDoc, draft: true); Pump(380);
            var mapSession = window.Sessions.Single(s => s.Key.Kind == EditorDocumentKind.MindMap);
            var map = (MindMapPreviewWindow)mapSession.Editor; sessions.Add(mapSession);
            Require(dashboard.IsEnabled && dashboard.IsHitTestVisible, "An editor opened modally and blocked the dashboard.");
            Require(tabs.Items.Count == 4, "Mixed tab opening or type/ID identity failed.");
            window.OpenOrFocusNote(note, vm); Pump(220);
            Require(tabs.Items.Count == 4 && ReferenceEquals(host.Content, noteRoot) && rich.CanUndo,
                "Duplicate focus replaced the retained note editor.");
            Require(original.Content == "An idea worth keeping.", "Unsaved note edits leaked into dashboard data.");
            // Ordinary Save stays mounted and persists the pending text.
            var saveButton = ControlTree(noteRoot).OfType<Button>().First(b => b.Style == noteRoot.FindResource("EditorPrimarySaveButtonStyle"));
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(550);
            var noteSession = window.Sessions.Single(s => s.Key.Kind == EditorDocumentKind.Note);
            Require(window.IsVisible && tabs.Items.Count == 4 && rich.CanUndo && original.Content != "An idea worth keeping.",
                "Note Save closed the tab, lost undo, or failed to persist.");
            Require(!noteSession.IsDirty, "A completed note Save became modified without a user edit.");
            SaveControlCapture((FrameworkElement)window.Content, $"mixed-Note-{theme}-{language}");
            foreach (var session in sessions)
            {
                window.FocusDocument(session.Key); Pump(240); window.UpdateLayout();
                var title = (TextBox)session.Content.FindName("TitleTextBox");
                title.Text += " — edited"; Pump(300);
                Require(session.Editor.WorkspaceDirty, "Title edits did not update dirty state.");
                var header = ((StackPanel)((TabItem)tabs.SelectedItem).Header).Children.OfType<TextBlock>().Single();
                Require(header.Text.Contains("edited") && header.Text.Contains('•'), "Tab header did not follow live title/dirty state.");
                var chrome = ControlTree(session.Content).OfType<Border>().First(b => b.Style == session.Content.FindResource("EditorChromeStyle"));
                var tray = (FrameworkElement)session.Content.FindName("EditorCommandTray");
                var primarySave = ControlTree(tray).OfType<Button>().Single(b => b.Style == session.Content.FindResource("EditorPrimarySaveButtonStyle"));
                var saveRect = new Rect(primarySave.TranslatePoint(new Point(), chrome), primarySave.RenderSize);
                Require(saveRect.Bottom <= chrome.ActualHeight + .6 && saveRect.Right <= chrome.ActualWidth + .6,
                    "The Save action wrapped into a clipped toolbar row.");
                Require(chrome.Margin.Top == 0 && chrome.CornerRadius == new CornerRadius(14), "An editor lost its connected chrome.");
                Require(window.SaveSession(session, false) && !session.IsDirty && tabs.Items.Count == 4 && ReferenceEquals(host.Content, session.Content),
                    "Save did not retain the active editor or clear confirmed dirty state.");
                Require(window.SaveSession(session, false) && tabs.Items.Count == 4, "Repeated Save duplicated or closed the document.");
                Pump(550);
                SaveControlCapture((FrameworkElement)window.Content, $"mixed-{session.Key.Kind}-{theme}-{language}");
            }
            Require(vm.FlashcardSets.Count == 1 && vm.Quizzes.Count == 1 && vm.MindMaps.Count == 1, "Repeated save changed draft identity.");
            Require(vm.FlashcardSets[0].Document.Cards[0].LinkedNoteId == original.Id, "Saving dropped a per-card note link.");
            window.FocusDocument(flashSession.Key);
            var lastSavedTitle = vm.FlashcardSets[0].Title;
            ((TextBox)flashSession.Content.FindName("TitleTextBox")).Text += " retry"; Pump(230);
            var blockedPath = Path.Combine(store, "blocked-store"); File.WriteAllText(blockedPath, "fixture");
            vm.PersistenceDirectoryOverride = blockedPath;
            Require(!window.SaveSession(flashSession, false) && flashSession.IsDirty && vm.FlashcardSets[0].Title == lastSavedTitle,
                "Failed persistence cleared edits or changed the dashboard's saved version.");
            vm.PersistenceDirectoryOverride = store;
            Require(window.SaveSession(flashSession, false), "Retrying a failed save did not succeed.");
            window.FocusDocument(noteSession.Key); Pump(220);
            rich.CaretPosition.InsertTextInRun(" Saved while inactive.");
            window.FocusDocument(flashSession.Key); Pump(220);
            Require(!noteRoot.IsLoaded && window.SaveSession(noteSession, false) && !noteSession.IsDirty,
                "An inactive note could not save its retained snapshot.");
            window.FocusDocument(noteSession.Key); Pump(220);
            Require(rich.CanUndo && new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Contains("Saved while inactive"),
                "Inactive saving changed the retained note editor.");
            // Drive the real scheduler without waiting for its configured wall-clock interval.
            rich.CaretPosition.InsertTextInRun(" Shared autosave.");
            window.FocusDocument(flashSession.Key); Pump(100);
            ((TextBox)flashSession.Content.FindName("TitleTextBox")).Text += " autosaved";
            window.FocusDocument(quizSession.Key); Pump(100);
            ((TextBox)quizSession.Content.FindName("TitleTextBox")).Text += " autosaved";
            var tick = typeof(EditorWorkspaceWindow).GetMethod("AutoSaveTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            settings.EnableAutoSave = true;
            tick.Invoke(window, [null, EventArgs.Empty]);
            typeof(EditorWorkspaceWindow).GetField("_nextAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, DateTime.UtcNow.AddSeconds(-1));
            tick.Invoke(window, [null, EventArgs.Empty]); Pump(800);
            Require(!noteSession.IsDirty && !flashSession.IsDirty && !quizSession.IsDirty,
                "The shared scheduler failed to save active and retained inactive editors.");
            settings.EnableAutoSave = false;
            tick.Invoke(window, [null, EventArgs.Empty]);
            window.FocusDocument(noteSession.Key); Pump(120);
            Require(rich.CanUndo && new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Contains("Shared autosave"),
                "Shared autosave rebuilt the note document or lost undo.");
            var activeTab = (TabItem)tabs.SelectedItem;
            typeof(EditorWorkspaceWindow).GetMethod("CommitTabReorder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [activeTab, 3]);
            Require(ReferenceEquals(tabs.Items[3], activeTab) && ReferenceEquals(host.Content, noteRoot) && rich.CanUndo,
                "Reordering a mixed tab rebuilt its active document.");
            window.FocusDocument(flashSession.Key);
            ShellWindowChrome.SuspendForFullscreen(window); Pump(80);
            window.FocusDocument(quizSession.Key); Pump(120);
            Require(!ShellWindowChrome.IsFullscreenSuspended(window) && window.WindowState == WindowState.Normal,
                "Leaving a fullscreen editor did not restore workspace chrome.");
            // Responsive command bands retain every command when the title needs its own row.
            window.Width = 860; window.Height = 650;
            foreach (var session in window.Sessions)
            {
                window.FocusDocument(session.Key); Pump(260); window.UpdateLayout();
                var tray = (FrameworkElement)session.Content.FindName("EditorCommandTray");
                var save = ControlTree(tray).OfType<Button>().Single(b => b.Style == session.Content.FindResource("EditorPrimarySaveButtonStyle"));
                var chrome = ControlTree(session.Content).OfType<Border>().First(b => b.Style == session.Content.FindResource("EditorChromeStyle"));
                var rect = new Rect(save.TranslatePoint(new Point(), chrome), save.RenderSize);
                Require(rect.Bottom <= chrome.ActualHeight + .6 && rect.Right <= chrome.ActualWidth + .6 && save.IsVisible,
                    "Narrow layout clipped its Save action.");
                SaveControlCapture((FrameworkElement)window.Content, $"narrow-{session.Key.Kind}-{theme}-{language}");
            }
            window.Width = 1080; window.Height = 780;
            // Switching doesn't rebuild the map: mutate the retained tree after a save and ensure
            // dashboard snapshot stays unchanged, then return to the same controller/root.
            var rootField = typeof(MindMapPreviewWindow).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var workingRoot = (MindMapNode)rootField.GetValue(map)!;
            workingRoot.Text = "Uncommitted map text";
            Require(vm.MindMaps[0].Document.Root.Text == "Central idea", "Saved map snapshot shares mutable editor nodes.");
            window.FocusDocument(flashSession.Key); Pump(80); window.FocusDocument(mapSession.Key); Pump(80);
            Require(ReferenceEquals(rootField.GetValue(map), workingRoot), "Returning to a map rebuilt or replaced its state.");
            foreach (var mode in new[] { MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(mode, false);
                window.FocusDocument(quizSession.Key); Pump(220);
                Require(ReferenceEquals(host.Content, quizSession.Content) && host.IsHitTestVisible, "Reduced/Off lost active content input.");
            }
            ((TextBox)flashSession.Content.FindName("TitleTextBox")).Text += " bulk pending";
            ((TextBox)quizSession.Content.FindName("TitleTextBox")).Text += " bulk pending"; Pump(230);
            RespondToWorkspaceDialogs(ModernDialogResult.Primary, ModernDialogResult.Cancel);
            Require(!window.PrepareDocumentRemoval([flashSession.Key, quizSession.Key])
                && !flashSession.IsDisposed && !quizSession.IsDisposed && tabs.Items.Count == 4,
                "Bulk-removal cancellation disposed an earlier approved tab.");
            Require(window.SaveSession(flashSession, false) && window.SaveSession(quizSession, false), "Bulk-cancelled tabs lost pending edits.");
            // A cancelled removal retains every session; accepted removal releases only the target.
            RespondToWorkspaceDialog(ModernDialogResult.Cancel);
            vm.RequestDeleteMindMap(vm.MindMaps[0]);
            Require(!mapSession.IsDisposed && tabs.Items.Count == 4 && vm.MindMaps.Count == 1,
                "Cancelled removal disposed editor sessions.");
            RespondToWorkspaceDialog(ModernDialogResult.Primary);
            vm.RequestDeleteMindMap(vm.MindMaps[0]);
            Require(mapSession.IsDisposed && tabs.Items.Count == 3 && vm.MindMaps.Count == 0 && vm.TrashedMindMaps.Count == 1,
                "Accepted removal did not dispose its session.");
            window.FocusDocument(flashSession.Key);
            ((TextBox)flashSession.Content.FindName("TitleTextBox")).Text += " pending"; Pump(230);
            var preflight = typeof(EditorWorkspaceWindow).GetField("_preflighting", BindingFlags.Instance | BindingFlags.NonPublic)!;
            preflight.SetValue(window, true);
            try
            {
                Require(!window.PreflightWorkspaceClose() && !window.PrepareDocumentRemoval([flashSession.Key]),
                    "A pending confirmation allowed another close/removal preflight.");
                window.Close();
                Require(window.IsVisible && tabs.Items.Count == 3, "Close reentered an existing confirmation.");
            }
            finally { preflight.SetValue(window, false); }
            RespondToWorkspaceDialog(ModernDialogResult.Cancel);
            window.Close(); Pump(160);
            Require(window.IsVisible && tabs.Items.Count == 3 && !flashSession.IsDisposed, "Cancelled workspace close disposed open tabs.");
            RespondToWorkspaceDialog(ModernDialogResult.Cancel);
            Require(!WindowCloseCascade.TryCloseOwnedWindows(dashboard) && window.IsVisible && tabs.Items.Count == 3,
                "A cancelled owner-close cascade disposed the workspace.");
            RespondToWorkspaceDialog(ModernDialogResult.Secondary);
            window.CloseDocument(flashSession.Key); Pump(160);
            Require(flashSession.IsDisposed && tabs.Items.Count == 2 && vm.FlashcardSets[0].Title.Contains("pending"),
                "Save and close did not persist before releasing the tab.");
            MotionPolicy.Configure(MotionPreference.Full, false);
            // The plus menu must open every kind, including Notes, without a dashboard opening hook.
            foreach (var kind in Enum.GetValues<EditorDocumentKind>())
            {
                var before = window.Sessions.Select(session => session.Key).ToHashSet();
                service.Create(kind);
                window.UpdateLayout();
                var created = window.Sessions.Single(session => !before.Contains(session.Key));
                Require(created.Key.Kind == kind && ReferenceEquals(host.Content, created.Content),
                    $"Creating {kind} did not open and select its editor.");
                var header = ControlTree(created.Content).OfType<Border>().Single(border => border.Style == created.Content.FindResource("EditorChromeStyle"));
                var commandTray = (FrameworkElement)created.Content.FindName("EditorCommandTray");
                var titleField = (FrameworkElement)created.Content.FindName("EditorTitleField");
                var anchor = header.TranslatePoint(new Point(), tabs);
                for (var frame = 0; frame < 8; frame++)
                {
                    Pump(25);
                    Require(created.Content.RenderTransform.Value.IsIdentity && created.Content.Opacity == 1
                        && header.Opacity == 1 && (header.TranslatePoint(new Point(), tabs) - anchor).Length < .02,
                        $"The new {kind} editor moved/faded its connected header at frame {frame}.");
                }
                var titleRect = new Rect(titleField.TranslatePoint(new Point(), header), titleField.RenderSize);
                var trayRect = new Rect(commandTray.TranslatePoint(new Point(), header), commandTray.RenderSize);
                var contentTop = Math.Min(titleRect.Top, trayRect.Top);
                var contentBottom = Math.Max(titleRect.Bottom, trayRect.Bottom);
                Require(Math.Abs(contentTop - (header.ActualHeight - contentBottom)) < 1,
                    $"{kind} header padding is uneven: {contentTop} / {header.ActualHeight - contentBottom}.");
                SaveControlCapture((FrameworkElement)window.Content, $"created-{kind}-{theme}-{language}");
                if (kind == EditorDocumentKind.Note)
                    Require(vm.Notes.Any(item => item.Document.Id == created.Key.Id), "A newly opened note was not added to the dashboard.");
            }
            var dragged = (TabItem)tabs.Items[1];
            tabs.SelectedItem = dragged; Pump(250);
            typeof(EditorWorkspaceWindow).GetMethod("BeginTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [dragged]);
            try
            {
                var rail = (Panel)tabs.Template.FindName("HeaderPanel", tabs);
                var x = dragged.TranslatePoint(new Point(), rail).X + 45;
                typeof(EditorWorkspaceWindow).GetMethod("UpdateTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [x]);
                Pump(60);
                var face = (Border)dragged.Template.FindName("HeaderBorder", dragged);
                var plate = (TabSelectionSurface)tabs.Template.FindName("TabSelectionPlate", tabs);
                Require(face.CornerRadius == new CornerRadius(10) && SurfaceMaterial.GetKind(face) == SurfaceMaterialKind.Raised
                    && EditorChromeContext.GetIsTabDragging(plate), "A dragged tab kept its square docked foot or lost its foreground face.");
                SaveControlCapture((FrameworkElement)window.Content, $"dragged-tab-{theme}-{language}");
            }
            finally { typeof(EditorWorkspaceWindow).GetMethod("EndTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); }
            Pump(80);
            Require(!EditorChromeContext.GetIsTabDragging((TabSelectionSurface)tabs.Template.FindName("TabSelectionPlate", tabs)),
                "Dropping a tab did not restore its connected selection face.");
            var closingSessions = window.Sessions.ToArray();
            ShellWindowChrome.SuspendForFullscreen(window);
            Require(WindowCloseCascade.TryCloseOwnedWindows(dashboard), "A clean owner-close cascade was rejected.");
            dashboard.Close(); Pump(350);
            Require(!window.IsVisible && sessions.All(s => s.IsDisposed) && closingSessions.All(s => s.IsDisposed), "Workspace close leaked retained controllers.");
            checks.Add($"PASS {theme}/{language}: production service/non-modal dashboard; compiled mixed editors; typed identity; in-place/repeated save; failed-write rollback/retry; live title/dirty events; isolated snapshots and retained links/undo/root; inactive save and shared autosave; retained reorder; fullscreen switch; responsive chrome at 860/1080 DIP; removal cancel/discard and bulk preflight cancellation; confirmation reentry blocked; workspace/owner-close cancel and accepted fullscreen owner cascade; Save and close; all four plus-menu creation routes select their editor; connected header remains stationary throughout arrival; balanced title/toolbar insets; fully rounded detached tab during dragging and restored connection on drop; Reduced/Off cleanup; captures 100/125/150/200%.");
            Console.WriteLine($"PASS workspace {theme}/{language}");
            File.WriteAllLines(Path.Combine(_output, "workspace-checks.txt"), checks);
        }
        ProbeNoteCreationBeforeLibraryReady(settings, runId);
        checks.Add("PASS note creation before the Notes library is ready: awaits the initial read, preserves the existing stored note, adds the new note to the dashboard, opens and selects its retained editor.");
        File.WriteAllLines(Path.Combine(_output, "workspace-checks.txt"), checks);
    }

    private static void ProbeNoteCreationBeforeLibraryReady(AppSettings settings, string runId)
    {
        LoadNativeControlResources("Light"); LocalizationService.SetCulture("en");
        MotionPolicy.Configure(MotionPreference.Off, false);
        var store = Path.Combine(_output, "stores", runId, "pending-notes");
        Directory.CreateDirectory(store);
        var seed = new MainViewModel(settings, loadPersistedContent: false, applyThemeAndCulture: false)
            { PersistenceDirectoryOverride = store };
        var existing = new NoteDocument { Title = "Existing library note", Content = "Keep this stored content." };
        seed.AddNoteFromDocument(existing);
        Require(seed.TrySaveNotes(), "Could not initialize the pending-library fixture.");
        var vm = new MainViewModel(settings, loadPersistedContent: false, applyThemeAndCulture: false)
            { PersistenceDirectoryOverride = store };
        var dashboard = new Window { DataContext = vm, Width = 800, Height = 600,
            ShowActivated = false, ShowInTaskbar = false };
        _app.MainWindow = dashboard;
        dashboard.Show();
        var service = new EditorWorkspaceService(dashboard, vm);
        service.OpenQuiz(new QuizDocument { Title = "An editor opened before Notes" });
        var window = service.Window!;
        window.ShowInTaskbar = false;
        Require(!vm.IsNotesDashboardReady, "The fixture did not exercise the initial Notes read.");
        service.Create(EditorDocumentKind.Note);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!window.Sessions.Any(session => session.Key.Kind == EditorDocumentKind.Note)
            && timeout.ElapsedMilliseconds < 5000) Pump(25);
        Require(vm.IsNotesDashboardReady, "Creating a note did not await the initial Notes read.");
        var created = window.Sessions.Single(session => session.Key.Kind == EditorDocumentKind.Note);
        var tabs = (TabControl)window.FindName("EditorTabs");
        var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs);
        Require(ReferenceEquals(host.Content, created.Content) && vm.Notes.Count == 2
            && vm.Notes.Any(item => item.Document.Id == created.Key.Id)
            && vm.Notes.Any(item => item.Document.Id == existing.Id && item.Document.Content == existing.Content),
            "Creating during library loading failed to select the new note or replaced stored notes.");
        Require(WindowCloseCascade.TryCloseOwnedWindows(dashboard), "The pending-library fixture could not close cleanly.");
        dashboard.Close(); Pump(50);
        Console.WriteLine("PASS workspace note creation before library ready");
    }

    private static void RespondToWorkspaceDialog(ModernDialogResult result) => RespondToWorkspaceDialogs(result);
    private static void RespondToWorkspaceDialogs(params ModernDialogResult[] results)
    {
        var pending = new Queue<ModernDialogResult>(results);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<ModernMessageDialog>().FirstOrDefault(w => w.IsVisible) is not { } dialog) return;
            var result = pending.Dequeue();
            if (pending.Count == 0) timer.Stop();
            var name = result switch { ModernDialogResult.Primary => "PrimaryButton", ModernDialogResult.Secondary => "NoButton", _ => "CancelButton" };
            ((Button)dialog.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
    }
}
