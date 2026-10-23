using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private const BindingFlags LoadingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void ProbeDashboardLoading(string theme)
    {
        LoadNativeControlResources(theme);
        var checks = new List<string>();
        void Mark(string message) { checks.Add(message); Console.WriteLine(message); }
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, 150000, Timeout.Infinite);

        // These cases retain MainWindow.ContentRendered and InitializeStartupData in production order.
        foreach (var count in new[] { 0, 24, 400 })
        {
            var (window, vm, store) = LoadingWindow(theme, MotionPreference.Full, count);
            var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
            var shell = (DashboardLoadingOverlay)window.FindName("DashboardLoadingShell");
            bool firstFrame = false, contentRendered = false;
            EventHandler frame = (_, _) =>
            {
                if (firstFrame || !window.IsVisible) return;
                firstFrame = true;
                Require(layer.Visibility == Visibility.Visible && layer.Opacity == 1 && shell.Opacity == 1,
                    "Actual startup exposed the unfinished dashboard on its first frame.");
            };
            window.ContentRendered += (_, _) => contentRendered = true;
            CompositionTarget.Rendering += frame;
            try
            {
                Require(layer.Visibility == Visibility.Visible && layer.Opacity == 1 && shell.Opacity == 1,
                    "The loading cover was not prepared before Show.");
                window.Show();
                WaitLoading(() => firstFrame && contentRendered && vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed);
                Require((bool)typeof(MainWindow).GetField("_startupDataInitialized", LoadingFlags)!.GetValue(window)!,
                    "The test bypassed actual ContentRendered startup initialization.");
                Require(vm.Notes.Count == count && !layer.IsHitTestVisible && !shell.IsWaveRunning, "Startup lost data or retained loading input/motion.");
                if (count == 400) Require(vm.NoteGroups.Count == 1, "The grouped startup fixture was not applied.");
                Mark($"PASS {theme}: actual first paint and ContentRendered startup, {count} notes, ready handoff and cleanup");
            }
            finally { CompositionTarget.Rendering -= frame; CloseLoadingWindow(window); }
        }

        foreach (var kind in Enum.GetValues<EditorDocumentKind>())
        foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            var (window, vm, _) = LoadingWindow(theme, mode, 24, held: true);
            var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
            var shell = (DashboardLoadingOverlay)window.FindName("DashboardLoadingShell");
            try
            {
                window.Show(); window.Activate();
                SelectLoadingDashboard(vm, kind); Pump(180);
                Require(!vm.IsActiveDashboardReady && layer.Visibility == Visibility.Visible && layer.Opacity == 1 && shell.Opacity == 1,
                    "Held loading introduced a delayed/transparent cover.");
                Require(mode != MotionPreference.Off || !shell.IsWaveRunning, "Off animated the shimmer.");
                var builds = shell.DrawingBuildCount; Pump(80);
                Require(shell.DrawingBuildCount == builds, "The native sweep rebuilt geometry every frame.");
                if (mode == MotionPreference.Full)
                {
                    SaveControlCapture((FrameworkElement)window.Content, $"loading-{theme}-{kind}-wide");
                    window.Width = 760; Pump(100);
                    SaveControlCapture((FrameworkElement)window.Content, $"loading-{theme}-{kind}-narrow");
                    window.Width = 1280; Pump(50);
                }
                // Release only this dashboard's real store read after the held-preview assertions.
                var handoff = Stopwatch.StartNew();
                bool reported = false;
                EventHandler readiness = (_, _) =>
                {
                    if (reported || handoff.ElapsedMilliseconds < 500 || !vm.IsActiveDashboardReady || layer.Visibility != Visibility.Visible) return;
                    reported = true;
                    var active = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                    var grids = ControlTree(active).OfType<DashboardVirtualizedCardGrid>().Where(g => g.IsVisible && g.HasItems).ToArray();
                    var notes = ControlTree(active).OfType<NoteCardControl>().Where(c => c.IsVisible && !c.IsBitmapMode).ToArray();
                    Mark($"WAIT {theme}/{kind}/{mode}: view visible={active.IsVisible}; grids={string.Join(',', grids.Select(g => g.HasRealizedContainers()))}; cached note previews={notes.Count(c => c.DataContext is NoteCardViewModel n && n.IsContentPreviewCached)}/{notes.Length}");
                };
                CompositionTarget.Rendering += readiness;
                SetLoadingField(vm, LoadedField(kind), false);
                vm.EnsureEditorStoreLoaded(kind);
                try { WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed); }
                finally { CompositionTarget.Rendering -= readiness; }
                handoff.Stop();
                Require(!layer.IsHitTestVisible && !shell.IsWaveRunning && LoadingCallbacksReleased(window), "The handoff leaked input, clocks or callbacks.");
                var page = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                var host = (FrameworkElement)page.FindName(vm.ActiveDashboard + "DashboardContentHost");
                Require(host.Opacity == 1 && host.RenderTransform.Value.IsIdentity, "Hydration added a second entrance under the dissolving skeleton.");
                if (mode == MotionPreference.Full) SaveControlCapture((FrameworkElement)window.Content, $"loading-{theme}-{kind}-ready");
                Mark($"PASS {theme}/{kind}/{mode}: opaque held loading, retained geometry, responsive preview, real read + handoff {handoff.ElapsedMilliseconds}ms, stationary content and released input");
            }
            finally { CloseLoadingWindow(window); }
        }
        ProbeLoadingCancellation(theme, Mark);
        ProbeLoadingFailureAndRetry(theme, Mark);
        File.WriteAllLines(Path.Combine(_output, "loading-checks.txt"), checks);
    }

    private static void ProbeLoadingCancellation(string theme, Action<string> mark)
    {
        var (window, vm, _) = LoadingWindow(theme, MotionPreference.Full, 0, held: true);
        var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
        try
        {
            // Content already ready before first paint: there must be no forced skeleton interval.
            SetLoadingField(vm, "_isNotesDashboardReady", true);
            SyncLoading(window, vm);
            Require(layer.Visibility == Visibility.Collapsed, "Ready-before-paint content was held behind a skeleton.");
            window.Show(); Pump(50);
            SetLoadingField(vm, "_isNotesDashboardReady", false); SyncLoading(window, vm); Pump(40);
            SetLoadingField(vm, "_isNotesDashboardReady", true); SyncLoading(window, vm);
            vm.ShowFlashcardsCommand.Execute(null); Pump(300);
            Require(!vm.IsActiveDashboardReady && layer.Visibility == Visibility.Visible && layer.Opacity == 1,
                "An old ready callback dismissed the new dashboard's loading cover.");
            SetLoadingField(vm, LoadedField(EditorDocumentKind.Flashcards), false);
            vm.EnsureEditorStoreLoaded(EditorDocumentKind.Flashcards);
            WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed);
            mark($"PASS {theme}: ready-before-paint bypass and navigation cancels superseded completion");

            vm.ShowNotesCommand.Execute(null); Pump(60);
            SetLoadingField(vm, "_isNotesDashboardReady", false); SyncLoading(window, vm);
            ((FrameworkElement)window.FindName("NotesDashboardView")).Visibility = Visibility.Collapsed;
            SetLoadingField(vm, "_isNotesDashboardReady", true); SyncLoading(window, vm);
            Pump(250); Require(layer.Visibility == Visibility.Visible, "The realization fallback revealed an unrealized page immediately.");
            WaitLoading(() => layer.Visibility == Visibility.Collapsed, 4000);
            Require(LoadingCallbacksReleased(window), "The elapsed realization fallback retained its render observer.");
            mark($"PASS {theme}: elapsed-time realization fallback releases the cover and callbacks");
        }
        finally { CloseLoadingWindow(window); }
    }

    private static void ProbeLoadingFailureAndRetry(string theme, Action<string> mark)
    {
        var (window, vm, store) = LoadingWindow(theme, MotionPreference.Full, 0);
        File.WriteAllText(Path.Combine(store, "notes.json"), "{broken");
        try
        {
            window.Show();
            WaitLoading(() => vm.IsActiveDashboardLoadFailed);
            var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
            var failure = (FrameworkElement)window.FindName("DashboardLoadFailurePanel");
            var shell = (DashboardLoadingOverlay)window.FindName("DashboardLoadingShell");
            Require(failure.IsVisible && layer.Opacity == 1 && !shell.IsWaveRunning, "Failure did not replace the skeleton cleanly.");
            File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData()));
            ControlTree(failure).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(layer.Opacity == 1 && shell.IsVisible, "Retry exposed the unfinished page.");
            WaitLoading(() => vm.IsActiveDashboardReady && layer.Visibility == Visibility.Collapsed);
            Require(!layer.IsHitTestVisible && LoadingCallbacksReleased(window), "Retry left an input blocker or callbacks.");
            mark($"PASS {theme}: actual malformed-store failure, opaque retry, empty-dashboard handoff and cleanup");
        }
        finally { CloseLoadingWindow(window); }
    }

    private static (MainWindow Window, MainViewModel Model, string Store) LoadingWindow(string theme, MotionPreference mode, int noteCount, bool held = false)
    {
        var store = Path.Combine(_output, "stores", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(store);
        AppSettingsService.SettingsDirectoryOverride = store;
        var settings = new AppSettings
        {
            Theme = theme, EnableAutoSave = false, EnableAiFeatures = false, AnimationPreference = mode,
            IsRecentSectionVisible = mode != MotionPreference.Reduced,
            IsCalendarSectionExpanded = mode != MotionPreference.Reduced,
            IsCalendarFirst = mode != MotionPreference.Off,
            IsGroupsFirst = mode != MotionPreference.Reduced,
            IsFlashcardDashboardGroupsFirst = mode != MotionPreference.Reduced,
            IsQuizDashboardGroupsFirst = mode != MotionPreference.Reduced,
            IsMindMapDashboardGroupsFirst = mode != MotionPreference.Reduced
        };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var group = Guid.NewGuid();
        File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData
        {
            Notes = Enumerable.Range(0, noteCount).Select(i => new NoteDocument
            { Title = $"Planning note {i + 1}", Content = "A short thought to organize and develop.", GroupId = noteCount == 400 && i < 12 ? group : null }).ToList(),
            Groups = noteCount == 400 ? [new NoteGroupData { GroupId = group, Name = "Research" }] : []
        }));
        File.WriteAllText(Path.Combine(store, "flashcard-sets.json"), JsonSerializer.Serialize(new FlashcardSetStoreData
        { Sets = [new FlashcardSetDocument { Title = "Review set" }] }));
        File.WriteAllText(Path.Combine(store, "mind-maps.json"), JsonSerializer.Serialize(new MindMapStoreData
        { Maps = [NewDocumentDefaults.MindMap()] }));
        File.WriteAllText(Path.Combine(store, "quizzes.json"), JsonSerializer.Serialize(new QuizStoreData
        { Quizzes = [new QuizDocument { Title = "Knowledge check" }] }));
        MotionPolicy.Configure(mode, false);
        var window = new MainWindow(settings) { Width = 1280, Height = 900, ShowInTaskbar = false };
        _app.MainWindow = window;
        var vm = (MainViewModel)window.DataContext; vm.PersistenceDirectoryOverride = store;
        if (held)
        {
            typeof(MainWindow).GetField("_startupDataInitialized", LoadingFlags)!.SetValue(window, true);
            foreach (var kind in Enum.GetValues<EditorDocumentKind>()) SetLoadingField(vm, LoadedField(kind), true);
        }
        return (window, vm, store);
    }
    private static string LoadedField(EditorDocumentKind kind) => kind switch
    { EditorDocumentKind.Note => "_notesPersistedContentLoaded", EditorDocumentKind.Flashcards => "_flashcardsPersistedContentLoaded",
        EditorDocumentKind.MindMap => "_mindMapsPersistedContentLoaded", _ => "_quizzesPersistedContentLoaded" };
    private static void SelectLoadingDashboard(MainViewModel vm, EditorDocumentKind kind)
    {
        var command = kind switch { EditorDocumentKind.Note => vm.ShowNotesCommand, EditorDocumentKind.Flashcards => vm.ShowFlashcardsCommand,
            EditorDocumentKind.MindMap => vm.ShowMindMapsCommand, _ => vm.ShowQuizzesCommand };
        command.Execute(null);
    }
    private static void SetLoadingField(MainViewModel vm, string field, bool value) => typeof(MainViewModel).GetField(field, LoadingFlags)!.SetValue(vm, value);
    private static void SyncLoading(MainWindow window, MainViewModel vm) => typeof(MainWindow).GetMethod("SyncDashboardLoadingState", LoadingFlags)!.Invoke(window, [vm]);
    private static bool LoadingCallbacksReleased(MainWindow window) => new[] { "_loadingRenderHandler", "_loadingFinishOperation" }
        .All(name => typeof(MainWindow).GetField(name, LoadingFlags)!.GetValue(window) is null);
    private static void WaitLoading(Func<bool> ready, int limitMs = 10000)
    {
        var clock = Stopwatch.StartNew(); while (!ready() && clock.ElapsedMilliseconds < limitMs) Pump(20);
        Require(ready(), "Loading did not reach its expected state before the deadline.");
    }
    private static void CloseLoadingWindow(MainWindow window)
    {
        window.Close(); Pump(35);
        (typeof(ActivityTracker).GetField("_timer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as DispatcherTimer)?.Stop();
        AppSettingsService.Flush();
        Require(LoadingCallbacksReleased(window) && !((DashboardLoadingOverlay)window.FindName("DashboardLoadingShell")).IsWaveRunning,
            "Closing the window retained loading work.");
    }
}
