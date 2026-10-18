using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Views;

internal static partial class Program
{
    private static TextBlock CoherenceLabel(string text, double size = 17)
    {
        var label = new TextBlock { Text = text, FontSize = size, Margin = new(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); return label;
    }
    private static Border CoherenceCard(string title)
    {
        var body = new StackPanel(); body.Children.Add(CoherenceLabel(title, 20)); body.Children.Add(CoherenceLabel("A surface that responds\nto what you do.", 14));
        var face = new Border { CornerRadius = new(14), BorderThickness = new(1), Padding = new(18), Child = new Grid { Children = { body } } };
        face.SetResourceReference(Border.BackgroundProperty, "CardBackground"); face.SetResourceReference(Border.BorderBrushProperty, "NoteCardBorder");
        RoundedClipBehavior.SetIsEnabled(face, true); return face;
    }
    private static FrameworkElement CoherenceSecondary(string path)
    {
        var source = XDocument.Load(path); XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var clone = new XElement(wpf + "Grid", new XElement(wpf + "Grid.Resources", source.Root!.Element(wpf + "Window.Resources")!.Elements().Select(e => new XElement(e))),
            source.Root!.Elements().Where(e => e.Name.LocalName != "Window.Resources").Select(e => new XElement(e)));
        foreach (var notice in clone.Descendants().Where(e => e.Name.LocalName == "AiReviewNotice").ToArray())
            notice.ReplaceWith(new XElement(wpf + "Border", new XAttribute("Height", "64")));
        StripHandlers(clone); return Fragment(clone, source.Root!);
    }
    private static List<System.Windows.Media.Imaging.BitmapSource> CaptureCoherenceSidebar(SidebarFixture fixture, string suffix)
    {
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>(); var poses = new List<object>();
        // Readback can exceed the entire transition on this machine. Seek the real width driver
        // for the recording, and separately trace uninterrupted playback without readbacks.
        fixture.Resize(232); var seek = CoherenceSidebarClock(fixture.Sidebar); var started = 0;
        for (int frame = 0; frame < 56; frame++)
        {
            if (frame == 19 || frame == 24 || frame == 45)
            {
                fixture.Resize(frame == 24 ? 232 : 64); seek = CoherenceSidebarClock(fixture.Sidebar);
                started = frame;
            }
            Pump(16); // Let the independently staged native label fades advance.
            seek(Math.Min(259, (frame - started) * 33));
            fixture.Root.UpdateLayout();
            poses.Add(new { Frame = frame, Column = fixture.Column.Width.Value, Edge = SidebarResizeMotion.VisibleWidth(fixture.Sidebar, 0),
                RootWidth = fixture.Root.ActualWidth, RootHeight = fixture.Root.ActualHeight });
            frames.Add(Snapshot(fixture.Root, .65));
        }
        File.WriteAllText(Path.Combine(_output, $"sidebar-poses-{suffix}.json"), JsonSerializer.Serialize(poses, new JsonSerializerOptions { WriteIndented = true }));
        return frames;
    }
    private static void TraceCoherenceSidebar(SidebarFixture fixture, string suffix)
    {
        fixture.Resize(64); Pump(300); var poses = new List<object>();
        for (int gesture = 0; gesture < 3; gesture++)
        {
            fixture.Resize(gesture == 1 ? 64 : 232); var timer = Stopwatch.StartNew();
            for (int sample = 0; sample < 19; sample++)
            {
                if (gesture == 2 && sample == 6) fixture.Resize(64);
                Pump(16); fixture.Root.UpdateLayout();
                var width = fixture.Column.Width.Value; var edge = SidebarResizeMotion.VisibleWidth(fixture.Sidebar, 0);
                if (Math.Abs(width - edge) > .001) throw new InvalidOperationException("Sidebar edge detached from the viewport.");
                poses.Add(new { Gesture = gesture, Sample = sample, ElapsedMs = timer.Elapsed.TotalMilliseconds, Column = width, Edge = edge,
                    RootWidth = fixture.Root.ActualWidth, RootHeight = fixture.Root.ActualHeight });
            }
            Pump(300);
        }
        File.WriteAllText(Path.Combine(_output, $"sidebar-playback-{suffix}.json"), JsonSerializer.Serialize(poses, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static Action<int> CoherenceSidebarClock(Border sidebar)
    {
        var table = typeof(SidebarResizeMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [sidebar, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var state = args[1]!;
        ((Stopwatch)state.GetType().GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!).Stop();
        ((System.Windows.Threading.DispatcherTimer)state.GetType().GetField("_frameWake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!).Stop();
        var advance = state.GetType().GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ms => advance.Invoke(state, [(double)ms]);
    }
    private static void MeasureCoherenceSidebar(SidebarFixture fixture, string theme)
    {
        var times = new List<double>(); var allocations = new List<long>(); var peak = 0;
        for (int cycle = 0; cycle < 14; cycle++)
        {
            fixture.Resize(cycle % 2 == 0 ? 232 : 64); var seek = CoherenceSidebarClock(fixture.Sidebar);
            for (int ms = 0; ms <= 256; ms += 16)
            {
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                seek(ms); fixture.Root.UpdateLayout(); timer.Stop();
                if (cycle >= 2) { times.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before); }
                peak = Math.Max(peak, Descendants(fixture.Cards!).OfType<ContentPresenter>().Count());
            }
        }
        MotionPolicy.Configure(MotionPreference.Off, false); fixture.Root.UpdateLayout(); Pump(40);
        times.Sort(); allocations.Sort();
        var result = new { Theme = theme, Items = 20_000, Samples = times.Count, MedianMs = times[times.Count / 2],
            P95Ms = times[(int)(times.Count * .95)], MaxMs = times[^1], MedianAllocatedBytes = allocations[allocations.Count / 2], PeakRealizedCards = peak,
            Scope = "UI-thread seek and layout update; two warm-up cycles excluded. Includes wrap changes. Not presented-frame timing or bitmap readback." };
        File.WriteAllText(Path.Combine(_output, $"sidebar-layout-{theme.ToLowerInvariant()}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result)); MotionPolicy.Configure(MotionPreference.Automatic, false);
    }
    private static void RecordCoherenceBlue(Window owner, string suffix)
    {
        var fields = new StackPanel { Width = 260, Margin = new(18), VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(CoherenceLabel("Focus", 21));
        var title = new TextBox { Text = "An idea worth keeping", Height = 48, Margin = new(0, 0, 0, 14) };
        var second = new TextBox { Text = "A place to explore", Height = 48 };
        fields.Children.Add(title); fields.Children.Add(second);
        var selected = CoherenceCard("Selection"); var target = CoherenceCard("Drop target");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(fields); row.Children.Add(new ElevationHost { Width = 244, Height = 164, Margin = new(18), Children = { selected } });
        row.Children.Add(new ElevationHost { Width = 244, Height = 164, Margin = new(18), Children = { target } });
        var root = new Grid { Children = { row } }; root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        owner.Content = root; owner.UpdateLayout(); Pump(50);
        var firstRing = (AccentRim)title.Template.FindName("FocusRing", title); var secondRing = (AccentRim)second.Template.FindName("FocusRing", second);
        var drop = new DropTargetSurface { Surface = target, Opacity = 0, IsHitTestVisible = false }; ((Grid)target.Child).Children.Add(drop);
        owner.UpdateLayout(); Pump(30);
        SaveGif(CaptureFeedback(root, () => { firstRing.IsActive = true; SelectionRimBehavior.SetIsSelected(selected, true); DashboardDropGlowVisual.AnimateToActive(drop); }, 38,
            new() { [14] = () => { firstRing.IsActive = false; secondRing.IsActive = true; DashboardDropGlowVisual.Reset(drop); },
                [18] = () => DashboardDropGlowVisual.AnimateToActive(drop), [29] = () => { secondRing.IsActive = false; DashboardDropGlowVisual.Reset(drop); } }), $"blue-focus-selection-drop-{suffix}.gif");
    }
    private static void RecordCoherenceButtons(Window owner, string suffix)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new[] { "DialogSecondaryButtonStyle", "DialogPrimaryButtonStyle", "DialogDangerButtonStyle", "EditorColorSwatchButtonStyle" }
            .Select((key, i) => new Button { Content = i == 3 ? null : new[] { "Keep editing", "Save changes", "Delete" }[i],
                Style = (Style)_app.FindResource(key), MinWidth = i == 3 ? 38 : 150, Margin = new(12), Background = i == 3 ? Brushes.Orange : null }).ToArray();
        // Preserve each production action's themed fill; the swatch alone supplies its color.
        foreach (var button in buttons.Take(3)) button.ClearValue(Control.BackgroundProperty);
        foreach (var button in buttons) row.Children.Add(button);
        var root = new Grid { Children = { row } }; root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); owner.Content = root; owner.UpdateLayout(); Pump(50);
        var faces = buttons.Select(button => Descendants(button).OfType<InteractionSurface>().First()).ToArray();
        void State(bool hover, bool press = false) { foreach (var face in faces) face.AnimateState(hover, press); }
        SaveGif(CaptureFeedback(root, () => State(true), 38, new() { [12] = () => State(true, true), [17] = () => State(true), [27] = () => State(false) }), $"button-corners-hover-press-{suffix}.gif");
    }
    private static void RecordCoherenceRows(Window owner, string suffix)
    {
        FrameworkElement Row(string key, object model)
        {
            var source = XDocument.Load("Scriblucent/Controls/CombineWorkspaceView.xaml"); XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var tile = new XElement(source.Descendants().First(e => e.Name.LocalName == "DashboardTileHost")); tile.SetAttributeValue("RendererKey", key); StripHandlers(tile);
            var root = Fragment(new XElement(wpf + "Grid", new XAttribute("Width", "410"), new XAttribute("Margin", "15"),
                tile), source.Root!);
            root.DataContext = model; return root;
        }
        var quiz = new QuizLibraryItemViewModel(new Scriblucent.ViewModels.QuizViewModel(new QuizDocument { Title = "A living library" }));
        var combine = new FlashcardCombineWindow.FlashcardCombineItem(new Scriblucent.ViewModels.FlashcardSetViewModel(new FlashcardSetDocument { Title = "Ideas to keep" }));
        var left = Row("QuizLibraryRow", quiz); var right = Row("CombineRow", combine);
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { left, right } };
        var root = new Grid { Children = { content } }; root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); owner.Content = root; owner.UpdateLayout(); Pump(50);
        SaveGif(CaptureFeedback(root, () => { quiz.IsSelected = true; combine.IsSelected = true; }, 30,
            new() { [18] = () => { quiz.IsSelected = false; combine.IsSelected = false; } }), $"secondary-collection-row-feedback-{suffix}.gif");
    }
    private static void RecordCoherence(bool sidebarOnly = false)
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadResources(theme); MotionPolicy.Configure(MotionPreference.Automatic, false); var suffix = theme.ToLowerInvariant();
            var owner = new Window { Width = 960, Height = 720, ShowInTaskbar = false, ShowActivated = false, Content = new Grid() }; owner.Show(); Pump(60);
            using (var sidebar = new SidebarFixture(owner, wrapCards: true))
            {
                TraceCoherenceSidebar(sidebar, suffix);
                SaveGif(CaptureCoherenceSidebar(sidebar, suffix), $"sidebar-continuous-push-{suffix}.gif");
                sidebar.Resize(64); Pump(300); MeasureCoherenceSidebar(sidebar, theme);
            }
            if (sidebarOnly) { owner.Close(); Pump(60); WaitForRest(); continue; }
            RecordCoherenceBlue(owner, suffix); RecordCoherenceButtons(owner, suffix); RecordCoherenceRows(owner, suffix);
            var stats = CoherenceSecondary("Scriblucent/Views/FlashcardStatisticsWindow.xaml");
            var rows = new[] { new { Label = "New", Count = 24, ChipKind = "New" }, new { Label = "Learning", Count = 18, ChipKind = "Learning" }, new { Label = "Mature", Count = 42, ChipKind = "Mature" } };
            stats.DataContext = new { HeaderSubtitleText = "A steady rhythm of learning", DueTodayCountText = "12", TotalCountText = "84", NewCountText = "24", LearningCountText = "18", MatureCountText = "42",
                MaturityRows = rows, ForecastRows = rows, IntervalRows = rows, DifficultySignalRows = rows, DifficultySignalTitleText = "Learning rhythm" };
            owner.Content = stats; owner.UpdateLayout(); Pump(60);
            foreach (var fill in Descendants(stats).OfType<Border>().Where(b => b.Name == "StatBarFill")) fill.Width = 150;
            SaveGif(CaptureFeedback(stats, () => ArrivalGroups.Start(stats, true), 30), $"secondary-statistics-elevation-{suffix}.gif");
            var quiz = CoherenceSecondary("Scriblucent/Views/QuizPreviewWindow.xaml"); owner.Content = quiz; owner.UpdateLayout(); Pump(50);
            foreach (var titleField in Descendants(quiz).OfType<TextBox>().Where(t => t.Name == "TitleTextBox")) titleField.Text = "A living library";
            var questions = Descendants(quiz).OfType<ItemsControl>().First(c => c.Name == "QuestionsItemsControl");
            var question = new QuizPreviewWindow.QuizPreviewQuestion(1, new QuizQuestion { Question = "Which idea should we explore next?", Options = [new QuizOption { Text = "A living library", IsCorrect = true }, new QuizOption { Text = "A clearer learning rhythm" }] });
            questions.ItemsSource = new[] { question, new QuizPreviewWindow.QuizPreviewQuestion(2, new QuizQuestion { Question = "What makes a useful daily tool?", Options = [new QuizOption { Text = "Clear, responsive feedback", IsCorrect = true }, new QuizOption { Text = "Room for expression" }] }) };
            owner.UpdateLayout(); Pump(60);
            SaveGif(CaptureFeedback(quiz, () => { question.IsFocused = true; ArrivalGroups.Start(quiz, true); }, 30), $"secondary-quiz-card-elevation-{suffix}.gif");
            var dialog = new OverlayDialogShell { Header = CoherenceLabel("Keep this idea?", 24), Body = new TextBox { Text = "A little more alive", Height = 46, Margin = new(0, 4, 0, 22) },
                Footer = new Button { Content = "Save idea", Style = (Style)_app.FindResource("DialogPrimaryButtonStyle"), HorizontalAlignment = HorizontalAlignment.Right } };
            var dialogRoot = new Grid(); dialogRoot.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); owner.Content = dialogRoot; owner.UpdateLayout(); Pump(30);
            SaveGif(CaptureFeedback(dialogRoot, () => { dialogRoot.Children.Add(dialog); owner.UpdateLayout(); dialog.BindAnimator(owner).BeginOpen(); }, 35), $"floating-dialog-elevation-{suffix}.gif");
            var source = XDocument.Load("Scriblucent/Views/FlashcardImageViewerWindow.xaml");
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var imageClone = new XElement(source.Root!.Element(wpf + "Grid")!); StripHandlers(imageClone);
            var imageRoot = Fragment(imageClone, source.Root!); owner.Content = imageRoot; owner.UpdateLayout(); Pump(30);
            var image = Descendants(imageRoot).OfType<Image>().First(i => i.Name == "ViewerImage"); image.Source = PhotoFixture(); image.Opacity = 1;
            var shadow = Descendants(imageRoot).OfType<BakedElevationLayer>().First();
            owner.UpdateLayout(); var elevation = BakedElevationAsset.Dialog;
            var imageBounds = (Rect)typeof(FlashcardImageViewerWindow).GetMethod("ImageBounds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [image, imageRoot])!;
            shadow.Width = imageBounds.Width + elevation.BleedTop * 2; shadow.Height = imageBounds.Height + elevation.BleedTop + elevation.BleedBottom; shadow.Opacity = .30;
            SaveGif(CaptureFeedback(imageRoot, () => UiMotion.Fade(shadow, .30, 160), 12), $"floating-photo-elevation-{suffix}.gif");
            MotionPolicy.Configure(MotionPreference.Reduced, false); RecordCoherenceBlue(owner, suffix + "-reduced"); RecordCoherenceButtons(owner, suffix + "-reduced");
            owner.Close(); Pump(60); WaitForRest();
        }
        File.WriteAllText(Path.Combine(_output, "capture-scope.txt"), "Isolated plain WPF Application with synthetic content. Production sidebar, 20,000-item virtualized wrap grid, field/button templates, statistics and quiz secondary-window XAML, dialog and photo surfaces. Focus/hover/press states are exercised directly, with no desktop input injection. No Scriblucent.App startup or personal settings/documents. Sidebar GIFs seek a paused native width clock because offscreen readback delays distort short transitions; sidebar-playback JSON separately traces natural clock playback without readback. Other clips use timed offscreen readbacks. Layout timings are not presented-frame FPS.");
    }
}
