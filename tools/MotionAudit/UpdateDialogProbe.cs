using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeUpdateDialogShadow()
    {
        _wake16 = true;
        var report = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture("en");
            MotionPolicy.Configure(MotionPreference.Automatic, false);
            var background = new Grid();
            background.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            var owner = new Window { Width = 980, Height = 760, Content = background, ShowInTaskbar = false };
            owner.Show(); _app.MainWindow = owner;
            foreach (var state in new[] { "current", "available", "error" })
            {
                var dialog = state == "error"
                    ? new UpdateCheckDialog("Could not check for updates", "The update server is temporarily unavailable.")
                    : new UpdateCheckDialog(new GitHubUpdateResult
                    {
                        CurrentVersion = "1.0.0",
                        HasComparableVersions = true,
                        IsUpdateAvailable = state == "available",
                        LatestRelease = new GitHubReleaseInfo
                        {
                            Name = "A new release", TagName = state == "available" ? "2.0.0" : "1.0.0",
                            HtmlUrl = "https://example.invalid/releases",
                            Body = state == "available" ? """
                                ## More comfortable daily work

                                **Smoother writing**, *clearer planning*, and small refinements throughout Scriblucent.

                                - **Notes:** improved editing and list layout.
                                - **Flashcards:** more comfortable study sessions.
                                - Read the [release details](https://example.invalid/releases).

                                | Area | Improvements |
                                | --- | --- |
                                | Dashboard | Refined feedback and navigation |
                                | Editors | Consistent controls and typography |

                                ### Complete release notes

                                All entries remain available by scrolling, including this final section.
                                """ : null
                        }
                    });
                dialog.Owner = owner;
                dialog.Show(); Pump(800);
                var shell = (OverlayDialogShell)dialog.Content;
                if (state == "available")
                {
                    var reader = ControlTree(shell).OfType<FlowDocumentScrollViewer>().Single();
                    var text = new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text;
                    Require(text.Contains("including this final section."), "Release notes were truncated.");
                    Require(!text.Contains("**"), "Markdown leaked into the formatted release notes.");
                    Require(reader.Document.TextAlignment == TextAlignment.Left, "Release prose uses stretched word spacing.");
                    foreach (var anchor in ReleaseDocumentElements<Hyperlink>(reader.Document))
                        Require(Equals(anchor.Foreground, reader.TryFindResource("AccentPrimarySubtleForeground")), "Release links do not use the theme's readable accent color.");
                }
                var face = ControlTree(shell).OfType<Border>().Single(element => element.Name == "DialogCard");
                var shadow = ControlTree(shell).OfType<BakedElevationLayer>().Single(layer => layer.Kind == BakedElevationLayer.ElevationKind.Dialog);
                var faceBounds = face.TransformToAncestor(shell).TransformBounds(new Rect(face.RenderSize));
                var shadowBounds = shadow.TransformToAncestor(shell).TransformBounds(new Rect(shadow.RenderSize));
                report.Add($"{theme}/{state}: face={faceBounds}; shadow={shadowBounds}; root={shell.RenderSize}.");
                var tolerance = 1 / VisualTreeHelper.GetDpi(shadow).DpiScaleX + .01;
                foreach (var image in ControlTree(shadow).OfType<Image>())
                {
                    var bounds = image.TransformToAncestor(shadow).TransformBounds(new Rect(image.RenderSize));
                    Require(bounds.Left >= -tolerance && bounds.Top >= -tolerance
                        && bounds.Right <= shadow.ActualWidth + tolerance && bounds.Bottom <= shadow.ActualHeight + tolerance,
                        $"{theme}/{state}: {image.Name} exceeds the shadow's arranged bounds: {bounds} vs {shadow.RenderSize}.");
                    if (image.Name.StartsWith("Bottom", StringComparison.Ordinal))
                        Require(Math.Abs(bounds.Bottom - shadow.ActualHeight) <= tolerance, "The lower shadow slices detached from the dialog.");
                }
                SaveControlCapture(shell, $"update-{state}-{theme}");
                if (state == "available")
                {
                    var reader = ControlTree(shell).OfType<FlowDocumentScrollViewer>().Single();
                    var scroller = ControlTree(reader).OfType<ScrollViewer>().Single();
                    scroller.ScrollToEnd(); Pump(100);
                    Require(scroller.VerticalOffset > 0, "Long release notes cannot be scrolled.");
                    var end = reader.Document.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    Require(end.Bottom > 0 && end.Bottom <= reader.ActualHeight + 1, "The final release-note line cannot be brought into view.");
                    SaveControlCapture(shell, $"update-notes-bottom-{theme}");
                }
                dialog.Close(); Pump(40);
            }
            owner.Close(); Pump(40);
        }
        File.WriteAllLines(Path.Combine(_output, "update-dialog-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }

    private static IEnumerable<T> ReleaseDocumentElements<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T element) yield return element;
            foreach (var descendant in ReleaseDocumentElements<T>(child)) yield return descendant;
        }
    }
}
