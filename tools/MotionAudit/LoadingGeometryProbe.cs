using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeDashboardChromeRegressions()
    {
        LoadNativeControlResources("Light");
        var report = new List<string>();
        var (window, vm, _) = LoadingWindow("Light", MotionPreference.Off, 24, held: true);
        var valid = true;
        try
        {
            window.Show(); Pump(120);
            var header = (Panel)window.FindName("DashboardHeader");
            var logo = (FrameworkElement)header.Children[0];
            var initialWidth = logo.ActualWidth;
            foreach (var (theme, width) in new[] { ("Light", 1280d), ("Dark", 1280d), ("Light", 760d), ("Dark", 760d) })
            {
                window.Width = width;
                // The audit's entry assembly is different from the app. Use the same theme
                // dictionary/source replacement with an assembly-qualified SVG resource.
                var dictionaries = _app.Resources.MergedDictionaries;
                var index = Enumerable.Range(0, dictionaries.Count).First(i =>
                    dictionaries[i].Source?.OriginalString.Contains("Theme.xaml") == true);
                dictionaries[index] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{theme}Theme.xaml") };
                _app.Resources["BrandLogoSvg"] = new Uri($"pack://application:,,,/Scriblucent;component/Assets/brand-logo-{(theme == "Dark" ? "light" : "dark")}.svg");
                Pump(150); window.UpdateLayout();
                report.Add($"{theme}/{width}: logo initial width={initialWidth:F2}, current={logo.ActualWidth:F2}, height={logo.ActualHeight:F2}");
                valid &= Math.Abs(initialWidth - logo.ActualWidth) <= 1 && Math.Abs(logo.ActualHeight - 36) <= 1 && logo.ActualWidth > 80;
                SaveControlCapture(logo, $"logo-{report.Count}-{theme}");

                var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
                var shell = (FrameworkElement)window.FindName("DashboardLoadingShell");
                var root = (FrameworkElement)window.FindName("ContentStage");
                shell.Visibility = Visibility.Hidden;
                foreach (var name in new[] { "Notes", "Flashcards", "Quizzes", "MindMaps" })
                    ((FrameworkElement)window.FindName(name + "DashboardView")).Visibility = Visibility.Hidden;
                layer.Visibility = Visibility.Visible; layer.Opacity = 1; window.UpdateLayout();
                var start = layer.TranslatePoint(new Point(), root);
                var loading = Snapshot(root, 1);
                layer.Visibility = Visibility.Collapsed; window.UpdateLayout();
                var ready = Snapshot(root, 1);
                var stride = ready.PixelWidth * 4;
                var a = new byte[stride * ready.PixelHeight]; var b = new byte[a.Length];
                loading.CopyPixels(a, stride, 0); ready.CopyPixels(b, stride, 0);
                var mismatches = 0;
                for (var y = (int)Math.Ceiling(start.Y) + 1; y < ready.PixelHeight - 20; y++)
                for (var x = 20; x < ready.PixelWidth - 20; x++)
                {
                    var offset = y * stride + x * 4;
                    if (!a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4))) mismatches++;
                }
                report.Add($"{theme}/{width}: blank workspace pixels changed by loading cover={mismatches}");
                valid &= mismatches == 0;
                layer.Visibility = Visibility.Visible; shell.Visibility = Visibility.Visible;
                SaveControlCapture((FrameworkElement)window.Content, $"chrome-{report.Count}-{theme}");
            }
        }
        finally { CloseLoadingWindow(window); }
        File.WriteAllLines(Path.Combine(_output, "chrome-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
        Require(valid, "Theme switching changed logo size or loading restarted the workspace background.");
    }

    private static void ProbeLoadingGeometry(string theme)
    {
        _wake16 = true;
        LoadNativeControlResources(theme);
        var report = new List<string>();
        var (window, vm, _) = LoadingWindow(theme, MotionPreference.Off, 400);
        try
        {
            window.Show(); WaitLoading(() => vm.IsActiveDashboardReady);
            var shell = (DashboardLoadingOverlay)window.FindName("DashboardLoadingShell");
            var layer = (FrameworkElement)window.FindName("DashboardLoadingLayer");
            vm.IsRecentSectionVisible = false;
            vm.IsCalendarFirst = true;
            vm.IsCalendarSectionExpanded = true;
            foreach (var kind in Enum.GetValues<EditorDocumentKind>())
            {
                SelectLoadingDashboard(vm, kind);
                WaitLoading(() => vm.IsActiveDashboardReady); Pump(200);
                foreach (var culture in new[] { "en", "lt" })
                foreach (var variant in new (double Width, bool Calendar)[] { (1280, true), (760, true), (640, true), (440, true), (440, false), (480, false) })
                {
                    if (kind != EditorDocumentKind.Note && !variant.Calendar) continue;
                    var width = variant.Width;
                    vm.IsCalendarSectionVisible = variant.Calendar;
                    LocalizationService.SetCulture(culture); vm.CalendarPlanner.RefreshLanguage(); window.Width = width;
                    layer.Visibility = Visibility.Collapsed;
                    window.UpdateLayout(); Pump(120);
                    var page = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                    var section = ControlTree(page).OfType<FrameworkElement>().First(e => e.Name.EndsWith("CalendarSectionContainer"));
                    var planner = ControlTree(section).OfType<CalendarPlannerView>().Single();
                    var layout = (DashboardLoadingOverlay.Layout)typeof(MainWindow).GetMethod("BuildDashboardSkeletonLayout", LoadingFlags)!.Invoke(window, [vm])!;
                    // This fixture knows its group data; content counts remain intentionally approximate in cold loading.
                    shell.SetLayout(layout with { ShowGroups = kind == EditorDocumentKind.Note });
                    layer.Visibility = Visibility.Visible; layer.Opacity = 1;
                    window.UpdateLayout(); Pump(30);
                    var checks = new List<string>();
                    void Compare(string key, FrameworkElement real, string? containerKey = null, FrameworkElement? container = null)
                    {
                        if (!real.IsVisible) return;
                        var actual = real.TransformToVisual(shell).TransformBounds(new Rect(real.RenderSize));
                        if (!actual.IntersectsWith(new Rect(shell.RenderSize))) return;
                        Require(shell.ChromeBounds.TryGetValue(key, out var expected), $"{theme}/{kind}/{culture}/{width}: missing visible {key}.");
                        // Content/group origins depend on data above them. Check chrome within its
                        // own surface, avoiding accumulated WPF pixel rounding between parent rows.
                        if (containerKey is not null && container is not null)
                        {
                            var origin = container.TransformToVisual(shell).Transform(new Point());
                            actual.Offset(-origin.X, -origin.Y);
                            var skeletonOrigin = shell.ChromeBounds[containerKey].TopLeft;
                            expected.Offset(-skeletonOrigin.X, -skeletonOrigin.Y);
                        }
                        var difference = new[] { Math.Abs(actual.X - expected.X), Math.Abs(actual.Y - expected.Y), Math.Abs(actual.Width - expected.Width), Math.Abs(actual.Height - expected.Height) }.Max();
                        checks.Add($"{key}: real={actual}; skeleton={expected}; delta={difference:F2}");
                        Require(difference <= 1.01, $"{theme}/{kind}/{culture}/{width}: {checks[^1]}");
                    }
                    var trayStyle = (Style)window.FindResource("ReorderControlTrayStyle");
                    Compare("Calendar.Panel", section);
                    Compare("Calendar.Strip", ControlTree(section).OfType<Border>().First(b => ReferenceEquals(b.Style, trayStyle)));
                    var actions = ControlTree(section).OfType<CalendarPlannerActions>().Single();
                    var actionButtons = ControlTree(actions).OfType<Button>().ToArray();
                    Compare("Calendar.Today", actionButtons[0]); Compare("Calendar.Add", actionButtons[1]);
                    var calendarItem = ControlTree(planner.CalendarControl).OfType<CalendarItem>().Single();
                    Compare("Calendar.Previous", (FrameworkElement)calendarItem.Template.FindName("PART_PreviousButton", calendarItem));
                    Compare("Calendar.Next", (FrameworkElement)calendarItem.Template.FindName("PART_NextButton", calendarItem));
                    Compare("Calendar.Modes", (FrameworkElement)VisualTreeHelper.GetParent((FrameworkElement)planner.FindName("ModeHost")));
                    Compare("Calendar.Filter", (FrameworkElement)planner.FindName("TypeFilter"));
                    var creation = ControlTree(page).OfType<DashboardCreateMenu>().Single();
                    Compare("Creation", (FrameworkElement)creation.FindName("ToggleAction"));
                    var content = (FrameworkElement)page.FindName(vm.ActiveDashboard + "DashboardContentHost");
                    Compare("Content.Strip", ControlTree(content).OfType<Border>().First(b => ReferenceEquals(b.Style, trayStyle)), "Content.Panel", content);
                    if (kind == EditorDocumentKind.Note)
                    {
                        var group = ControlTree(content).OfType<DashboardGroupHeader>().First();
                        Compare("Group.Strip", (FrameworkElement)group.Template.FindName("GroupActionsTray", group), "Group.Panel", ControlTree(content).OfType<DashboardGroupCard>().First());
                    }
                    report.Add($"PASS {theme}/{kind}/{culture}/{width}/calendar={variant.Calendar}: {checks.Count} actual control rectangles match within 1 DIP.");
                    report.AddRange(checks);
                    if (kind == EditorDocumentKind.Note && culture == "en" && width is 1280 or 440)
                    {
                        var suffix = variant.Calendar ? "" : "-groups";
                        SaveControlCapture((FrameworkElement)window.Content, $"geometry-{theme}-{width}{suffix}-loading");
                        layer.Visibility = Visibility.Collapsed;
                        SaveControlCapture((FrameworkElement)window.Content, $"geometry-{theme}-{width}{suffix}-ready");
                    }
                }
            }
        }
        finally { CloseLoadingWindow(window); }
        File.WriteAllLines(Path.Combine(_output, "geometry-checks.txt"), report);
        foreach (var entry in report.Where(e => e.StartsWith("PASS"))) Console.WriteLine(entry);
    }
}
