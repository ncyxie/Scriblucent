using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeDashboardToolbarMotion(string theme)
    {
        _wake16 = true; LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
        var (window, model, _) = LoadingWindow(theme, MotionPreference.Off, 0);
        var report = new List<string>();
        DashboardActionStrip? strip = null;
        try
        {
            window.Show(); WaitLoading(() => model.IsActiveDashboardReady); Pump(250);
            strip = (DashboardActionStrip)window.FindName("DashboardCollectionActions");
            var header = (DashboardHeaderPanel)window.FindName("DashboardHeader");
            var surfaces = strip.Children.Cast<FrameworkElement>().ToArray();
            var utilities = new[] { "TopSearchButton", "SortNotesButton", "TagsFilterButton", "DashboardSectionsButton" }
                .Select(name => (FrameworkElement)window.FindName(name)).ToArray();
            void Go(string dashboard)
            {
                (dashboard switch { "Flashcards" => model.ShowFlashcardsCommand, "Quizzes" => model.ShowQuizzesCommand,
                    "MindMaps" => model.ShowMindMapsCommand, _ => model.ShowNotesCommand }).Execute(null);
                strip.GetBindingExpression(DashboardActionStrip.DashboardProperty)!.UpdateTarget();
                header.UpdateLayout();
            }
            void Rest() { WaitLoading(() => !strip.IsTransitioning, 2500); Pump(32); header.UpdateLayout(); }
            Point[] UtilityPositions() => utilities.Select(button => button.TranslatePoint(new Point(), header)).ToArray();
            void FixedUtilities(Point[] before)
            {
                var after = UtilityPositions();
                Require(before.Zip(after).All(pair => (pair.First - pair.Second).Length < 1), "Dashboard action motion moved search/filter utilities.");
            }
            void CheckRest(bool show, bool statistics)
            {
                Rest();
                Require(strip.IsVisible == show && strip.IsHitTestVisible == show, "Action group retained stale visibility or hit testing.");
                for (int i = 0; i < 4; i++)
                {
                    bool wanted = show && (i != 2 || statistics);
                    var pose = (TranslateTransform)surfaces[i].RenderTransform;
                    Require((surfaces[i].Visibility == Visibility.Visible) == wanted && Math.Abs(pose.X) < .01 && Math.Abs(pose.Y) < .01,
                        "Toolbar surface retained an old visibility/position after settling.");
                    if (wanted) Require(Math.Abs(surfaces[i].Opacity - (i == 3 ? .7 : 1)) < .001, "Toolbar surface retained a partial fade.");
                }
            }
            // Warm fixture libraries before observing motion; disk loading is not this probe's subject.
            foreach (var dashboard in new[] { "Flashcards", "Quizzes", "MindMaps", "Notes" })
            { Go(dashboard); WaitLoading(() => model.IsActiveDashboardReady); Rest(); }
            foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(mode, false); Go("Notes"); CheckRest(false, false);
                var fixedPositions = UtilityPositions();
                Go("Flashcards");
                Require(mode == MotionPreference.Off ? !strip.IsTransitioning : strip.IsTransitioning, "Entry did not follow the selected motion preference.");
                if (mode == MotionPreference.Off)
                    Require(surfaces.All(surface => ((TranslateTransform)surface.RenderTransform).Y == 0), "Off entry retained a spatial lift.");
                if (mode == MotionPreference.Full)
                {
                    var frames = new List<BitmapSource> { ToolbarFrame(header) };
                    foreach (int elapsed in new[] { 32, 32, 32, 64, 96 }) { Pump(elapsed); frames.Add(ToolbarFrame(header)); FixedUtilities(fixedPositions); }
                    SaveGif(frames, $"toolbar-entry-{theme}-sampled-poses.gif");
                }
                CheckRest(true, true); FixedUtilities(fixedPositions);
                Go("Quizzes");
                Require(surfaces[0].Opacity == 1 && surfaces[1].Opacity == 1, "Shared buttons replayed their entrance on a sibling dashboard.");
                Require(!surfaces[2].IsHitTestVisible, "Departing Statistics retained an obsolete hit target.");
                if (mode == MotionPreference.Full)
                {
                    var frames = new List<BitmapSource> { ToolbarFrame(header) };
                    foreach (int elapsed in new[] { 40, 40, 40, 80, 120 }) { Pump(elapsed); frames.Add(ToolbarFrame(header)); FixedUtilities(fixedPositions); }
                    SaveGif(frames, $"toolbar-statistics-leave-{theme}-sampled-poses.gif");
                }
                CheckRest(true, false); FixedUtilities(fixedPositions);
                Go("Flashcards");
                Require(surfaces[0].Opacity == 1 && surfaces[1].Opacity == 1, "Statistics entry blanked the shared buttons.");
                if (mode != MotionPreference.Off) Require(!surfaces[2].IsHitTestVisible, "Invisible arriving Statistics could intercept a utility click.");
                if (mode == MotionPreference.Full)
                {
                    var frames = new List<BitmapSource> { ToolbarFrame(header) };
                    foreach (int elapsed in new[] { 40, 40, 40, 60, 80 }) { Pump(elapsed); frames.Add(ToolbarFrame(header)); FixedUtilities(fixedPositions); }
                    SaveGif(frames, $"toolbar-statistics-enter-{theme}-sampled-poses.gif");
                }
                CheckRest(true, true); FixedUtilities(fixedPositions);
                Go("Notes");
                Require(!strip.IsHitTestVisible, "Departing group could still receive clicks.");
                CheckRest(false, false); FixedUtilities(fixedPositions);
                report.Add($"{theme}/{mode}: staged group arrival/exit, retained shared buttons, Statistics handoff, stationary utilities, rest geometry and hit-target cleanup passed.");
            }
            MotionPolicy.Configure(MotionPreference.Full, false);
            foreach (var dashboard in new[] { "Flashcards", "Quizzes", "MindMaps", "Flashcards", "Notes", "Flashcards", "Quizzes" })
            { Go(dashboard); Pump(22); }
            CheckRest(true, false);
            Go("Flashcards"); Pump(25); MotionPolicy.Configure(MotionPreference.Off, false);
            Require(!strip.IsTransitioning, "Changing motion preference left a toolbar animation running.");
            CheckRest(true, true);
            MotionPolicy.Configure(MotionPreference.Full, false); Go("Notes"); Pump(20);
            window.Content = null; Pump(60);
            Require(!strip.IsTransitioning, "Unloading the toolbar retained native motion clocks.");
            report.Add($"{theme}: rapid reversals, preference changes during motion and unload cleanup passed. Pose captures are sampled WPF visuals, not presented-frame measurements.");
        }
        finally { CloseLoadingWindow(window); }
        File.WriteAllLines(Path.Combine(_output, "toolbar-motion-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }
    private static BitmapSource ToolbarFrame(FrameworkElement header)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)_app.FindResource("WindowBackground"), null, new Rect(header.RenderSize));
            context.DrawRectangle(new VisualBrush(header), null, new Rect(header.RenderSize));
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)header.ActualWidth), Math.Max(1, (int)header.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
}
