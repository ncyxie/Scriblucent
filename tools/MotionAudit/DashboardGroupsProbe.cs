using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbeDashboardGroups(string? only = null)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load("Scriblucent/MainWindow.xaml");
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            var shell = new MainWindow(settings); // Never shown; no persisted-content startup.
            ((MainViewModel)shell.DataContext).PersistenceDirectoryOverride = Path.Combine(_output, "stores", theme);
            void Drop(Border face, bool active) => typeof(MainWindow).GetMethod("SetDropZoneBorderVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(shell, [face, active]);
            try
            {
                foreach (var dashboard in new[] { "Notes", "Flashcards", "MindMaps", "Quizzes" })
                {
                    if (only is not null && only != $"{theme}/{dashboard}") continue;
                    MotionPolicy.Configure(MotionPreference.Off, false);
                    var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
                    var markup = source.Descendants().Single(e => ((string?)e.Attribute("Style"))?.Contains("GroupCardStyle") == true);
                    var wrapper = new XElement(wpf + "Grid", new XAttribute("Margin", "20"),
                        new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()), new XElement(markup));
                    StripFixtureEvents(wrapper);
                    var root = (Grid)Fragment(wrapper, source.Root!);
                    var face = root.Children.OfType<Border>().Single(); face.VerticalAlignment = VerticalAlignment.Top;
                    var (model, items) = GroupFixture(dashboard); face.DataContext = model;
                    var scene = new Grid { Background = (Brush)_app.FindResource("CardBackground"),
                        Children = { new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } } };
                    var window = new Window { Width = 920, Height = 560, Content = scene, ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true };
                    try
                    {
                        _app.MainWindow = window; window.Show(); Pump(80); CardSurfaceMotionBehavior.PlayGroupEnter(face); window.UpdateLayout();
                        var header = ControlTree(face).OfType<DashboardGroupHeader>().Single();
                        var name = (TextBlock)header.Template.FindName("GroupName", header);
                        var count = (TextBlock)header.Template.FindName("GroupCount", header);
                        var naturalName = new TextBlock { Text = name.Text, FontFamily = name.FontFamily, FontSize = name.FontSize, FontWeight = name.FontWeight, FontStyle = name.FontStyle, FontStretch = name.FontStretch,
                            Visibility = Visibility.Hidden, UseLayoutRounding = false };
                        TextOptions.SetTextFormattingMode(naturalName, TextOptions.GetTextFormattingMode(name));
                        scene.Children.Add(naturalName); // Match the real window's DPI/font metrics.
                        naturalName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double naturalNameWidth = naturalName.DesiredSize.Width;
                        scene.Children.Remove(naturalName);
                        var titleArea = (FrameworkElement)header.Template.FindName("TitleArea", header);
                        var disclosureButton = FindGroupDisclosure(header);
                        Require(name.ActualWidth + .01 >= naturalNameWidth, $"An unconstrained group name was truncated: slot={name.ActualWidth}, natural={naturalNameWidth}, panel={titleArea.DesiredSize}/{titleArea.RenderSize}/round={titleArea.UseLayoutRounding}, button={disclosureButton.DesiredSize}/{disclosureButton.RenderSize}/round={disclosureButton.UseLayoutRounding}, family={name.FontFamily}.");
                        var actions = ((StackPanel)header.Content).Children.OfType<Button>().ToArray();
                        Require(header.ItemCount == 2 && count.Text == "2", $"Initial group count was not rendered: {dashboard}/{theme}, value={header.ItemCount}, text='{count.Text}'.");
                        Require(actions.Length == 3 && actions.All(b => b.RenderSize == new Size(24, 24)),
                            $"Group management actions lost their compact pair: count={actions.Length}, sizes={string.Join(", ", actions.Select(b => $"{b.RenderSize}/{b.Visibility}/style={b.Style is not null}"))}, header={header.RenderSize}.");
                        Require(actions.Take(2).All(button => button.Margin == new Thickness(0, 0, 2, 0)) && actions[2].Margin == new Thickness(), "Group action gaps differ.");
                        Require(((StackPanel)header.Content).Children.OfType<ItemReorderControls>().Count() == 1, "Group lost its movement controls inside the shared action strip.");
                        Require(!ControlTree(titleArea).OfType<Button>().Any() && disclosureButton.ActualWidth == 24,
                            "Group title retained disclosure behavior or the dedicated chevron lost its target.");
                        Require(face.Background == _app.FindResource("ContentInsetSurface") && face.Padding == new Thickness(12)
                            && face.CornerRadius == new CornerRadius(14) && face.Effect is null, "Group shell diverged from its inset material.");
                        var neutral = face.Background;
                        foreach (var colour in new[] { "#F8FAFF", "#EEF0FE", "#ECFDF3", "#FFF1F2", "#FFFBEB", "#F5F3FF", "#F0FDFA", "#F8FAFC", "#FFF7ED" })
                        {
                            model.GetType().GetMethod("SetBackground")!.Invoke(model, [colour]); window.UpdateLayout(); Pump(10);
                            Require(ReferenceEquals(neutral, face.Background), "Group colour changed the neutral body.");
                            Require(header.GroupBrush == model.GetType().GetProperty("BackgroundBrush")!.GetValue(model), "Group accent failed to follow its saved colour.");
                        }
                        model.GetType().GetMethod("SetBackground")!.Invoke(model, ["#ECFDF3"]); Pump(20);
                        SaveControlCapture(scene, $"groups-{dashboard}-{theme}-wide");
                        var originalItems = items.Cast<object>().ToArray(); items.Clear(); Pump(20);
                        Require(header.ItemCount == 0 && count.Text == "0", "Group count did not follow removal.");
                        foreach (var item in originalItems) items.Add(item); Pump(20);
                        Require(header.ItemCount == 2 && count.Text == "2", "Group count did not follow additions.");
                        model.GetType().GetProperty("Name")!.SetValue(model, "A deliberately long collection name that must leave every group action usable");
                        Pump(20); Require(name.Text == header.Header as string && name.ToolTip as string == name.Text, "Group rename/full-name tooltip failed.");
                        foreach (var width in new[] { 920d, 420d, 300d })
                        {
                            window.Width = width; window.UpdateLayout(); Pump(20);
                            var wrapped = header.ActualWidth < DashboardGroupHeaderPanel.CompactWidth;
                            Require(Math.Abs(header.ActualHeight - (wrapped ? 80 : 36)) < .01, "Group header did not use its expected row arrangement.");
                            Require(name.ActualWidth > 0 && name.TranslatePoint(new(), header).X >= 0, "Group title lost its bounded slot.");
                            Require(actions.All(b => b.TranslatePoint(new(), header).X >= 0 && b.TranslatePoint(new(b.ActualWidth, 0), header).X <= header.ActualWidth + .01), "Group action overflowed its header.");
                            SaveControlCapture(scene, $"groups-{dashboard}-{theme}-{(wrapped ? "narrow" : "long")}");
                        }
                        window.Width = 920; Pump(40); window.UpdateLayout();
                        var size = face.RenderSize; var padding = face.Padding; var radius = face.CornerRadius; var frame = face.BorderThickness;
                        var namePosition = name.TranslatePoint(new(), face);
                        MotionPolicy.Configure(MotionPreference.Full, false); Drop(face, true); Pump(180); window.UpdateLayout();
                        Require(face.RenderSize == size && face.Padding == padding && face.CornerRadius == radius && face.BorderThickness == frame
                            && name.TranslatePoint(new(), face) == namePosition, "Group drop feedback changed layout or contour.");
                        SaveControlCapture(scene, $"groups-{dashboard}-{theme}-drop");
                        Drop(face, false); Pump(170);
                        Drop(face, true); root.Children.Remove(face); Pump(30);
                        Require(typeof(MainWindow).GetField("_activeGroupDropBorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell) is null,
                            "Group drop feedback retained an unloaded target.");
                        root.Children.Add(face); Pump(30);
                        CardSurfaceMotionBehavior.PlayGroupEnter(face); Pump(70);
                        var (scale, _) = UiMotion.EnsureScaleTranslateGroup(face);
                        Require(scale.ScaleX == 1 && scale.ScaleY == 1, "Group entrance scaled its contents.");
                        foreach (var mode in new[] { MotionPreference.Reduced, MotionPreference.Off })
                        {
                            MotionPolicy.Configure(mode, false); CardSurfaceMotionBehavior.PlayGroupEnter(face); Pump(300);
                            Require(face.Opacity == 1 && scale.ScaleX == 1 && scale.ScaleY == 1 && UiMotion.EnsureScaleTranslateGroup(face).Translate.Y == 0,
                                "Group entrance did not settle at rest.");
                        }
                        checks.Add($"PASS {dashboard}/{theme}: actual group template, 9 colours, live count/name, compact actions, 4-scale captures, fixed drop contour, unload cleanup, and motion preferences.");
                        Console.WriteLine(checks[^1]);
                    }
                    finally { window.Close(); Pump(20); }
                    ProbeGroupPageFinishing(dashboard, theme, source, main, checks);
                }
            }
            finally { shell.Close(); }
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) is null,
            "Group fixtures queued a preferences write.");
        Require(checks.Count != 0, "No dashboard group case matched.");
        File.WriteAllLines(Path.Combine(_output, "group-checks.txt"), checks);
    }

    private static (object Model, IList Items) GroupFixture(string dashboard)
    {
        const string title = "Planning & ideas", colour = "#F8FAFF";
        switch (dashboard)
        {
            case "Notes":
                var notes = new NoteGroupViewModel(Guid.NewGuid(), title, colour,
                    Enumerable.Range(1, 2).Select(i => new NoteCardViewModel(new NoteDocument { Title = $"Project note {i}" }, _ => { })));
                return (notes, notes.Notes);
            case "Flashcards":
                var sets = new FlashcardSetGroupViewModel(Guid.NewGuid(), title, colour,
                    Enumerable.Range(1, 2).Select(i => new FlashcardSetViewModel(new FlashcardSetDocument { Title = $"Vocabulary {i}" })));
                return (sets, sets.Sets);
            case "MindMaps":
                var maps = new MindMapGroupViewModel(Guid.NewGuid(), title, colour,
                    Enumerable.Range(1, 2).Select(i => new MindMapViewModel(new MindMapDocument { Title = $"Connections {i}" })));
                return (maps, maps.MindMaps);
            default:
                var quizzes = new QuizGroupViewModel(Guid.NewGuid(), title, colour,
                    Enumerable.Range(1, 2).Select(i => new QuizViewModel(new QuizDocument { Title = $"Practice {i}" })));
                return (quizzes, quizzes.Quizzes);
        }
    }
}
