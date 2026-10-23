using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;
using Scriblucent.Behaviors;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeUpdateDialogScroll()
    {
        _wake16 = true;
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previousPointer);
        var report = new List<string>();
        var body = File.ReadAllText("CHANGELOG.md").Split("## 1.0.0-beta.2", 2)[1].Trim();
        try
        {
            foreach (var size in new[] { new Size(980, 760), new Size(680, 540) })
            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
            {
                LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
                LocalizationService.SetCulture("en"); MotionPolicy.Configure(mode, false);
                var owner = new Window { Width=size.Width, Height=size.Height, Content=new Grid(), ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.CenterScreen };
                _app.MainWindow=owner; owner.Show(); Pump(60);
                var dialog=new UpdateCheckDialog(new GitHubUpdateResult
                {
                    CurrentVersion="1.0.0-beta.3", HasComparableVersions=true, IsUpdateAvailable=false,
                    LatestRelease=new GitHubReleaseInfo
                    {
                        Name="Scriblucent", TagName="v1.0.0-beta.2", HtmlUrl="https://example.invalid/releases", Body=body,
                        PublishedAt=new DateTimeOffset(2026,10,20,23,30,44,TimeSpan.Zero)
                    }
                }) { Owner=owner };
                dialog.Show(); dialog.Activate(); Pump(650);
                var reader=ControlTree(dialog).OfType<FlowDocumentScrollViewer>().Single();
                var scroller=ControlTree(reader).OfType<ScrollViewer>().Single();
                var shell=(OverlayDialogShell)dialog.Content;
                var card=ControlTree(shell).OfType<Border>().Single(b=>b.Name=="DialogCard");
                if(mode==MotionPreference.Automatic && size.Height>700)
                    SaveControlCapture(shell,$"update-notes-top-{theme}");
                var textPoint=reader.PointToScreen(new Point(30,45));
                SetCursorPos((int)textPoint.X,(int)textPoint.Y); Pump(50);
                var hit=Mouse.DirectlyOver?.GetType().Name;
                var offsets=new HashSet<double>();
                var hadViewportDrift=false;
                EventHandler sample=(_,_)=>
                {
                    offsets.Add(scroller.VerticalOffset);
                    hadViewportDrift |= SmoothScrollCompositor.TryGetVerticalDrift(scroller,out _);
                };
                RenderTransitionClock.AddFrameObserver(sample);
                try { mouse_event(0x0800,0,0,unchecked((uint)-120),UIntPtr.Zero); Pump(350); }
                finally { RenderTransitionClock.RemoveFrameObserver(sample); }
                var wheelOffset=scroller.VerticalOffset;
                var line=$"{theme}/{mode}/{size}: reader={reader.ActualWidth:F1}×{reader.ActualHeight:F1}, viewport={scroller.ViewportHeight:F1}, extent={scroller.ExtentHeight:F1}, smooth={SmoothScrollBehavior.GetIsEnabled(scroller)}, wheel={wheelOffset:F1}, hit={hit}, card={card.ActualHeight:F1}.";
                report.Add(line);Console.WriteLine(line);
                Require(SmoothScrollBehavior.GetIsEnabled(scroller),"The release reader bypasses shared smooth scrolling.");
                Require(wheelOffset>=scroller.ViewportHeight*.18,"A wheel notch over release text barely moves the document.");
                Require(card.ActualHeight<=dialog.ActualHeight-47,"The update card exceeds the owner viewport.");
                Require(card.ActualWidth<=560.5,"The update card exceeds the standard dialog width.");
                var notesFrame=(Border)dialog.FindName("ReleaseNotesFrame");
                var versionFrame=(Border)dialog.FindName("VersionSummaryFrame");
                var notesBounds=notesFrame.TransformToAncestor(shell).TransformBounds(new Rect(notesFrame.RenderSize));
                var versionBounds=versionFrame.TransformToAncestor(shell).TransformBounds(new Rect(versionFrame.RenderSize));
                Require(Math.Abs(notesBounds.Left-versionBounds.Left)<=1 && Math.Abs(notesBounds.Right-versionBounds.Right)<=1,
                    "The changelog and version panels do not share the same horizontal alignment.");
                Require(reader.ActualWidth>=390,"The release reader wastes available panel width.");
                Require(reader.ActualHeight<=280.5,"The release reader is oversized.");
                if(size.Height>700) Require(reader.ActualHeight>=250,"The release reader is too short for long notes.");
                Require(!hadViewportDrift,"Scrolling translates the viewport-sized document view between commits.");
                if(mode==MotionPreference.Automatic) Require(offsets.Count>4,"The document did not advance through intermediate eased offsets.");
                var readerBounds=new Rect(reader.RenderSize);
                Require(reader.Clip is { } clip && !clip.FillContains(new Point(0,0)) && clip.FillContains(new Point(readerBounds.Width/2,readerBounds.Height/2)),
                    "The inner release reading area is not rounded.");
                Require(dialog.FindName("ReleaseNotesFrame") is Border { Clip: not null },"The release surface does not clip to its rounded shape.");

                var bar=ControlTree(scroller).OfType<ScrollBar>().Single(b=>b.Orientation==Orientation.Vertical);
                var barPoint=bar.PointToScreen(new Point(bar.ActualWidth/2,bar.ActualHeight/2));
                SetCursorPos((int)barPoint.X,(int)barPoint.Y);Pump(80);
                mouse_event(0x0800,0,0,unchecked((uint)-120),UIntPtr.Zero);Pump(350);
                Require(scroller.VerticalOffset-wheelOffset>=scroller.ViewportHeight*.18,"Wheel input over the release scrollbar is inconsistent with text scrolling.");

                var thumb=ControlTree(bar).OfType<Thumb>().Single();
                var thumbPoint=thumb.PointToScreen(new Point(thumb.ActualWidth/2,thumb.ActualHeight/2));
                var beforeDrag=scroller.VerticalOffset;
                SetCursorPos((int)thumbPoint.X,(int)thumbPoint.Y);Pump(40);
                mouse_event(2,0,0,0,UIntPtr.Zero);Pump(30);
                SetCursorPos((int)thumbPoint.X,(int)(thumbPoint.Y+Math.Min(45,bar.ActualHeight/3)));Pump(60);
                mouse_event(4,0,0,0,UIntPtr.Zero);Pump(120);
                Require(scroller.VerticalOffset>beforeDrag+1,"The release scrollbar thumb cannot be dragged.");

                SetCursorPos((int)textPoint.X,(int)textPoint.Y);Pump(40);
                scroller.ScrollToEnd();Pump(120);
                var end=reader.Document.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                Require(scroller.VerticalOffset>0 && end.Bottom>0 && end.Bottom<=reader.ActualHeight+1,
                    $"The final release line cannot be reached: offset={scroller.VerticalOffset}, maximum={scroller.ScrollableHeight}, end={end}, height={reader.ActualHeight}.");
                if(mode==MotionPreference.Automatic) SaveControlCapture(shell,$"release-scroll-{theme}-{size.Height}");
                dialog.Close();Pump(80);owner.Close();Pump(50);
            }
        }
        finally
        {
            mouse_event(4,0,0,0,UIntPtr.Zero);
            SetCursorPos(previousPointer.X,previousPointer.Y);SetForegroundWindow(previousWindow);
        }
        File.WriteAllLines(Path.Combine(_output,"release-scroll-checks.txt"),report);
    }

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
