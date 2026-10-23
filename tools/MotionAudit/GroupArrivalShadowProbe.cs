using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeGroupArrivalShadows()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            var main = XDocument.Load("Scriblucent/MainWindow.xaml");
            var keys = new[] { "DashboardGroupedListingCardStyle", "DashboardListingCardSelectionStyle", "DashboardListingMenuButtonStyle" };
            var resources = new XElement(wpf + "ResourceDictionary", main.Descendants(wpf + "Style")
                .Where(e => keys.Contains((string?)e.Attribute(x + "Key"))).Select(e => new XElement(e)));
            var resourceHost = Fragment(new XElement(wpf + "Grid", new XElement(wpf + "Grid.Resources", resources)), main.Root!);
            _app.Resources.MergedDictionaries.Add(resourceHost.Resources);
            var root = new Grid { Background = (System.Windows.Media.Brush)_app.FindResource("WindowBackground") };
            var window = new Window { Width = 560, Height = 330, Content = root, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); _app.MainWindow = window;
            foreach (var dashboard in new[] { "Flashcards", "Quizzes", "MindMaps" })
            {
                MotionPolicy.Configure(MotionPreference.Off, false);
                var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
                var card = source.Descendants().First(e => e.Name.LocalName == "Border" && e.Attributes().Any(a =>
                    a.Name.LocalName == "CardSurfaceMotionBehavior.ShadowElementName" && a.Value.StartsWith("Grouped")));
                var markup = new XElement(card);
                foreach (var attribute in markup.DescendantsAndSelf().SelectMany(e => e.Attributes()).ToArray())
                    if (!attribute.IsNamespaceDeclaration && attribute.Name.NamespaceName.Length == 0
                        && Regex.IsMatch(attribute.Name.LocalName, "^(Click|Checked|Unchecked|Loaded|Unloaded|Drop|Opened|Closed|SizeChanged)$|^(Mouse|PreviewMouse|Drag)")) attribute.Remove();
                var fixture = (Border)Fragment(markup, source.Root!);
                fixture.DataContext = dashboard switch
                {
                    "Flashcards" => (object)new FlashcardSetViewModel(new FlashcardSetDocument { Title = "Arriving flashcards" }),
                    "Quizzes" => new QuizViewModel(new QuizDocument { Title = "Arriving quiz" }),
                    _ => new MindMapViewModel(new MindMapDocument { Title = "Arriving mind map" })
                };
                fixture.Width = 350; fixture.HorizontalAlignment = HorizontalAlignment.Center; fixture.VerticalAlignment = VerticalAlignment.Center;
                root.Children.Add(fixture); window.UpdateLayout(); Pump(60);
                var faceName = card.Attributes().Single(a => a.Name.LocalName == "CardSurfaceMotionBehavior.ShadowElementName").Value;
                var face = (FrameworkElement)fixture.FindName(faceName);
                var shadow = ControlTree(fixture).OfType<CardShadowLayer>().Single();
                Require(shadow.RenderSize == ((FrameworkElement)shadow.Content).RenderSize, "Shadow grid overgrew its host on a short grouped card.");
                Require(DashboardMotionCardCatalog.TryGetItemId(fixture, out var id), "Grouped fixture had no card ID.");
                checks.Add($"{theme}/{dashboard}/geometry: face={face.RenderSize}; shadow={shadow.RenderSize}; shadow root={((FrameworkElement)shadow.Content).RenderSize}; face alignment={face.VerticalAlignment}; face margin={face.Margin}.");
                var scope = dashboard == "Flashcards" ? DashboardMotionScope.Flashcards : dashboard == "Quizzes" ? DashboardMotionScope.Quizzes : DashboardMotionScope.MindMaps;
                var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, scope);
                engine.Bind(root, root, null); engine.Activate(); engine.PreparePendingEnter(id, grouped: true);
                MotionPolicy.Configure(MotionPreference.Full, false);
                typeof(DashboardMotionEngine).GetMethod("PlayCardEnter", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(engine, [fixture, face, id, null, 0, false]);
                // Capture deterministic poses of the real entry surface; wall-clock dispatcher delays
                // can otherwise skip the brief partial-opacity phase in a background audit window.
                SurfaceMotion.Stop(face);
                Pose(0); Check("initial"); Pose(.35); Check("arriving"); Pose(1); Check("rest");
                engine.Deactivate(); root.Children.Remove(fixture); Pump(30);

                void Pose(double opacity)
                {
                    var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(face);
                    face.Opacity = opacity; scale.ScaleX = scale.ScaleY = .975 + .025 * opacity;
                    translate.Y = 8 * (1 - opacity); window.UpdateLayout();
                }

                void Check(string phase)
                {
                    if (phase == "arriving") Require(face.Opacity > 0 && face.Opacity < 1, "Arrival fixture did not sample a partially visible card.");
                    foreach (var tile in ControlTree(shadow).OfType<Image>())
                        Require(Math.Abs(tile.Opacity - .58 * face.Opacity) < .00001, "Shadow stayed opaque during face arrival.");
                    Require(shadow.CacheMode is null && shadow.Effect is null, $"Shadow introduced an effect/cache: cache={shadow.CacheMode}, effect={shadow.Effect}.");
                    checks.Add($"{theme}/{dashboard}/{phase}: face opacity {face.Opacity:F3}; shadow tiles follow the same opacity; no live effect or shadow cache.");
                    if (phase != "initial") SaveControlCapture(root, $"group-arrival-{dashboard}-{theme}-{phase}");
                }
            }
            window.Close(); _app.Resources.MergedDictionaries.Remove(resourceHost.Resources);
        }
        File.WriteAllLines(Path.Combine(_output, "group-arrival-shadow-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
