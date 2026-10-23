using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Xml.Linq;

internal static partial class Program
{
    private static void ProbeRecentTint() => ProbeProductionRecentTint();

    public sealed class RecentTintFixture : INotifyPropertyChanged
    {
        public ObservableCollection<NoteCardViewModel> RecentNotes { get; } = new();
        public ObservableCollection<MindMapViewModel> RecentMindMaps { get; } = new();
        public ObservableCollection<FlashcardSetViewModel> RecentFlashcardSets { get; } = new();
        public ObservableCollection<QuizViewModel> RecentQuizzes { get; } = new();
        public bool IsMassSelectMode => false;
        private bool _isRecentSectionVisible = true;
        public bool IsRecentSectionVisible
        {
            get => _isRecentSectionVisible;
            set { _isRecentSectionVisible = value; PropertyChanged?.Invoke(this, new(nameof(IsRecentSectionVisible))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Clear()
        {
            RecentNotes.Clear(); RecentMindMaps.Clear(); RecentFlashcardSets.Clear(); RecentQuizzes.Clear();
        }
        internal void Fill(bool firstWide)
        {
            Clear();
            for (var i = 0; i < 8; i++)
            {
                var title = i == 0 && firstWide ? "A first recent item with a considerably longer title" : i == 0 ? "Short" : $"Recent item {i + 1}";
                RecentNotes.Add(new(new NoteDocument { Title = title, LastModified = DateTime.Now }, _ => { }));
                RecentMindMaps.Add(new(new MindMapDocument { Title = title, LastModified = DateTime.Now }));
                RecentFlashcardSets.Add(new(new FlashcardSetDocument { Title = title, LastModified = DateTime.Now }));
                RecentQuizzes.Add(new(new QuizDocument { Title = title, LastModified = DateTime.Now }));
            }
        }
    }
    private static void ProbeProductionRecentTint()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load("Scriblucent/MainWindow.xaml");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var dashboard in new[] { "Notes", "MindMaps", "Flashcards", "Quizzes" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Full, false);
            var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
            var body = source.Descendants().Single(e => ((string?)e.Attribute(x + "Name"))?.EndsWith("RecentSectionBody") == true);
            var wrapper = new XElement(wpf + "Grid", new XAttribute("Margin", "20"),
                new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()),
                new XElement(body.Ancestors().Last(e => e.Name.LocalName == "LayoutRevealHost")));
            StripFixtureEvents(wrapper); var root = Fragment(wrapper, source.Root!);
            var fixture = new RecentTintFixture(); root.DataContext = fixture;
            var scene = new Grid { Background = (Brush)_app.FindResource("WindowBackground"), Children = { root } };
            var window = new Window { Content = scene, Width = 650, Height = 235, ShowActivated = false, ShowInTaskbar = false,
                UseLayoutRounding = true, Background = (Brush)_app.FindResource("WindowBackground") };
            window.Show(); Pump(350);
            try
            {
                var section = ((Grid)root).Children.OfType<LayoutRevealHost>().Single();
                fixture.Clear(); Pump(300); SettleRecentSection(section);
                Require(section.Visibility == Visibility.Collapsed && section.DesiredSize.Height == 0,
                    "Empty Recents retained its heading or layout space.");
                Require(fixture.IsRecentSectionVisible, "Hiding empty Recents changed its preference.");
                SaveRecentTintCapture(scene, $"production-{dashboard}-{theme}-empty");
                fixture.Fill(firstWide: true); Pump(350); SettleRecentSection(section);
                Require(section.IsVisible && section.ActualHeight > 0 && section.IsHitTestVisible,
                    "Recents failed to reveal when items became available.");
                fixture.IsRecentSectionVisible = false; Pump(300); SettleRecentSection(section);
                Require(section.Visibility == Visibility.Collapsed, "Disabled Recents remained visible.");
                fixture.Clear(); fixture.IsRecentSectionVisible = true; Pump(300);
                Require(section.Visibility == Visibility.Collapsed, "Enabling empty Recents displayed a blank section.");
                fixture.Fill(firstWide: true); Pump(350); SettleRecentSection(section);
                fixture.Fill(firstWide: true); Pump(30);
                Require(double.IsNaN(section.Extent) && !NativeSurfaceMotion.IsRunning(section, MotionChannel.Layout),
                    "A synchronous recent-items refresh restarted section visibility motion.");
                var viewer = ControlTree(root).OfType<ScrollViewer>().First();
                for (var state = 0; state < 3; state++)
                {
                    if (state == 1) { fixture.Fill(firstWide: false); Pump(300); fixture.Fill(firstWide: true); Pump(300); }
                    if (state == 2)
                    {
                        var hosts = ControlTree(root).OfType<DashboardTileHost>().ToArray();
                        var sizes = hosts.Select(h => h.RenderSize).ToArray();
                        SmoothScrollCompositor.Begin(viewer, []); SmoothScrollCompositor.TryApply(viewer, 110, 0, true, out _, out _);
                        window.UpdateLayout();
                        Require(hosts.All(h => !h.IsBitmapMode) && hosts.Select(h => h.RenderSize).SequenceEqual(sizes),
                            "Recent cards changed size or paint representation while scrolling.");
                        SmoothScrollCompositor.End(viewer, 110, 0); Pump(250);
                        viewer.ScrollToLeftEnd(); Pump(250);
                    }
                    var buttons = ControlTree(root).OfType<Button>().Where(b => ChromeHoverMotionBehavior.GetIsEnabled(b) && ChromeHoverMotionBehavior.GetProfile(b) == ChromeHoverMotionProfile.RecentCard).Take(2).ToArray();
                    Require(buttons.Length == 2, "Production recent items were not realized.");
                    foreach (var button in buttons)
                    {
                        var face = (InteractionSurface)button.Template.FindName("CardBorder", button);
                        var index = Array.IndexOf(buttons, button);
                        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                        Pump(200);
                        SettleCardTint(face);
                        Require(Math.Abs(UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y - UiMotion.CardHoverLiftY) < .01,
                            "Recent-card paint cancelled the shared card lift.");
                        var suffix = $"production-{dashboard}-{theme}-{state}-{index}";
                        SaveRecentTintCapture(scene, suffix + "-hover");
                        CheckCardTint(face);
                        Require(CommandLightBehavior.LightFor(face) is null, "Recent card retained a different command-light tint.");
                        SurfaceMotion.Stop(face, MotionChannel.Interaction);
                        var tint = SetCardTintMask(face);
                        File.WriteAllLines(Path.Combine(_output, suffix + "-geometry.txt"), ControlTree(face).OfType<FrameworkElement>().Select(v =>
                            $"{v.GetType().Name}: size={v.RenderSize}; margin={v.Margin}; offset={v.TranslatePoint(new(), face)}; clip={VisualTreeHelper.GetClip(v)?.Bounds}; drawing={VisualTreeHelper.GetDrawing(v)?.Bounds}"));
                        var bitmap = Snapshot(scene, 1); SaveRecentTintCapture(scene, suffix + "-tint-mask");
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        foreach (var local in new[] { new Point(3, face.ActualHeight / 2), new Point(face.ActualWidth - 4, face.ActualHeight / 2),
                            new Point(face.ActualWidth / 2, 3), new Point(face.ActualWidth / 2, face.ActualHeight - 4) })
                        {
                            var point = face.TranslatePoint(local, scene); var p = ((int)point.Y * bitmap.PixelWidth + (int)point.X) * 4;
                            Require(pixels[p] > 200 && pixels[p + 1] < 60 && pixels[p + 2] < 60,
                                $"{suffix}: card tint is inset at {local}, face={face.RenderSize}, pixel={string.Join(',', pixels.Skip(p).Take(4))}");
                        }
                        RestoreCardTint(face);
                        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }); Pump(200);
                        SettleCardTint(face);
                        Require(tint.Opacity == 0, "Recent card tint failed to leave.");
                    }
                }
                foreach (var mode in new[] { MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(mode, false);
                    fixture.Clear(); Pump(350);
                    Require(section.Visibility == Visibility.Collapsed, "Reduced/Off retained empty Recents.");
                    fixture.Fill(firstWide: true); Pump(350);
                    Require(section.IsVisible && !NativeSurfaceMotion.IsRunning(section, MotionChannel.Layout),
                        "The recent section did not settle after restoring content.");
                    var button = ControlTree(root).OfType<Button>().First(b => ChromeHoverMotionBehavior.GetIsEnabled(b) && ChromeHoverMotionBehavior.GetProfile(b) == ChromeHoverMotionProfile.RecentCard);
                    var face = (FrameworkElement)button.Template.FindName("CardBorder", button);
                    button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }); Pump(240);
                    Require(ControlTree(face).OfType<SurfacePaint>().Where(p => p.GetType().Name == "Light").All(p => mode == MotionPreference.Off ? p.Opacity == 0 || !p.IsVisible : p.Opacity == 1 && p.IsVisible),
                        "Reduced lost its card tint or Off retained it.");
                    Require(!NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction), "The recent-card hover did not settle.");
                    button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                }
                Console.WriteLine($"PASS production {dashboard}/{theme}: empty/disabled Recents, first-item reveal, refresh coalescing, reduced motion, shared tint, scrolling and edge mask.");
            }
            finally { window.Close(); Pump(40); }
        }
    }
    private static void SettleRecentSection(LayoutRevealHost section)
    {
        for (var i = 0; i < 40 && NativeSurfaceMotion.IsRunning(section, MotionChannel.Layout); i++) Pump(20);
        Require(!NativeSurfaceMotion.IsRunning(section, MotionChannel.Layout), "Recent section failed to settle.");
    }
    private static FrameworkElement SetCardTintMask(FrameworkElement face)
    {
        var tracks = new List<NativeMotionTrack>();
        SurfaceLight.AddTracks(face, tracks, true, wash: Brushes.Blue, edge: Brushes.Blue);
        var tint = (FrameworkElement)tracks.Single().Target; tint.Opacity = 1; return tint;
    }
    private static void SettleCardTint(FrameworkElement face)
    {
        // Native animation ticks can trail a wall-clock timer on a cold render/readback.
        for (var i = 0; i < 40 && NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction); i++) Pump(20);
        Require(!NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction), "Card hover failed to settle.");
    }
    private static void RestoreCardTint(FrameworkElement face)
        => SurfaceLight.AddTracks(face, new List<NativeMotionTrack>(), true,
            wash: (Brush)_app.FindResource("HoverSurfaceWash"), edge: (Brush)_app.FindResource("HoverSurfaceEdge"));
    private static void CheckCardTint(FrameworkElement face)
    {
        var tint = ControlTree(face).OfType<SurfacePaint>().Single(p => p.GetType().Name == "Light" && p.Opacity > .9);
        var brushes = CardTintBrushes(VisualTreeHelper.GetDrawing(tint)).OfType<LinearGradientBrush>().ToArray();
        bool Matches(LinearGradientBrush actual, string key)
        {
            var expected = (LinearGradientBrush)_app.FindResource(key);
            return actual.StartPoint == expected.StartPoint && actual.EndPoint == expected.EndPoint
                && actual.MappingMode == expected.MappingMode
                && actual.GradientStops.Select(s => (s.Color, s.Offset)).SequenceEqual(expected.GradientStops.Select(s => (s.Color, s.Offset)));
        }
        Require(brushes.Any(b => Matches(b, "HoverSurfaceWash")) && brushes.Any(b => Matches(b, "HoverSurfaceEdge")),
            "Recent-card tint differs from the main-card wash or lit edge.");
        Require(Math.Abs(tint.Opacity - 1) < .001, "Recent-card tint has a different hover strength.");
    }
    private static IEnumerable<Brush> CardTintBrushes(Drawing? drawing)
    {
        if (drawing is GeometryDrawing { Brush: { } brush }) yield return brush;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children) foreach (var value in CardTintBrushes(child)) yield return value;
    }
    private static void SaveRecentTintCapture(FrameworkElement root, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Snapshot(root, 1)));
        using var stream = File.Create(Path.Combine(_output, name + ".png")); encoder.Save(stream);
    }
}
