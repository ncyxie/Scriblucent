using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeSurfaceCards()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            var main = XDocument.Load("Scriblucent/MainWindow.xaml");
            var resources = new XElement(wpf + "ResourceDictionary", main.Descendants(wpf + "Style").Where(e => new[] { "DashboardGroupedListingCardStyle", "DashboardListingCardSelectionStyle", "DashboardListingMenuButtonStyle" }.Contains((string?)e.Attribute(x + "Key"))).Select(e => new XElement(e)));
            var resourceHost = Fragment(new XElement(wpf + "Grid", new XElement(wpf + "Grid.Resources", resources)), main.Root!);
            _app.Resources.MergedDictionaries.Add(resourceHost.Resources);
            var root = new Grid { Width = 900, Height = 600, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(0), Background = (Brush)_app.FindResource("WindowBackground") };
            var stack = new StackPanel { Margin = new(20) }; root.Children.Add(stack);
            stack.Children.Add(new TextBlock { Text = "Production dashboard faces", Foreground = (Brush)_app.FindResource("TextColor"), FontSize = 24, FontWeight = FontWeights.SemiBold });
            var grid = new UniformGrid { Columns = 2, Margin = new(0, 16, 0, 0) }; stack.Children.Add(grid);
            var fixtures = new (string File, string Name, object Model)[]
            {
                ("Scriblucent/Controls/NoteCardControl.xaml", "CardBorder", new NoteCardViewModel(new NoteDocument { Title = "A useful idea", Tags = new() { "Biology" }, Content = "Space for the content to breathe." }, _ => { })),
                ("Scriblucent/Views/Dashboards/FlashcardsDashboardPage.xaml", "FlashcardSetCardBorder", new FlashcardSetViewModel(new FlashcardSetDocument { Title = "Biology flashcards", Tags = new() { "Biology" } })),
                ("Scriblucent/Views/Dashboards/MindMapsDashboardPage.xaml", "MindMapCardBorder", new MindMapViewModel(new MindMapDocument { Title = "Connected ideas", Tags = new() { "Biology" } })),
                ("Scriblucent/Views/Dashboards/QuizzesDashboardPage.xaml", "QuizCardBorder", new QuizViewModel(new QuizDocument { Title = "Knowledge check", Tags = new() { "Biology" } }))
            };
            foreach (var item in fixtures)
            {
                var source = XDocument.Load(item.File);
                var localResources = source.Root!.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources"));
                if (localResources is not null)
                {
                    var localHost = Fragment(new XElement(wpf + "Grid", new XElement(wpf + "Grid.Resources", new XElement(wpf + "ResourceDictionary", localResources.Elements().Select(e => new XElement(e))))), source.Root);
                    _app.Resources.MergedDictionaries.Add(localHost.Resources);
                }
                var markup = new XElement(source.Descendants().First(e => (string?)e.Attribute(x + "Name") == item.Name));
                foreach (var attribute in markup.DescendantsAndSelf().SelectMany(e => e.Attributes()).ToArray())
                    if (!attribute.IsNamespaceDeclaration && attribute.Name.NamespaceName.Length == 0 && Regex.IsMatch(attribute.Name.LocalName, "^(Click|Checked|Unchecked|Loaded|Unloaded|Drop|Opened|Closed|SizeChanged)$|^(Mouse|PreviewMouse|Drag)")) attribute.Remove();
                var face = Fragment(markup, source.Root!); face.DataContext = item.Model;
                // The production listing controller restores the entry pose to full scale.
                var (restScale, _) = UiMotion.EnsureScaleTranslateGroup(face);
                restScale.ScaleX = restScale.ScaleY = 1;
                face.Width = item.Name == "CardBorder" ? 220 : 316; face.Height = item.Name == "CardBorder" ? 180 : 224;
                var card = new Grid { Margin = new(12), Width = face.Width, Height = face.Height }; card.Children.Add(new CardShadowLayer()); card.Children.Add(face);
                grid.Children.Add(new Viewbox { Width = 405, Height = 255, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = card });
            }
            var host = HostSurfaceFixture(root, 940, 680);
            try
            {
                Pump(200); host.UpdateLayout(); SaveControlCapture(root, $"cards-{theme}");
                var titles = ControlTree(root).OfType<TextBlock>().Where(t => t.FontSize == 16 && t.FontWeight == FontWeights.SemiBold).ToArray();
                Require(titles.Length == 3, "The three large card titles did not share the large-card type size.");
                foreach (var title in titles) title.Text = "A longer dashboard card title that must leave room for the menu";
                host.UpdateLayout();
                foreach (var title in titles)
                    Require(title.ActualWidth <= ((FrameworkElement)title.Parent).ActualWidth + .1 && title.TextTrimming == TextTrimming.CharacterEllipsis,
                        "Large card title overflowed its available header width.");
                SaveControlCapture(root, $"cards-{theme}-long-titles");
            }
            finally { host.Close(); Pump(50); }
            checks.Add($"{theme}: four production dashboard face fragments with synthetic view models and shared shadows.");
        }
        Require(!RenderTransitionClock.IsAnyRunning, "Dashboard face fixtures leaked observers.");
        File.WriteAllLines(Path.Combine(_output, "card-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
