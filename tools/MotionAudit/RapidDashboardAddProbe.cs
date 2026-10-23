using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Services;
using Scriblucent.Models;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeRapidDashboardAdd(bool captureChoreography = false, bool checkSpam = false, bool checkInput = false, bool startEmpty = false, bool useLibraryCopy = false)
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        LoadNativeControlResources("Light");
        var window = new MainWindow(settings) { Width = 1280, Height = 960, ShowInTaskbar = false, ShowActivated = false };
        typeof(MainWindow).GetField("_startupDataInitialized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        var vm = (MainViewModel)window.DataContext;
        vm.PersistenceDirectoryOverride = Path.Combine(_output, "store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vm.PersistenceDirectoryOverride);
        if (useLibraryCopy)
            File.Copy(_rapidAddLibrarySource ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent", "notes.json"),
                Path.Combine(vm.PersistenceDirectoryOverride, "notes.json"));
        else Require(vm.TrySaveNotes(), "Could not initialize the isolated note store.");
        vm.EnsureEditorStoreLoaded(EditorDocumentKind.Note);
        var startup = System.Diagnostics.Stopwatch.StartNew();
        while (!vm.IsNotesDashboardReady && startup.ElapsedMilliseconds < 5000) Pump(25);
        Require(vm.IsNotesDashboardReady, "The isolated Notes library did not become ready.");
        _app.MainWindow = window;
        try
        {
            window.Show(); Pump(650); MotionPolicy.Configure(MotionPreference.Full, false);
            var page = (FrameworkElement)window.FindName("NotesDashboardView");
            var root = (FrameworkElement)page.FindName("UngroupedSectionBody");
            var viewer = (ScrollViewer)page.FindName("NotesScrollViewer");
            if (checkInput)
            {
                ReducedMotionPreferences.Configure(MotionPreference.Off);
                for (var index = 0; index < (startEmpty || useLibraryCopy ? 0 : 12); index++)
                    vm.AddNoteFromDocument(new NoteDocument { Title = $"Existing {index + 1}" });
                Pump(800);
                if (useLibraryCopy) root.BringIntoView();
                else viewer.ScrollToVerticalOffset(300);
                Pump(650);
                ReducedMotionPreferences.Configure(MotionPreference.Full); Pump(700);
                var reports = new List<string>();
                var layoutOffsets = (ConditionalWeakTable<FrameworkElement, TranslateTransform>)typeof(DashboardMotionEngine)
                    .GetField("ReflowTranslations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var creationMenu = ControlTree(page).OfType<Scriblucent.Controls.DashboardCreateMenu>().Single();
                creationMenu.IsOpen = true; Pump(300);
                var addButton = creationMenu.QuickAddButton;
                foreach (var (interval, targetCount) in new[] { (15, 25), (15, 50), (30, 25), (30, 50), (70, 25), (70, 50), (110, 25), (110, 50) })
                {
                    var initialCount = vm.Notes.Count;
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    long lastFrame = 0, worstFrame = 0, lastClick = 0, worstCommand = 0;
                    var count = 0;
                    var poses = new List<string>();
                    var worstOverlap = 0;
                    var maxHeldDuringInput = 0;
                    var visibleDuringInput = false;
                    var movingFrames = 0;
                    var lateMovingFrames = 0;
                    var interpolatedFrames = 0;
                    var previousOffsets = new Dictionary<Guid, Vector>();
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
                        { Interval = TimeSpan.FromMilliseconds(interval) };
                    var observationQueued = false;
                    void ObserveFrame()
                    {
                        observationQueued = false;
                        var now = elapsed.ElapsedMilliseconds;
                        if (lastFrame > 0) worstFrame = Math.Max(worstFrame, now - lastFrame);
                        lastFrame = now;
                        var cards = DashboardMotionCardCatalog.EnumerateLayoutRoots(root, DashboardMotionScope.Notes).ToArray();
                        var moving = false;
                        var interpolating = false;
                        foreach (var card in cards)
                            if (DashboardMotionCardCatalog.TryGetItemId(card, out var id) && layoutOffsets.TryGetValue(card, out var offset))
                            {
                                var current = new Vector(offset.X, offset.Y);
                                if (current.Length > .5)
                                {
                                    moving = true;
                                    if (previousOffsets.TryGetValue(id, out var previous) && previous.Length > current.Length + .1)
                                        interpolating = true;
                                }
                                previousOffsets[id] = current;
                            }
                        if (count > 1 && count < targetCount && moving) movingFrames++;
                        if (count > 5 && count < targetCount && moving) lateMovingFrames++;
                        if (count > 1 && count < targetCount && interpolating) interpolatedFrames++;
                        double DisplayedOpacity(FrameworkElement card)
                        {
                            var opacity = card.Opacity;
                            for (var parent = VisualTreeHelper.GetParent(card); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                                if (parent is UIElement element) opacity *= element.Opacity;
                            return opacity;
                        }
                        if (count > 2 && count < targetCount)
                        {
                            maxHeldDuringInput = Math.Max(maxHeldDuringInput, cards.Count(card => !card.IsHitTestVisible || DisplayedOpacity(card) < .9));
                            visibleDuringInput |= cards.Any(card => DisplayedOpacity(card) > .9);
                        }
                        var rects = cards.Where(card => DisplayedOpacity(card) > .9).Select(card => card.TransformToAncestor(root)
                            .TransformBounds(new Rect(card.RenderSize))).ToArray();
                        var overlaps = 0;
                        var overlapDetails = new List<string>();
                        for (var left = 0; left < rects.Length; left++)
                            for (var right = left + 1; right < rects.Length; right++)
                            {
                                var shared = Rect.Intersect(rects[left], rects[right]);
                                if (!shared.IsEmpty && shared.Width > 2 && shared.Height > 2)
                                {
                                    overlaps++;
                                    overlapDetails.Add($"{left}:{rects[left]} / {right}:{rects[right]} intersection={shared}");
                                }
                            }
                        worstOverlap = Math.Max(worstOverlap, overlaps);
                        if (overlaps > 0) poses.Add(string.Join(" | ", overlapDetails));
                        if (count <= 6 || overlaps > 0 || moving)
                            poses.Add($"{now}ms click={count} overlaps={overlaps}: " + string.Join(" | ", cards.Take(12).Select(card =>
                                $"{card.Opacity:F2}/{DisplayedOpacity(card):F2}/{card.IsHitTestVisible}/{NativeSurfaceMotion.IsRunning(card, MotionChannel.Presence)}: {card.RenderTransform.Value}")));
                    }
                    EventHandler render = (_, _) =>
                    {
                        // WPF updates native clocks before Rendering, while the shared
                        // procedural clock advances inside that event. Observe after all
                        // handlers have run so we don't compare poses from two frames.
                        if (observationQueued) return;
                        observationQueued = true;
                        window.Dispatcher.BeginInvoke(ObserveFrame, System.Windows.Threading.DispatcherPriority.Render);
                    };
                    CompositionTarget.Rendering += render;
                    timer.Tick += (_, _) =>
                    {
                        if (count < targetCount)
                        {
                            var click = System.Diagnostics.Stopwatch.StartNew();
                            var button = addButton;
                            Require(button.IsVisible, "The floating Add button is not visible.");
                            button.Focus();
                            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                            worstCommand = Math.Max(worstCommand, click.ElapsedMilliseconds);
                            count++; lastClick = elapsed.ElapsedMilliseconds;
                            return;
                        }
                        // Observe without invoking IsPendingEnter or layout synchronization.
                        var cards = DashboardMotionCardCatalog.EnumerateLayoutRoots(root, DashboardMotionScope.Notes).ToArray();
                        var held = cards.Count(card => !card.IsHitTestVisible || card.Opacity < .99
                            || Math.Abs(card.RenderTransform.Value.OffsetX) > .01
                            || Math.Abs(card.RenderTransform.Value.OffsetY) > .01
                            || Math.Abs(card.RenderTransform.Value.M11 - 1) > .01);
                        if ((cards.Length > 0 && vm.Notes.Count == initialCount + targetCount && held == 0 && !DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes))
                            || elapsed.ElapsedMilliseconds - lastClick > 2000)
                        {
                            var report = $"interval={interval}; count={count}; notes={vm.Notes.Count}; realized={cards.Length}; total={elapsed.ElapsedMilliseconds}; worst frame gap={worstFrame}; command={worstCommand}; settling={elapsed.ElapsedMilliseconds - lastClick}; held={held}; held during input={maxHeldDuringInput}; visible during input={visibleDuringInput}; moving frames={movingFrames}; late moving frames={lateMovingFrames}; interpolated frames={interpolatedFrames}; overlaps={worstOverlap}; gate={DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes)}";
                            reports.Add(report); Console.WriteLine(report);
                            timer.Stop(); frame.Continue = false;
                        }
                    };
                    timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
                    CompositionTarget.Rendering -= render;
                    File.WriteAllLines(Path.Combine(_output, $"input-add-{interval}-{targetCount}-poses.txt"), poses);
                    File.WriteAllLines(Path.Combine(_output, "input-add-checks.txt"), reports);
                    if (targetCount >= 25)
                        Require(visibleDuringInput, "Cards appeared only after the burst stopped.");
                    Require(movingFrames > 0 && lateMovingFrames > 0 && interpolatedFrames > 0,
                        "Sibling movement stopped animating during rapid input.");
                    Require(worstOverlap == 0, "Opaque cards overlapped during the burst, even though they eventually settled.");
                    AssertRapidAddLayout(root); Pump(800);
                }
                return;
            }
            if (checkSpam)
            {
                ReducedMotionPreferences.Configure(MotionPreference.Off);
                for (var index = 0; index < 12; index++)
                    vm.AddNoteFromDocument(new NoteDocument { Title = $"Existing {index + 1}" });
                Pump(800); viewer.ScrollToVerticalOffset(300); Pump(650);
                ReducedMotionPreferences.Configure(MotionPreference.Full); Pump(700);
                var spamChecks = new List<string>();
                foreach (var interval in new[] { 20, 60, 0 })
                {
                    var burst = System.Diagnostics.Stopwatch.StartNew();
                    long slowestAdd = 0;
                    for (var index = 0; index < 40; index++)
                    {
                        var click = System.Diagnostics.Stopwatch.StartNew();
                        vm.AddNoteCommand.Execute(null);
                        slowestAdd = Math.Max(slowestAdd, click.ElapsedMilliseconds);
                        if (interval > 0) Pump(interval);
                    }
                    var settling = System.Diagnostics.Stopwatch.StartNew();
                    string? issue = null;
                    long nextSample = 0;
                    do
                    {
                        Pump(16);
                        try
                        {
                            AssertRapidAddLayout(root);
                            Require(!DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes), "layout gate active");
                            issue = null;
                        }
                        catch (InvalidOperationException error) { issue = error.Message; }
                        if (issue is not null && settling.ElapsedMilliseconds >= nextSample)
                        {
                            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                            var active = (Dictionary<DashboardMotionScope, DashboardMotionEngine>)typeof(DashboardMotionEngine)
                                .GetField("ActiveByScope", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                            var engine = active[DashboardMotionScope.Notes];
                            var pending = (Dictionary<Guid, Action>)typeof(DashboardMotionEngine).GetField("_pendingEnterCompletions", flags)!.GetValue(engine)!;
                            var playing = (HashSet<Guid>)typeof(DashboardMotionEngine).GetField("_playingEnterIds", flags)!.GetValue(engine)!;
                            Console.WriteLine($"  {settling.ElapsedMilliseconds}ms: {issue}; pending={pending.Count}; playing={playing.Count}; scheduled={typeof(DashboardMotionEngine).GetField("_reflowScheduled", flags)!.GetValue(engine)}");
                            if (interval == 0)
                                Require(playing.Count <= MotionPolicy.PresenceItemBudget, "Coalesced spam bypassed the entrance budget.");
                            nextSample += 200;
                        }
                    } while (issue is not null && settling.ElapsedMilliseconds < 1800);
                    var check = $"interval={interval}ms; count={vm.Notes.Count}; burst={burst.ElapsedMilliseconds - settling.ElapsedMilliseconds}ms; slowest command={slowestAdd}ms; settled={settling.ElapsedMilliseconds}ms; issue={issue ?? "none"}";
                    spamChecks.Add(check); Console.WriteLine(check);
                    File.WriteAllLines(Path.Combine(_output, "spam-add-checks.txt"), spamChecks);
                    Require(issue is null, check);
                    Require(settling.ElapsedMilliseconds < 700, "The burst needed a delayed recovery rather than finishing its motion promptly.");
                    viewer.ScrollToBottom(); Pump(450); AssertRapidAddLayout(root);
                    viewer.ScrollToVerticalOffset(300); Pump(450); AssertRapidAddLayout(root);
                    Pump(700);
                }
                return;
            }
            if (captureChoreography)
            {
                ReducedMotionPreferences.Configure(MotionPreference.Off);
                for (var index = 0; index < 12; index++)
                    vm.AddNoteFromDocument(new NoteDocument { Title = $"Existing {index + 1}" });
                Pump(800); viewer.ScrollToVerticalOffset(300); Pump(650);
                ReducedMotionPreferences.Configure(MotionPreference.Full); Pump(700);
                var surface = (FrameworkElement)window.Content;
                var poses = new List<string>();
                var frames = CaptureFeedback(surface,
                    () => vm.AddNoteFromDocument(new NoteDocument { Title = "Just added" }), 18, afterFrame: () =>
                    {
                        var card = DashboardMotionCardCatalog.EnumerateLayoutRoots(root, DashboardMotionScope.Notes)
                            .OfType<Scriblucent.Controls.NoteCardControl>()
                            .FirstOrDefault(item => item.DataContext is NoteCardViewModel note && note.Document.Title == "Just added");
                        if (card is null) return;
                        var paint = ((Grid)card.FindName("CardContent")).Children.OfType<SurfacePaint>()
                            .Select(light => $"{light.GetType().Name}:{light.Opacity:F3}/{light.IsVisible}");
                        poses.Add($"{poses.Count}: card alpha={card.Opacity:F3} pose={card.RenderTransform.Value}; light={string.Join(",", paint)}");
                    });
                File.WriteAllLines(Path.Combine(_output, "single-add-poses.txt"), poses);
                SaveGif(frames, "single-add.gif");
                foreach (var frame in new[] { 0, 2, 4, 7, 12, 17 })
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frames[frame]));
                    using var file = File.Create(Path.Combine(_output, $"single-add-{frame}.png"));
                    encoder.Save(file);
                }
                Pump(900); AssertRapidAddLayout(root);
                Require(!DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes), "Single add left its layout gate active.");
                var creationMenu = ControlTree(page).OfType<Scriblucent.Controls.DashboardCreateMenu>().Single();
                creationMenu.IsOpen = true; Pump(300);
                var floatingAdd = creationMenu.QuickAddButton;
                var added = 0;
                var burstTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
                    { Interval = TimeSpan.FromMilliseconds(70) };
                burstTimer.Tick += (_, _) =>
                {
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(floatingAdd);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                    if (++added == 12) burstTimer.Stop();
                };
                try { SaveGif(CaptureFeedback(surface, burstTimer.Start, 50), "continuous-add.gif"); }
                finally { burstTimer.Stop(); }
                Require(added == 12, "The recorded burst did not complete.");
                Pump(900); AssertRapidAddLayout(root);
                return;
            }
            var checks = new List<string>();
            foreach (var (preference, interval) in new[] {
                (MotionPreference.Full, 35), (MotionPreference.Full, 90),
                (MotionPreference.Full, 160), (MotionPreference.Full, 350),
                (MotionPreference.Reduced, 35), (MotionPreference.Off, 35) })
            {
                ReducedMotionPreferences.Configure(preference);
                var name = $"rapid-add-{preference}-{interval}";
                for (int i = 0; i < 12; i++)
                {
                    vm.AddNoteCommand.Execute(null); Pump(interval);
                    if (i == 11)
                        SaveLocalControlCapture((FrameworkElement)window.Content, name + "-in-flight");
                }
                Pump(1300); window.UpdateLayout();
                AssertRapidAddLayout(root);
                Require(!DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes),
                    "Rapid adds left the dashboard's layout/virtualization gate active.");
                viewer.ScrollToBottom(); Pump(450); window.UpdateLayout();
                AssertRapidAddLayout(root);
                SaveLocalControlCapture((FrameworkElement)window.Content, name + "-scrolled");
                viewer.ScrollToTop(); Pump(450); window.UpdateLayout();
                AssertRapidAddLayout(root);
                var check = $"PASS {name}: {vm.Notes.Count} notes; no overlapping cards or residual transforms; cards interactive; gate released; scroll down/up stable.";
                checks.Add(check); Console.WriteLine(check);
            }
            File.WriteAllLines(Path.Combine(_output, "rapid-add-checks.txt"), checks);
        }
        finally { window.Close(); }
    }

    private static void AssertRapidAddLayout(FrameworkElement root)
    {
        var cards = DashboardMotionCardCatalog.EnumerateLayoutRoots(root, DashboardMotionScope.Notes).ToArray();
        Require(cards.Length > 0, "No note cards were realized.");
        var bounds = new List<Rect>();
        foreach (var card in cards)
        {
            Require(card.IsHitTestVisible && card.Opacity > .99 && !DashboardMotionEngine.IsPendingEnter(card),
                "A card was left in an entrance hold or was not interactive.");
            var matrix = card.RenderTransform.Value;
            Require(Math.Abs(matrix.OffsetX) < .01 && Math.Abs(matrix.OffsetY) < .01
                && Math.Abs(matrix.M11 - 1) < .01 && Math.Abs(matrix.M22 - 1) < .01,
                "Rapid adds left a card displaced or scaled.");
            var ancestor = VisualTreeHelper.GetParent(card);
            while (ancestor is not null && !ReferenceEquals(ancestor, root))
            {
                if (ancestor is FrameworkElement element)
                {
                    Require(element.RenderTransform.Value.IsIdentity, "Section reveal left an outer card container displaced.");
                    Require(element.Opacity > .99, "A card container was left in a row-handoff fade.");
                }
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }
            var rect = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
            Require(bounds.All(other => { var overlap = Rect.Intersect(other, rect);
                return overlap.IsEmpty || overlap.Width < .01 || overlap.Height < .01; }), "Cards overlapped after rapid add/scroll.");
            bounds.Add(rect);
        }
    }
}
