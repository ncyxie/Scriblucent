using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using System.Windows.Markup;
using Scriblucent.Controls.Dashboard;
using Scriblucent.Icons;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.IO;
using System.Windows.Input;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Views;

internal static partial class Program
{
    private static readonly List<object> Results = new();
    private static string _output = "";
    private static Application _app = null!;
    private static bool _wake16;
    private static bool _pointerControls;
    private static string? _rapidAddLibrarySource;
    private static readonly List<OverlayBackdropCapture.Measurement> MaterialMeasurements = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            _output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/motion");
            _wake16 = args.Contains("--wake-16");
            _pointerControls = args.Contains("--pointer-controls");
            _rapidAddLibrarySource = args.FirstOrDefault(a => a.StartsWith("--library-source="))?["--library-source=".Length..];
            Directory.CreateDirectory(_output);
            if (args.Contains("--software")) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            OverlayBackdropCapture.Measured += value => MaterialMeasurements.Add(value);
            LoadResources("Dark");
            Scriblucent.Rendering.SkiaBackdropBlur.BeginWarmup();
            ReducedMotionPreferences.Configure(MotionPreference.Full);
            _app.Dispatcher.BeginInvoke(() =>
            {
                var exitCode = 0;
                try
                {
                    if (args.Contains("--dashboard-toolbar-motion")) ProbeDashboardToolbarMotion(args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--dashboard-toolbar")) ProbeDashboardToolbar(args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--ordering-settings")) ProbeSettingsOrderingOnly(args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--manual-order")) ProbeManualOrder(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..], args.Contains("--editors-only"));
                    else if (args.Contains("--combine-workspace")) ProbeCombineWorkspace(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--update-dialog-shadow")) ProbeUpdateDialogShadow();
                    else if (args.Contains("--update-dialog-scroll")) ProbeUpdateDialogScroll();
                    else if (args.Contains("--wrapped-note-save")) ProbeWrappedNoteSave();
                    else if (args.Contains("--closed-dropdowns")) ProbeClosedDropdowns();
                    else if (args.Contains("--field-spacing")) ProbeFieldSpacing();
                    else if (args.Contains("--rename-default")) ProbeRenameDefault();
                    else if (args.Contains("--selection-consistency")) ProbeSelectionConsistency(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--selection-completion")) ProbeSelectionCompletion(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--dashboard-selection")) ProbeDashboardSelection(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--editor-selection")) ProbeEditorSelection(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--sidebar-load")) ProbeSidebarLoad();
                    else if (args.Contains("--large-dashboard")) ProbeLargeDashboard();
                    else if (args.Contains("--notes-content-profile")) ProbeNotesContent();
                    else if (args.Contains("--about-layout")) ProbeAboutLayout();
                    else if (args.Contains("--calendar-week-start")) ProbeCalendarWeekStart();
                    else if (args.Contains("--dashboard-chrome-regressions")) ProbeDashboardChromeRegressions();
                    else if (args.Contains("--dashboard-loading-geometry")) ProbeLoadingGeometry(args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--dashboard-loading")) ProbeDashboardLoading(args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--creation-menu-paint")) ProbeCreationMenuPaint();
                    else if (args.Contains("--creation-menu")) ProbeGroupStartup(checkCreationMenus: true, theme: args.Contains("--dark") ? "Dark" : "Light");
                    else if ((args.Contains("--dashboard-ordering") || args.Contains("--dashboard-arrows"))) ProbeGroupStartup(checkSectionOrdering: true, theme: args.Contains("--dark") ? "Dark" : "Light");
                    else if (args.Contains("--group-startup")) ProbeGroupStartup();
                    else if (args.Contains("--recent-tint")) ProbeRecentTint();
                    else if (args.Contains("--smooth-caret")) ProbeSmoothCaret();
                    else if (args.Contains("--dashboard-calendar")) ProbeDashboardCalendar();
                    else if (args.Contains("--save-status")) ProbeEditorSaveStatus();
                    else if (args.Contains("--caption-layout")) ProbeCaptionLayout();
                    else if (args.Contains("--ui-finishing")) ProbeUiFinishing(args.Contains("--quick"));
                    else if (args.Contains("--dashboard-groups")) ProbeDashboardGroups(args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..]);
                    else if (args.Contains("--group-arrival-shadow")) ProbeGroupArrivalShadows();
                    else if (args.Contains("--popup-bounds")) ProbePopupBounds();
                    else if (args.Contains("--ui-fixes")) ProbeUiFixes();
                    else if (args.Contains("--ui-unification")) ProbeUiUnification(args.Contains("--quick"));
                    else if (args.Contains("--flashcard-hitch")) ProbeFlashcardWheel(hitch: true);
                    else if (args.Contains("--flashcard-wheel")) ProbeFlashcardWheel();
                    else if (args.Contains("--flashcard-frames")) ProbeFlashcardFrames();
                    else if (args.Contains("--flashcard-scroll")) ProbeFlashcardScroll(args.Contains("--quick"));
                    else if (args.Contains("--remaining-ui")) ProbeRemainingUi(args.Contains("--baseline"), args.Contains("--quick"));
                    else if (args.Contains("--input-library-add")) ProbeRapidDashboardAdd(checkInput: true, useLibraryCopy: true);
                    else if (args.Contains("--input-empty-add")) ProbeRapidDashboardAdd(checkInput: true, startEmpty: true);
                    else if (args.Contains("--input-dashboard-add")) ProbeRapidDashboardAdd(checkInput: true);
                    else if (args.Contains("--spam-dashboard-add")) ProbeRapidDashboardAdd(checkSpam: true);
                    else if (args.Contains("--add-choreography")) ProbeRapidDashboardAdd(captureChoreography: true);
                    else if (args.Contains("--rapid-dashboard-add")) ProbeRapidDashboardAdd();
                    else if (args.Contains("--context-chrome")) ProbeContextualChrome();
                    else if (args.Contains("--editor-spacing")) ProbeEditorToolbar(includeTabScroll: false, includeSpacing: true);
                    else if (args.Contains("--linked-details")) ProbeEditorToolbar(includeTabScroll: false);
                    else if (args.Contains("--editor-toolbar")) ProbeEditorToolbar();
                    else if (args.Contains("--editor-workspace")) ProbeEditorWorkspace();
                    else if (args.Contains("--settings-input")) ProbeSettingsInput();
                    else if (args.Contains("--command-places")) ProbeCommandPlaces();
                    else if (args.Contains("--command-light")) ProbeCommandLight();
                    else if (args.Contains("--gradient")) ProbeWorkspaceGradient();
                    else if (args.Contains("--fluent-surfaces")) ProbeFluentSurfaces();
                    else if (args.Contains("--workspaces")) ProbeWorkspaces();
                    else if (args.Contains("--premium-ui")) ProbePremiumUi();
                    else if (args.Contains("--hover-corners")) ProbeButtonHoverEdge(corners: true);
                    else if (args.Contains("--hover-edge")) ProbeButtonHoverEdge();
                    else if (args.Contains("--surface-cards")) ProbeSurfaceCards();
                    else if (args.Contains("--surface-coherence")) ProbeSurfaceCoherence();
                    else if (args.Contains("--surfaces")) ProbeColoredSurfaces();
                    else if (args.Contains("--dictionary-scroll")) ProbeDictionaryScrolling();
                    else if (args.Contains("--controls")) ProbeControlRevamp();
                    else if (args.Contains("--drag-shadow")) ProbeDragShadows();
                    else if (args.Contains("--compiled-shell")) ProbeCompiledShell();
                    else if (args.Contains("--coherence")) RecordCoherence(args.Contains("--sidebar-only"));
                    else if (args.Contains("--repairs")) RecordRepairs();
                    else if (args.Contains("--probe")) exitCode = Probe() ? 0 : 2;
                    else if (args.Contains("--record")) Record();
                    else Benchmark(args.Contains("--software"));
                }
                catch (Exception e) { Console.Error.WriteLine(e); exitCode = 1; }
                finally { _app.Shutdown(exitCode); }
            }, DispatcherPriority.ApplicationIdle);
            return _app.Run();
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void LoadResources(string theme)
    {
        _app.Resources.MergedDictionaries.Clear();
        foreach (var path in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome",
            "Controls/Calendar", "Controls/Buttons", "Controls/Dashboard", "Controls/Quiz" })
            _app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{path}.xaml") });
        _app.Resources["NoteCardContextMenu"] = new ContextMenu();
        _app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        _app.Resources["Loc"] = Scriblucent.Localization.LocalizationProvider.Instance;
        _app.Resources["BrandLogoSvg"] = new Uri($"pack://application:,,,/Scriblucent;component/Assets/brand-logo-{(theme == "Dark" ? "light" : "dark")}.svg");
    }
    private sealed class Scene : IDisposable
    {
        internal Window Window;
        internal Grid Root = new();
        internal EmptyDashboardPanel Empty = new() { VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, IconKind = AppIconKind.Notes,
            Prompt = "Your next idea starts here", ButtonContent = "Create a note" };
        internal AboutPanel About;
        internal Canvas Celebration = new() { IsHitTestVisible = false };
        internal StudyResultEmblem Score;
        internal FrameworkElement Summary;
        internal Grid Navigation;
        internal Button[] NavigationButtons;
        internal Border RevealContent = new() { Height = 520, Background = Brushes.SlateGray, Visibility = Visibility.Collapsed };
        internal NoteCardControl Card;
        internal Border CardStage;
        internal FlashcardFlipHost Flip = new() { Width = 280, Height = 150 };
        internal ListBox List = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        internal Scene()
        {
            Root.Background = (Brush)_app.FindResource("WindowBackground");
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            var aside = new StackPanel { Margin = new Thickness(20) };
            var heading = new TextBlock { Text = "Scriblucent motion audit", FontSize = 22 };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); aside.Children.Add(heading);
            var reveal = new LayoutRevealHost { Child = RevealContent }; aside.Children.Add(reveal);
            Flip.FrontContent = new Border { Background = Brushes.LightSteelBlue, CornerRadius = new CornerRadius(14),
                Child = new TextBlock { Text = "Question", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            Flip.BackContent = new Border { Background = Brushes.PaleTurquoise, CornerRadius = new CornerRadius(14),
                Child = new TextBlock { Text = "Answer", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            aside.Children.Add(Flip);
            Summary = LoadResultsSummary();
            Score = (StudyResultEmblem)Summary.FindName("ResultsEmblem"); Score.Ratio = .9;
            ((TextBlock)Summary.FindName("ResultsTitleText")).Text = "Well done";
            ((TextBlock)Summary.FindName("ResultsScoreText")).Text = "9 / 10 correct";
            ((TextBlock)Summary.FindName("ResultsTimeText")).Text = "Completed in 2:14";
            aside.Children.Add(Summary);
            Navigation = LoadSidebarNavigation();
            NavigationButtons = Navigation.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            aside.Children.Add(Navigation);
            columns.Children.Add(aside);
            var template = new DataTemplate(typeof(string));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, Brushes.SlateGray);
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            factory.SetValue(FrameworkElement.HeightProperty, 80d);
            factory.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(12));
            factory.AppendChild(label);
            template.VisualTree = factory;
            List.ItemTemplate = template;
            VirtualizingPanel.SetIsVirtualizing(List, true);
            VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
            List.ItemsSource = Enumerable.Range(0, 20_000).Select(i => $"Synthetic item {i}").ToArray();
            SmoothScrollBehavior.SetIsEnabled(List, true);
            Grid.SetColumn(List, 1); columns.Children.Add(List);
            Root.Children.Add(columns);
            Empty.Visibility = Visibility.Collapsed;
            var emptyStage = new Border { Background = (Brush)_app.FindResource("WindowBackground"), Child = Empty };
            System.Windows.Data.BindingOperations.SetBinding(emptyStage, UIElement.VisibilityProperty,
                new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = Empty });
            Root.Children.Remove(Empty); Root.Children.Add(emptyStage);
            Card = new NoteCardControl { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                DataContext = new NoteCardViewModel(new NoteDocument { Title = "A little more alive", Content = "Good motion makes an action feel connected.\nIdeas, notes, and a little momentum.", Tags = ["Design", "Scriblucent"] }, _ => { }) };
            CardStage = new Border { Background = (Brush)_app.FindResource("WindowBackground"), Child = Card, Visibility = Visibility.Collapsed };
            Root.Children.Add(CardStage);
            Root.Children.Add(Celebration);
            About = new AboutPanel { Visibility = Visibility.Collapsed };
            Panel.SetZIndex(About, 250); Root.Children.Add(About);
            Window = new Window { Title = "Scriblucent isolated motion audit", Width = 1100, Height = 780,
                Content = new AdornerDecorator { Child = Root }, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Window.Show(); Window.Activate(); Pump(250); Window.UpdateLayout();
            SidebarNavMotionBehavior.SetIsActive(NavigationButtons[0], true);
            if (Find<ScrollViewer>(List) is { } viewer) SmoothScrollBehavior.SetIsEnabled(viewer, true);
        }
        public void Dispose() { StudyCelebration.Stop(Celebration); Window.Close(); Pump(20); }
    }
    private static FrameworkElement Fragment(XElement element, XElement sourceRoot)
    {
        var clone = new XElement(element);
        foreach (var node in clone.DescendantsAndSelf())
        {
            var ns = node.Name.NamespaceName;
            if (ns.StartsWith("clr-namespace:") && !ns.Contains("assembly=")) node.Name = XName.Get(node.Name.LocalName, ns + ";assembly=Scriblucent");
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            {
                ns = attribute.Name.NamespaceName;
                if (!ns.StartsWith("clr-namespace:") || ns.Contains("assembly=")) continue;
                attribute.Remove(); node.SetAttributeValue(XName.Get(attribute.Name.LocalName, ns + ";assembly=Scriblucent"), attribute.Value);
            }
        }
        foreach (var attribute in sourceRoot.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var value = attribute.Value;
            if (value.StartsWith("clr-namespace:") && !value.Contains("assembly=")) value += ";assembly=Scriblucent";
            clone.SetAttributeValue(attribute.Name, value);
        }
        return (FrameworkElement)XamlReader.Parse(clone.ToString());
    }
    private static FrameworkElement LoadResultsSummary()
    {
        var source = XDocument.Load("Scriblucent/Views/QuizModeWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var title = source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ResultsTitleText");
        return Fragment(title.Parent!.Parent!, source.Root!);
    }
    private static Grid LoadSidebarNavigation()
    {
        var source = XDocument.Load("Scriblucent/MainWindow.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var group = new XElement(source.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "SidebarNavMotionBehavior.IsGroupHost")));
        var keys = new[] { "SidebarNavIconStyle", "SidebarActionButtonStyle", "SidebarLabelTextStyle",
            "SidebarNotesDashboardButtonStyle", "SidebarFlashcardsDashboardButtonStyle", "SidebarMindMapsDashboardButtonStyle", "SidebarQuizzesDashboardButtonStyle" };
        var dictionary = new XElement(wpf + "ResourceDictionary");
        foreach (var key in keys)
            if (source.Descendants().FirstOrDefault(e => (string?)e.Attribute(x + "Key") == key) is { } style)
                dictionary.Add(new XElement(style));
        group.AddFirst(new XElement(wpf + "Grid.Resources", dictionary));
        return (Grid)Fragment(group, source.Root!);
    }

    private static void Benchmark(bool software)
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            LoadResources(theme);
            using var s = new Scene();
            foreach (var preference in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
            {
                ReducedMotionPreferences.Configure(preference); Pump(120);
                Measure($"{theme}/{preference}/about-open", s, () => s.About.ShowAnimated(), 420);
                Measure($"{theme}/{preference}/about-close", s, () => s.About.HideAnimated(), 220);
                Measure($"{theme}/{preference}/empty-showcase", s, () =>
                {
                    s.Empty.Visibility = Visibility.Visible; UiMotion.AnimateEmptyDashboardEnter(s.Empty);
                }, 360);
                s.Empty.Visibility = Visibility.Collapsed;
                Measure($"{theme}/{preference}/layout-open", s, () => UiMotion.AnimateLayoutReveal(s.RevealContent, true), 360);
                Measure($"{theme}/{preference}/layout-close", s, () => UiMotion.AnimateLayoutReveal(s.RevealContent, false), 360);
                Measure($"{theme}/{preference}/flip", s, () => s.Flip.AnimateToggle(400), 480);
                Measure($"{theme}/{preference}/celebration", s, () =>
                {
                    s.Score.Play(); StudyCelebration.PlayConfetti(s.Celebration, origin: s.Score);
                }, 1320);
                Measure($"{theme}/{preference}/card-hover", s, () =>
                {
                    s.CardStage.Visibility = Visibility.Visible;
                    s.Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                }, 320);
                s.Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                Pump(250); s.CardStage.Visibility = Visibility.Collapsed;
                Measure($"{theme}/{preference}/sidebar-select", s, () =>
                {
                    var selected = preference == MotionPreference.Reduced ? 0 : 3;
                    foreach (var button in s.NavigationButtons) SidebarNavMotionBehavior.SetIsActive(button, false);
                    SidebarNavMotionBehavior.SetIsActive(s.NavigationButtons[selected], true);
                }, 350);
                Measure($"{theme}/{preference}/large-library-scroll", s, () =>
                {
                    var viewer = Find<ScrollViewer>(s.List);
                    if (viewer is not null) viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -480)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
                }, 600);
                BenchmarkThirdPass(s, theme, preference);
                BenchmarkFourthPass(s, theme, preference);
                BenchmarkFifthPass(s, theme, preference);
            }
            s.Window.WindowState = WindowState.Maximized; Pump(220);
            ReducedMotionPreferences.Configure(MotionPreference.Full);
            Measure($"{theme}/Full/maximized-about", s, () => s.About.ShowAnimated(), 450);
            s.About.HideAnimated(); Pump(200);
            RasterProbe(s, theme);
        }
        File.WriteAllText(Path.Combine(_output, software ? "software-benchmark.json" : "benchmark.json"),
            JsonSerializer.Serialize(new
            {
                runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
                processors = Environment.ProcessorCount, renderTier = RenderCapability.Tier >> 16,
                renderMode = RenderOptions.ProcessRenderMode.ToString(),
                dispatcherWakeMs = _wake16 ? 16 : (int?)null,
                note = "Callback timings, not ETW/presented GPU frame timings. Raster probes are offscreen DPI simulations.",
                results = Results
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Measure(string name, Scene scene, Action start, int duration)
    {
        WaitForRest();
        scene.Window.Activate();
        Pump(50);
        MotionDiagnostics.Start();
        MaterialMeasurements.Clear();
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew(); start(); timer.Stop();
        var preparation = timer.Elapsed.TotalMilliseconds;
        var preparationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
        var initialQuality = MotionPolicy.Quality.ToString();
        Pump(duration);
        WaitForRest();
        var samples = MotionDiagnostics.Stop();
        var gaps = samples.Select(x => x.CallbackGapMs).Where(x => x > 0).ToArray();
        var managed = samples.Select(x => x.ManagedMs).ToArray();
        Results.Add(new
        {
            name, initialQuality, finalQuality = MotionPolicy.Quality.ToString(),
            dpi = VisualTreeHelper.GetDpi(scene.Window).DpiScaleX, preparationMs = preparation,
            preparationAllocation, callbackSamples = samples.Count,
            callbackGapP95Ms = Percentile(gaps, .95), callbackGapMaxMs = gaps.DefaultIfEmpty().Max(),
            managedP95Ms = Percentile(managed, .95), managedMaxMs = managed.DefaultIfEmpty().Max(),
            callbackAllocation = samples.Sum(x => x.AllocatedBytes),
            maxCachePixels = samples.Select(x => x.CachePixels).DefaultIfEmpty().Max(),
            remainingCachePixels = MotionCacheBudget.ReservedPixels, anyMotionRemaining = RenderTransitionClock.IsAnyRunning,
            retainedBackdropPixels = OverlayBackdropCapture.RetainedPixels,
            remainingMotionCachePixels = MotionCacheBudget.ReservedPixels - OverlayBackdropCapture.RetainedPixels,
            materialMeasurements = MaterialMeasurements.ToArray(),
            windowActive = scene.Window.IsActive, windowVisible = scene.Window.IsVisible
        });
        Console.WriteLine($"{name}: prep {preparation:F2} ms, managed p95 {Percentile(managed, .95):F3} ms, callback gap p95 {Percentile(gaps, .95):F2} ms, {initialQuality}");
    }
    private static void WaitForRest()
    {
        var limit = Stopwatch.StartNew();
        while ((RenderTransitionClock.IsAnyRunning || OverlayBackdropCapture.IsProcessing) && limit.ElapsedMilliseconds < 5000) Pump(25);
    }
    private static double Percentile(double[] values, double fraction)
    { if (values.Length == 0) return 0; Array.Sort(values); return values[(int)Math.Min(values.Length - 1, Math.Ceiling(values.Length * fraction) - 1)]; }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private static void RasterProbe(Scene scene, string theme)
    {
        foreach (var scale in new[] { 1d, 1.5, 2d })
        {
            var bitmap = Snapshot(scene.Root, scale);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_output, $"raster-{theme.ToLowerInvariant()}-{scale.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}.png")); encoder.Save(file);
        }
    }
    private static bool Probe()
    {
        var results = new List<object>();
        var available = true;
        LoadResources("Dark"); using var scene = new Scene();
        ReducedMotionPreferences.Configure(MotionPreference.Full);
        scene.About.ShowAnimated(); Pump(800); scene.About.HideAnimated(); Pump(250);
        void MeasureVisible(string name, FrameworkElement region, Action start, int duration)
        {
            using var probe = new OnscreenMotionProbe(region); start(); Pump(duration);
            results.Add(probe.Finish(name)); available &= probe.Available; WaitForRest();
        }
        MeasureVisible("About brand stage", (FrameworkElement)scene.About.FindName("BrandStage"), () => scene.About.ShowAnimated(), 800);
        scene.About.HideAnimated(); Pump(250);
        MeasureVisible("Flashcard flip", scene.Flip, () => scene.Flip.AnimateToggle(400), 600);
        MeasureVisible("Score and arc", scene.Score, () => scene.Score.Play(), 650);
        scene.Empty.Visibility = Visibility.Visible; Pump(50);
        MeasureVisible("Empty illustration", (FrameworkElement)scene.Empty.FindName("GlyphChip"), () => UiMotion.AnimateEmptyDashboardEnter(scene.Empty), 650);
        File.WriteAllText(Path.Combine(_output, "onscreen-probe.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(results));
        if (!available) Console.Error.WriteLine("UNAVAILABLE: desktop readback failed; this run supplies no visible-frame cadence evidence.");
        return available;
    }

    private static void Record()
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            LoadResources(theme);
            using var scene = new Scene();
            ReducedMotionPreferences.Configure(MotionPreference.Full);
            var frames = Capture(scene.Root, () => scene.About.ShowAnimated(), 650);
            SaveGif(frames, $"about-{theme.ToLowerInvariant()}.gif");
            var logo = (FrameworkElement)scene.About.FindName("BrandLogoHost");
            if (logo.Opacity < .999) throw new InvalidOperationException("About logo did not finish visible.");
            var (_, translate) = UiMotion.EnsureScaleTranslateGroup(logo);
            if (Math.Abs(translate.Y) > .01) throw new InvalidOperationException("About logo did not settle.");
            scene.About.HideAnimated(); Pump(45); scene.About.ShowAnimated(); Pump(750); WaitForRest();
            if (scene.About.Visibility != Visibility.Visible || logo.Opacity < .999) throw new InvalidOperationException("About reversal failed.");
            scene.About.HideAnimated(); Pump(250); WaitForRest();
            if (scene.About.Visibility != Visibility.Collapsed) throw new InvalidOperationException($"About close failed after bounded completion wait. Activity={RenderTransitionClock.IsAnyRunning}, panel opacity={((FrameworkElement)scene.About.FindName("PanelMotionHost")).Opacity}");
            scene.Empty.Visibility = Visibility.Visible;
            foreach (var kind in new[] { AppIconKind.Notes, AppIconKind.Flashcards, AppIconKind.Quizzes, AppIconKind.MindMaps })
            {
                scene.Empty.IconKind = kind;
                scene.Empty.Prompt = $"Your next {kind.ToString().ToLowerInvariant()} starts here";
                scene.Empty.ButtonContent = $"Create {kind.ToString().ToLowerInvariant()}";
                SaveGif(Capture(scene.Root, () => UiMotion.AnimateEmptyDashboardEnter(scene.Empty), 650), $"empty-{kind.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}.gif");
            }
            scene.Empty.Visibility = Visibility.Collapsed;
            // Put the production result header in focus for the recording; benchmark keeps the stress scene.
            var summaryParent = (Panel)scene.Summary.Parent;
            var summaryIndex = summaryParent.Children.IndexOf(scene.Summary);
            summaryParent.Children.Remove(scene.Summary);
            scene.Summary.HorizontalAlignment = HorizontalAlignment.Center;
            scene.Summary.VerticalAlignment = VerticalAlignment.Center;
            var resultStage = new Border { Background = (Brush)_app.FindResource("WindowBackground"), Child = scene.Summary };
            scene.Root.Children.Insert(scene.Root.Children.IndexOf(scene.Celebration), resultStage); Pump(50);
            SaveGif(Capture(scene.Root, () => { scene.Score.Play(); StudyCelebration.RevealScore(scene.Summary.FindName("ResultsTitleText") as FrameworkElement); StudyCelebration.PlayConfetti(scene.Celebration, origin: scene.Score); }, 1400), $"celebration-{theme.ToLowerInvariant()}.gif");
            scene.Root.Children.Remove(resultStage); resultStage.Child = null;
            scene.Summary.HorizontalAlignment = HorizontalAlignment.Stretch; scene.Summary.VerticalAlignment = VerticalAlignment.Stretch;
            summaryParent.Children.Insert(summaryIndex, scene.Summary); Pump(50);
            SaveGif(Capture(scene.Root, () => scene.Flip.AnimateToggle(400), 550), $"flip-{theme.ToLowerInvariant()}.gif");
            SaveGif(Capture(scene.Root, () =>
            {
                SidebarNavMotionBehavior.SetIsActive(scene.NavigationButtons[0], false);
                SidebarNavMotionBehavior.SetIsActive(scene.NavigationButtons[3], true);
            }, 400), $"sidebar-{theme.ToLowerInvariant()}.gif");
            // Dashboard normally supplies the card's dimensions; reproduce those in this isolated visual stage.
            scene.Card.Width = 280; scene.Card.Height = 220;
            scene.CardStage.Visibility = Visibility.Visible; Pump(50);
            SaveGif(Capture(scene.Root, () => scene.Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseEnterEvent }), 400), $"card-hover-{theme.ToLowerInvariant()}.gif");
            scene.Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Pump(250); scene.CardStage.Visibility = Visibility.Collapsed;
            Pump(200);
            if (scene.Celebration.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Celebration did not clean up.");
            RecordThirdPass(scene, theme);
            RecordFourthPass(scene, theme);
            RecordFifthPass(scene, theme);
        }
        File.WriteAllText(Path.Combine(_output, "showcase-checks.txt"), "PASS: About final visibility, rest transform, reversal, close, and celebration cleanup in both themes.\n");
    }
    private static List<BitmapSource> Capture(FrameworkElement root, Action start, int duration)
    {
        var frames = new List<BitmapSource>();
        start();
        for (int time = 0; time < duration; time += 33) { Pump(33); frames.Add(Snapshot(root, .65)); }
        return frames;
    }
    private static BitmapSource Snapshot(FrameworkElement root, double scale)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)(root.ActualWidth * scale)),
            Math.Max(1, (int)(root.ActualHeight * scale)), scale * 96, scale * 96, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze(); return bitmap;
    }
    private static void SaveGif(List<BitmapSource> frames, string name)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var source in frames)
        {
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)3);
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        }
        using var file = File.Create(Path.Combine(_output, name)); encoder.Save(file);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frames[^1]));
        using var still = File.Create(Path.Combine(_output, Path.GetFileNameWithoutExtension(name) + ".png")); png.Save(still);
        var width = frames[0].PixelWidth; var height = frames[0].PixelHeight;
        var strip = new DrawingVisual();
        using (var drawing = strip.RenderOpen())
            for (var i = 0; i < 6; i++)
            {
                // Keep the arrival/hinge poses, then include the final settled frame.
                var index = i == 5 ? frames.Count - 1 : (int)Math.Round(Math.Min(frames.Count - 1, 12) * i / 4d);
                drawing.DrawImage(frames[index], new Rect(i % 3 * width, i / 3 * height, width, height));
            }
        var contact = new RenderTargetBitmap(width * 3, height * 2, 96, 96, PixelFormats.Pbgra32);
        contact.Render(strip); contact.Freeze();
        var sequence = new PngBitmapEncoder(); sequence.Frames.Add(BitmapFrame.Create(contact));
        using var sequenceFile = File.Create(Path.Combine(_output, Path.GetFileNameWithoutExtension(name) + "-sequence.png"));
        sequence.Save(sequenceFile);
        Console.WriteLine($"Recorded {name}: {frames.Count} frames");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_wake16 ? Math.Min(16, milliseconds) : milliseconds) };
        timer.Tick += (_, _) =>
        {
            if (elapsed.ElapsedMilliseconds < milliseconds) return;
            timer.Stop(); frame.Continue = false;
        };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
