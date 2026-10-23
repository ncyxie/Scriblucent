using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;

internal static partial class Program
{
    // Reflection lets the exact same fixture capture the production renderer before and after migration.
    private static Border NewMaterialFace() => typeof(SurfaceMaterial).Assembly.GetType("Scriblucent.Controls.SurfaceMaterialBorder") is { } type
        ? (Border)Activator.CreateInstance(type)! : new Border();

    private static void ProbeSurfaceCoherence()
    {
        ProbeColoredSurfaces();
        var checks = new List<string>(); var costs = new List<object>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            foreach (var preference in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(preference, false);
                var gallery = BuildNeutralGallery(); var window = HostSurfaceFixture(gallery, 940, 860);
                try
                {
                    Pump(180); window.UpdateLayout();
                    foreach (var button in ControlTree(gallery).OfType<Button>().Where(b => b.Tag is string))
                    {
                        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
                        var pose = (string)button.Tag!; var size = button.RenderSize;
                        face.AnimateState(pose is "Hover" or "Press", pose == "Press", false);
                        Require(size == button.RenderSize, "Neutral feedback changed hit geometry.");
                    }
                    SaveControlCapture(gallery, $"neutral-{theme}-{preference}");
                    checks.Add($"{theme}/{preference}: production gray/colored actions, selectors, flat text-entry areas, raised cards and flat content planes.");
                }
                finally { window.Close(); Pump(40); }
            }
            MotionPolicy.Configure(MotionPreference.Off, false);
            foreach (var material in new[] { false, true })
            {
                var controls = new WrapPanel { Width = 900 };
                for (var i = 0; i < 48; i++)
                {
                    var button = new Button { Content = "Action", Width = 140, Height = 36, Margin = new(4), Style = (Style)_app.FindResource("DialogSecondaryButtonStyle"), MinWidth = 0, MinHeight = 0 };
                    if (!material) SurfaceMaterial.SetKind(button, SurfaceMaterialKind.None);
                    controls.Children.Add(button);
                }
                var host = HostSurfaceFixture(controls, 940, 500);
                try
                {
                    Pump(80); host.UpdateLayout(); _ = Snapshot(controls, 1); var samples = new List<double>();
                    for (var i = 0; i < 30; i++) { var watch = Stopwatch.StartNew(); _ = Snapshot(controls, 1); watch.Stop(); samples.Add(watch.Elapsed.TotalMilliseconds); }
                    var sorted = samples.Order().ToArray();
                    costs.Add(new { theme, material, surfaces = 48, warmCaptureMedianMs = (sorted[14] + sorted[15]) / 2, warmCaptureP95Ms = sorted[28], warmCaptureMaxMs = sorted[^1], samples });
                }
                finally { host.Close(); Pump(30); }
            }
        }
        MotionPolicy.Configure(MotionPreference.Full, false);
        Require(!RenderTransitionClock.IsAnyRunning, "Neutral fixtures leaked idle observers.");
        File.WriteAllLines(Path.Combine(_output, "neutral-checks.txt"), checks);
        File.WriteAllText(Path.Combine(_output, "neutral-costs.json"), JsonSerializer.Serialize(costs, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static Grid BuildNeutralGallery()
    {
        var root = new Grid { Width = 900, Height = 800, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = (Brush)_app.FindResource("WindowBackground") };
        var body = new StackPanel { Margin = new(20) }; root.Children.Add(body);
        TextBlock Label(string text) => new() { Text = text, Foreground = (Brush)_app.FindResource("TextColor"), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 8) };
        body.Children.Add(new TextBlock { Text = "Surface hierarchy", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (Brush)_app.FindResource("TextColor") });
        foreach (var pose in new[] { "Rest", "Hover", "Press" })
        {
            body.Children.Add(Label(pose)); var row = new UniformGrid { Columns = 4 };
            foreach (var action in new[] { ("Secondary", "More options"), ("Secondary", "Check for updates"), ("Primary", "Save changes"), ("Success", "Begin study") })
                row.Children.Add(new Button { Content = action.Item2, Style = (Style)_app.FindResource("Dialog" + action.Item1 + "ButtonStyle"), Margin = new(0, 0, 12, 0), Tag = pose });
            body.Children.Add(row);
        }
        body.Children.Add(Label("Selectors share the control finish; typing areas stay quiet"));
        var fields = new UniformGrid { Columns = 3 };
        fields.Children.Add(new ComboBox { ItemsSource = new[] { "English", "Lietuvių" }, SelectedIndex = 0, Margin = new(0, 0, 12, 0), Style = (Style)_app.FindResource("ChromeComboBoxStyle") });
        fields.Children.Add(new ComboBox { ItemsSource = new[] { "Everyday collection", "Biology" }, SelectedIndex = 0, Margin = new(0, 0, 12, 0), Style = (Style)_app.FindResource("FeatureToolbarComboBoxStyle") });
        fields.Children.Add(new TextBox { Text = "Search your notes…", Margin = new(0, 0, 12, 0) }); body.Children.Add(fields);
        body.Children.Add(Label("Raised objects keep a quiet face and a gently lit perimeter"));
        var cards = new UniformGrid { Columns = 3 };
        foreach (var title in new[] { "A useful idea", "Biology flashcards", "Study breakdown" })
        {
            var card = NewMaterialFace(); card.Background = (Brush)_app.FindResource("CardBackground"); card.BorderBrush = (Brush)_app.FindResource("NoteCardBorder");
            card.BorderThickness = new(1); card.CornerRadius = new(14); card.Padding = new(16); card.Height = 155; card.Margin = new(0, 0, 12, 0);
            if (Enum.TryParse<SurfaceMaterialKind>("Raised", out var raised)) SurfaceMaterial.SetKind(card, raised);
            var content = new StackPanel(); content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = (Brush)_app.FindResource("TextColor") });
            content.Children.Add(new TextBlock { Text = "Space for the content to breathe.\nUseful detail remains easy to read.", Margin = new(0, 10, 0, 0), FontSize = 14, Foreground = (Brush)_app.FindResource("TextColorSecondary") });
            card.Child = content; cards.Children.Add(card);
        }
        body.Children.Add(cards);
        body.Children.Add(Label("A large content plane remains calm"));
        var plane = new Border { Height = 180, Background = (Brush)_app.FindResource("InputSurface"), CornerRadius = new(14), Padding = new(20), Margin = new(0, 0, 12, 0) };
        plane.Child = new TextBlock { Text = "Your writing stays the focus.\n\nSurfaces support reading without adding decorative texture behind every paragraph.", FontSize = 14, Foreground = (Brush)_app.FindResource("TextColor") }; body.Children.Add(plane);
        return root;
    }
}
