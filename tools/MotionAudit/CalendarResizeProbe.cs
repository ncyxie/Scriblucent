using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;

internal static partial class Program
{
    private sealed class CalendarMeasureCounter : FrameworkElement
    {
        internal int Count;
        protected override Size MeasureOverride(Size available) { Count++; return new Size(140, 32); }
    }

    private static void ProbeCalendarResize()
    {
        var checks = new List<string>(); var failures = new List<string>();
        void Fail(string message) { if (!failures.Contains(message)) { failures.Add(message); Console.WriteLine(message); } }
        var header = new CalendarPlannerHeaderPanel(); var counter = new CalendarMeasureCounter();
        header.Children.Add(new Border { Width = 244, Height = 32 }); header.Children.Add(counter);
        for (var width = 440d; width <= 560; width += 4)
        { header.Measure(new Size(width, double.PositiveInfinity)); header.Arrange(new Rect(0, 0, width, header.DesiredSize.Height)); }
        checks.Add($"Agenda filter: {counter.Count} measures for 31 width changes.");
        if (counter.Count > 31) Fail("Agenda filter is measured repeatedly within each width change.");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load("Scriblucent/MainWindow.xaml");
        foreach (var language in new[] { "en", "lt" })
        foreach (var dashboard in new[] { "Notes", "Flashcards", "MindMaps", "Quizzes" })
        {
            LoadNativeControlResources("Light"); LocalizationService.SetCulture(language); MotionPolicy.Configure(MotionPreference.Full, false);
            var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
            var section = new XElement(source.Descendants().Single(node => ((string?)node.Attribute(x + "Name"))?.EndsWith("CalendarSectionContainer") == true));
            var wrapper = new XElement(wpf + "Grid", new XAttribute("Margin", "16"),
                new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()),
                new XElement(wpf + "Grid.RowDefinitions", new XElement(wpf + "RowDefinition", new XAttribute("Height", "Auto"))), section);
            StripFixtureEvents(wrapper); var content = Fragment(wrapper, source.Root!);
            var fixture = new CalendarDashboardFixture(); fixture.Populate(); content.DataContext = fixture;
            var root = new Grid { UseLayoutRounding = true };
            var column = new ColumnDefinition { Width = new GridLength(64) };
            root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new ColumnDefinition());
            var sidebar = new SidebarChrome(); SidebarResizeMotion.SetIsEnabled(sidebar, true); root.Children.Add(sidebar);
            var scroll = new ScrollViewer { Name = "SidebarContentStage", Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 1); root.Children.Add(scroll);
            var window = new Window { Content = root, Width = 1080, Height = 720, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            try
            {
                window.Show(); Pump(300);
                var view = ControlTree(content).OfType<CalendarPlannerView>().Single();
                var body = (DashboardCalendarPanel)view.FindName("Body");
                var filter = (ComboBox)view.FindName("TypeFilter");
                void CheckBounds(string phase)
                {
                    var bounds = view.CalendarControl.TransformToAncestor(body).TransformBounds(new Rect(view.CalendarControl.RenderSize));
                    if (bounds.Left < -.6 || bounds.Right > body.ActualWidth + .6)
                        Fail($"{dashboard}/{language}/{phase}: month extends beyond its {body.ActualWidth:F1} DIP body ({bounds.Left:F1}..{bounds.Right:F1}).");
                    var agenda = (FrameworkElement)body.Children[1];
                    var agendaBounds = agenda.TransformToAncestor(body).TransformBounds(new Rect(agenda.RenderSize));
                    if (agendaBounds.Bottom > body.ActualHeight + 1) Fail($"{dashboard}/{language}/{phase}: agenda extends below the measured body.");
                    var plannerHeader = (CalendarPlannerHeaderPanel)view.FindName("PlannerHeader");
                    var expectedHeight = DashboardCalendarPanel.BodyHeight(body.ActualWidth, plannerHeader.RequiredHeight(agenda.ActualWidth) - 32);
                    if (body.ActualHeight < expectedHeight - 1) Fail($"{dashboard}/{language}/{phase}: body does not reserve its actual header's wrapped height.");
                }
                foreach (var width in new[] { 1080d, 940, 840, 780, 640, 560 })
                {
                    window.Width = width; Pump(40);
                    foreach (var target in new[] { 232d, 64d })
                    {
                        EventHandler frame = (_, _) => CheckBounds($"{width}/{target}");
                        CompositionTarget.Rendering += frame;
                        try { UiMotion.AnimateSidebarChrome(sidebar, column, column.ActualWidth, target, 280); Pump(380); }
                        finally { CompositionTarget.Rendering -= frame; }
                        CheckBounds($"settled-{width}/{target}");
                        Require(Math.Abs(column.ActualWidth - target) < 1, "Sidebar did not settle after the calendar resized.");
                    }
                }
                window.Width = 520; column.Width = new GridLength(232); Pump(80); CheckBounds("narrow");
                if (dashboard == "Notes") SaveControlCapture(root, "calendar-sidebar-narrow-" + language);
                window.Width = 1080; column.Width = new GridLength(232); filter.FontSize = 20; Pump(80); CheckBounds("larger-filter-text");
                filter.FontSize = 14;
                foreach (var scale in new[] { 1.25, 1.5, 2d })
                {
                    root.LayoutTransform = new ScaleTransform(scale, scale); Pump(60); CheckBounds("scaled-layout-" + scale);
                }
                root.LayoutTransform = Transform.Identity; Pump(60);
                var layoutEvents = 0; EventHandler layout = (_, _) => layoutEvents++;
                root.LayoutUpdated += layout; Pump(120); root.LayoutUpdated -= layout;
                Require(layoutEvents <= 3, "Idle calendar continues relaying out after the sidebar settles.");
                checks.Add($"{dashboard}/{language}: resized sidebar, compact/scaled layouts, larger filter text, and idle layout checked.");
            }
            finally { window.Close(); Pump(30); }
        }
        File.WriteAllLines(Path.Combine(_output, "calendar-resize.txt"), checks.Concat(failures));
        foreach (var check in checks) Console.WriteLine(check);
        Require(failures.Count == 0, "Calendar layout overflows or performs redundant width measurements.");
    }
}
