using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using Scriblucent.Animations;

internal static partial class Program
{
    private static void ProbePopupBounds()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var anchor = new Button { Content = "Sections", Width = 180, Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0) };
            var root = new Grid { Children = { anchor } };
            var window = new Window { Width = 820, Height = 620, Content = root, ShowInTaskbar = false };
            window.Show(); _app.MainWindow = window; Pump(80);

            var source = XDocument.Load("Scriblucent/MainWindow.xaml");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var popup = (Popup)Fragment(source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "DashboardSectionsPopup"), source.Root!);
            popup.DataContext = new DashboardPopupProbeModel();
            popup.PlacementTarget = anchor; root.Children.Add(popup); popup.IsOpen = true; Pump(100);
            var shell = (FrameworkElement)popup.Child;
            Require(shell.ActualWidth > 64 && shell.ActualHeight > 64, "Dashboard menu did not lay out.");
            CheckGutter(shell);
            SaveControlCapture(shell, "dashboard-sections-boundary-" + theme);
            popup.IsOpen = false; root.Children.Remove(popup);

            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            menu.Items.Add(new MenuItem { Header = "A native context menu item" }); menu.IsOpen = true; Pump(100);
            CheckGutter(menu);
            menu.IsOpen = false;

            var calendar = new Calendar { Style = (Style)window.FindResource("PopupCalendarControlStyle") };
            var calendarPopup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, AllowsTransparency = true, Child = calendar };
            calendarPopup.IsOpen = true; Pump(100); CheckGutter(calendar); calendarPopup.IsOpen = false;

            var combo = new ComboBox { Width = 200, MaxDropDownHeight = 170, SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 100, 0, 0),
                ItemsSource = Enumerable.Range(0, 80).Select(i => i < 40 ? $"Short label {i}" : $"A longer library label that should be completely readable, item {i}").ToArray() };
            combo.Style = (Style)window.FindResource("ChromeComboBoxStyle"); root.Children.Add(combo); window.UpdateLayout();
            combo.IsDropDownOpen = true; Pump(100);
            var listPopup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var viewer = ControlTree(listPopup.Child).OfType<ScrollViewer>().First();
            var initial = viewer.RenderSize;
            viewer.ScrollToEnd(); Pump(150);
            var label = ControlTree(listPopup.Child).OfType<TextBlock>().First(t => t.Text.StartsWith("A longer library"));
            Require(label.ActualHeight > label.FontSize * 1.8 && label.ActualWidth <= viewer.ViewportWidth,
                "Long native labels did not fit within the fixed viewport.");
            Require(Math.Abs(initial.Width - viewer.ActualWidth) < .5 && Math.Abs(initial.Height - viewer.ActualHeight) < .5,
                "Dropdown size changed while scrolling.");
            SaveControlCapture((FrameworkElement)listPopup.Child, "dropdown-wrapped-labels-" + theme);
            combo.IsDropDownOpen = false; window.Close(); Pump(80);
            checks.Add(theme + ": dashboard/context/calendar menu gutters reject hits one DIP outside the face; long labels wrap without viewport resize.");
        }
        File.WriteAllLines(Path.Combine(_output, "popup-bounds-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private sealed class DashboardPopupProbeModel
    {
        public bool IsCalendarSectionVisible { get; set; } = true;
        public bool IsFlashcardDashboardGroupsSectionVisible { get; set; }
        public bool IsFlashcardDashboardUngroupedSectionVisible { get; set; }
        public bool IsFlashcardGroupsSectionVisible { get; set; }
        public bool IsFlashcardsView { get; set; }
        public bool IsGroupsSectionVisible { get; set; } = true;
        public bool IsMindMapDashboardGroupsSectionVisible { get; set; }
        public bool IsMindMapDashboardUngroupedSectionVisible { get; set; }
        public bool IsMindMapGroupsSectionVisible { get; set; }
        public bool IsMindMapsView { get; set; }
        public bool IsNotesDashboardSectionVisible { get; set; } = true;
        public bool IsNotesView { get; set; } = true;
        public bool IsQuizDashboardGroupsSectionVisible { get; set; }
        public bool IsQuizDashboardUngroupedSectionVisible { get; set; }
        public bool IsQuizGroupsSectionVisible { get; set; }
        public bool IsQuizzesView { get; set; }
        public bool IsRecentSectionVisible { get; set; } = true;
        public bool IsUngroupedSectionVisible { get; set; } = true;
    }

    private static void CheckGutter(FrameworkElement shell)
    {
        foreach (var point in new[] { new Point(31, shell.ActualHeight / 2), new Point(shell.ActualWidth - 31, shell.ActualHeight / 2),
            new Point(shell.ActualWidth / 2, 31), new Point(shell.ActualWidth / 2, shell.ActualHeight - 31) })
            Require(shell.InputHitTest(point) is null, "Invisible shadow gutter still receives input at " + point);
        Require(shell.InputHitTest(new Point(shell.ActualWidth / 2, shell.ActualHeight / 2)) is not null,
            "Visible menu face lost its hit target.");
    }
}
