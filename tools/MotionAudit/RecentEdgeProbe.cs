using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

internal static partial class Program
{
    private static void ProbeRecentEdges()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load("Scriblucent/MainWindow.xaml");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            ProbeRecentEdgePixels(theme);
            foreach (var dashboard in new[] { "Notes", "Flashcards", "MindMaps", "Quizzes" })
            {
                var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
                var body = source.Descendants().Single(node => ((string?)node.Attribute(x + "Name"))?.EndsWith("RecentSectionBody") == true);
                var wrapper = new XElement(wpf + "Grid", new XAttribute("Margin", "20"),
                    new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()),
                    new XElement(body.Ancestors().Last(node => node.Name.LocalName == "LayoutRevealHost")));
                StripFixtureEvents(wrapper); var root = Fragment(wrapper, source.Root!);
                var fixture = new RecentTintFixture(); fixture.Fill(firstWide: false); root.DataContext = fixture;
                var surface = new SurfaceMaterialBorder { Style = (Style)_app.FindResource("WorkspaceFrameStyle"), Child = root };
                var window = new Window { Content = surface, Width = 720, Height = 240, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true };
                try
                {
                    window.Show(); Pump(180);
                    var viewer = ControlTree(root).OfType<ScrollViewer>().First();
                    var fade = ControlTree(viewer).OfType<RecentStripEdgeFade>().Single();
                    var presenter = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
                    var bar = (ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
                    Require(!fade.IsHitTestVisible && viewer.OpacityMask is null && presenter.OpacityMask is null,
                        "Recent edge treatment intercepted input or masked the live content tree.");
                    Require(fade.ActualWidth == presenter.ActualWidth && fade.ActualHeight == presenter.ActualHeight,
                        "Recent fades did not follow the content viewport.");
                    Require(fade.LeftFadeWidth == 0 && fade.RightFadeWidth == 20, "Initial Recents faded the wrong edge.");
                    Require(fade.TranslatePoint(new Point(0, fade.ActualHeight), viewer).Y <= bar.TranslatePoint(new Point(), viewer).Y,
                        "Recent fade covered the scrollbar.");
                    viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth / 2); Pump(80);
                    Require(fade.LeftFadeWidth == 20 && fade.RightFadeWidth == 20, "Middle of Recents lost an overflowing edge.");
                    SaveControlCapture(surface, $"recent-edges-{dashboard}-{theme}");
                    viewer.ScrollToRightEnd(); Pump(80);
                    Require(fade.LeftFadeWidth == 20 && fade.RightFadeWidth == 0, "Final recent card retained its trailing fade.");
                    viewer.ScrollToHorizontalOffset(4); Pump(60);
                    Require(Math.Abs(fade.LeftFadeWidth - 4) < .1, "Leading fade snapped on at full width.");
                    viewer.ScrollToLeftEnd(); Pump(60);
                    MotionPolicy.Configure(MotionPreference.Full, false);
                    SmoothScrollCompositor.Begin(viewer, Array.Empty<DashboardVirtualizationHost>());
                    Require(SmoothScrollCompositor.TryApply(viewer, 4, 0, false, out _, out _), "Recent glide did not start.");
                    Require(viewer.HorizontalOffset == 0 && Math.Abs(fade.LeftFadeWidth - 4) < .1,
                        "Recent fade waited for a native offset commit instead of following the visible glide.");
                    SmoothScrollCompositor.TryApply(viewer, 8, 0, true, out _, out _);
                    Require(Math.Abs(fade.LeftFadeWidth - 8) < .1, "Fade jumped back during a pending native commit.");
                    Pump(40);
                    Require(Math.Abs(fade.LeftFadeWidth - 8) < .1, "Fade lost its position after a native commit.");
                    SmoothScrollCompositor.End(viewer, 8, 0); Pump(40);
                    Require(Math.Abs(fade.LeftFadeWidth - 8) < .1, "Fade jumped when the glide finished.");
                    MotionPolicy.Configure(MotionPreference.Off, false);
                    foreach (var collection in new IList[] { fixture.RecentNotes, fixture.RecentFlashcardSets, fixture.RecentMindMaps, fixture.RecentQuizzes })
                        while (collection.Count > 1) collection.RemoveAt(collection.Count - 1);
                    Pump(100);
                    Require(viewer.ScrollableWidth == 0 && fade.LeftFadeWidth == 0 && fade.RightFadeWidth == 0, "Fades remained when all recent cards fit.");
                    window.Width = 420; Pump(80); fixture.Fill(firstWide: true); Pump(100);
                    Require(fade.RightFadeWidth > 0 && fade.ActualWidth <= viewer.ActualWidth, "Resized recent viewport did not update overflow.");
                    foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
                    {
                        MotionPolicy.Configure(mode, false); viewer.ScrollToRightEnd(); Pump(60);
                        Require(fade.RightFadeWidth == 0 && fade.LeftFadeWidth > 0, "Motion setting changed functional edge cues.");
                        viewer.ScrollToLeftEnd(); Pump(60);
                    }
                    MotionPolicy.Configure(MotionPreference.Off, false);
                    Console.WriteLine($"{dashboard}/{theme}: all overflow states, shrink-to-fit, resize, scrollbar exclusion and motion settings passed.");
                }
                finally { window.Close(); Pump(40); }
            }
        }
    }
    private static void ProbeRecentEdgePixels(string theme)
    {
        var viewer = new ScrollViewer { Style = (Style)_app.FindResource("RecentItemsStripScrollViewerStyle"),
            Height = 100, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center,
            Content = new Border { Width = 1200, Height = 80, Background = Brushes.SteelBlue } };
        ScrollBarAutoHideBehavior.SetEnable(viewer, false);
        var surface = new SurfaceMaterialBorder { Style = (Style)_app.FindResource("WorkspaceFrameStyle"), Child = viewer };
        var window = new Window { Width = 600, Height = 340, Content = surface, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(100); viewer.ScrollToHorizontalOffset(100); Pump(80);
            var fade = ControlTree(viewer).OfType<RecentStripEdgeFade>().Single();
            var bar = (ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
            foreach (var height in new[] { 340d, 480d })
            {
                window.Height = height; Pump(80);
                fade.Visibility = Visibility.Hidden; Pump(30); var original = Snapshot(surface, 2);
                fade.Visibility = Visibility.Visible; Pump(30); var softened = Snapshot(surface, 2);
                viewer.Visibility = Visibility.Hidden; Pump(30); var background = Snapshot(surface, 2); viewer.Visibility = Visibility.Visible; Pump(30);
                SaveControlCapture(surface, $"recent-edges-material-{theme}-{height}");
                static byte[] Pixel(BitmapSource bitmap, Point point)
                {
                    var value = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)Math.Floor(point.X * 2), (int)Math.Floor(point.Y * 2), 1, 1), value, 4, 0); return value;
                }
                static int Difference(byte[] first, byte[] second) => Enumerable.Range(0, 3).Max(index => Math.Abs(first[index] - second[index]));
                foreach (var y in new[] { 8d, fade.ActualHeight / 2, fade.ActualHeight - 8 })
                foreach (var edge in new[] { .5, fade.ActualWidth - .5 })
                {
                    var point = fade.TranslatePoint(new Point(edge, y), surface);
                    Require(Difference(Pixel(softened, point), Pixel(background, point)) <= 4,
                        $"Fade did not match the shaded workspace background at {point}: actual={string.Join(',', Pixel(softened, point))}, background={string.Join(',', Pixel(background, point))}, original={string.Join(',', Pixel(original, point))}.");
                }
                var center = fade.TranslatePoint(new Point(fade.ActualWidth / 2, fade.ActualHeight / 2), surface);
                Require(Difference(Pixel(original, center), Pixel(softened, center)) == 0, "Fade softened text/content in the center of the viewport.");
                var barPoint = bar.TranslatePoint(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2), surface);
                Require(Difference(Pixel(original, barPoint), Pixel(softened, barPoint)) == 0, "Fade softened the scrollbar.");
            }
            Console.WriteLine($"{theme}: pixel checks passed for background continuity after resizing, unchanged central content and unchanged scrollbar.");
        }
        finally { window.Close(); Pump(40); }
    }
}
