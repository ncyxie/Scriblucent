using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Converters;

internal static partial class Program
{
    public sealed class GroupPageFixture
    {
        public ObservableCollection<object> NoteGroups { get; } = new();
        public ObservableCollection<object> FlashcardSetGroups { get; } = new();
        public ObservableCollection<object> MindMapGroups { get; } = new();
        public ObservableCollection<object> QuizGroups { get; } = new();
        public bool HasGroups => NoteGroups.Count > 0;
        public bool HasFlashcardSetGroups => FlashcardSetGroups.Count > 0;
        public bool HasMindMapGroups => MindMapGroups.Count > 0;
        public bool HasQuizGroups => QuizGroups.Count > 0;
        public bool IsNotMassSelectMode => true;
        public bool IsMassSelectMode => false;
        public bool IsRecentSectionVisible => false;
        public bool IsCalendarSectionVisible => false;
        public bool IsCalendarFirst => true;
        public bool ShowNotesEmptyDashboard => false;
        public bool ShowFlashcardsEmptyDashboard => false;
        public bool ShowMindMapsEmptyDashboard => false;
        public bool ShowQuizzesEmptyDashboard => false;
        public bool ShouldShowNotesDashboardContent => true;
        public bool ShouldShowFlashcardsDashboardContent => true;
        public bool ShouldShowMindMapsDashboardContent => true;
        public bool ShouldShowQuizzesDashboardContent => true;
        public bool IsNotesDashboardSectionVisible => true;
        public bool IsGroupsSectionVisible => true;
        public bool IsFlashcardGroupsSectionVisible => true;
        public bool IsMindMapGroupsSectionVisible => true;
        public bool IsQuizGroupsSectionVisible => true;
        public bool IsFlashcardDashboardGroupsSectionVisible => true;
        public bool IsMindMapDashboardGroupsSectionVisible => true;
        public bool IsQuizDashboardGroupsSectionVisible => true;
        public bool IsGroupsFirst => true;
        public bool IsFlashcardDashboardGroupsFirst => true;
        public bool IsMindMapDashboardGroupsFirst => true;
        public bool IsQuizDashboardGroupsFirst => true;
        public bool IsNotesDashboardSectionExpanded => true;
        public bool IsGroupsSectionExpanded => true;
        public bool IsFlashcardGroupsSectionExpanded => true;
        public bool IsMindMapGroupsSectionExpanded => true;
        public bool IsQuizGroupsSectionExpanded => true;
        public bool IsFlashcardDashboardGroupsSectionExpanded => true;
        public bool IsMindMapDashboardGroupsSectionExpanded => true;
        public bool IsQuizDashboardGroupsSectionExpanded => true;
    }

    private static void ProbeGroupPageFinishing(string dashboard, string theme, XDocument source, XDocument main, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var wrapper = new XElement(wpf + "Grid",
            new XElement(wpf + "Grid.Resources", main.Root!.Element(wpf + "Window.Resources")!.Elements()),
            new XElement(source.Root!.Element(wpf + "Grid")!));
        StripFixtureEvents(wrapper);
        var root = (Grid)Fragment(wrapper, source.Root!);
        var fixture = new GroupPageFixture();
        var groups = dashboard switch { "Notes" => fixture.NoteGroups, "Flashcards" => fixture.FlashcardSetGroups, "MindMaps" => fixture.MindMapGroups, _ => fixture.QuizGroups };
        for (var i = 0; i < 3; i++) groups.Add(GroupFixture(dashboard).Model);
        root.DataContext = fixture;
        var scene = new Grid { Background = (Brush)_app.FindResource("WindowBackground"), Children = { root } };
        var window = new Window { Content = scene, Width = 1000, Height = 680, ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true };
        _app.MainWindow = window;
        try
        {
            window.Show(); Pump(150); window.UpdateLayout();
            DashboardGroupHeader[] Headers() => ControlTree(root).OfType<DashboardGroupHeader>().ToArray();
            ItemsControl Owner() => ControlTree(root).OfType<ItemsControl>().Single(c => ReferenceEquals(c.ItemsSource, groups));
            void CheckOrder()
            {
                Pump(30); window.UpdateLayout();
                var visible = Owner().Items.Cast<object>().ToList();
                var headers = Headers();
                Require(headers.Length == visible.Count, $"{dashboard}/{theme}: actual page did not realize its groups: {headers.Length}/{visible.Count}.");
                foreach (var header in headers)
                {
                    var actions = ((StackPanel)header.Content).Children.OfType<Button>().ToArray();
                    Require(((StackPanel)header.Content).Children.OfType<ItemReorderControls>().Count() == 1, "Group headers do not expose one movement control.");
                    Require(actions.All(action => action.Focusable && action.Template is not null), "Group controls cannot receive focus or lost their templates.");
                    var tray = (Border)header.Template.FindName("GroupActionsTray", header);
                    var title = (FrameworkElement)header.Template.FindName("TitleArea", header);
                    Require(Math.Abs(tray.TranslatePoint(new(), header).X) < .01, "Group actions are not anchored on the left.");
                    var trayButtons = ControlTree(tray).OfType<Button>().ToHashSet();
                    Require(trayButtons.Count == 6 && actions.All(trayButtons.Contains), "Group actions lost their shared surface.");
                    Require(header.ActualWidth < DashboardGroupHeaderPanel.CompactWidth || tray.TranslatePoint(new(tray.ActualWidth, 0), header).X <= title.TranslatePoint(new(), header).X,
                        "Group controls are split around the title.");
                }
            }
            CheckOrder(); groups.Move(0, 2); CheckOrder();
            groups.Insert(1, GroupFixture(dashboard).Model); CheckOrder();
            groups.RemoveAt(0); CheckOrder();
            var hidden = groups[1]; Owner().Items.Filter = item => !ReferenceEquals(item, hidden); CheckOrder();
            Owner().Items.Filter = null; CheckOrder();
            groups[0] = GroupFixture(dashboard).Model; CheckOrder();
            while (groups.Count > 1) groups.RemoveAt(groups.Count - 1);
            CheckOrder();
            groups.Add(GroupFixture(dashboard).Model); groups.Add(GroupFixture(dashboard).Model); CheckOrder();

            var first = Headers()[0];
            var wash = (Border)first.Template.FindName("TitleWash", first);
            var title = (FrameworkElement)first.Template.FindName("TitleArea", first);
            first.DataContext.GetType().GetProperty("Name")!.SetValue(first.DataContext, "A"); Pump(30); window.UpdateLayout();
            var shortWash = wash.ActualWidth;
            first.DataContext.GetType().GetProperty("Name")!.SetValue(first.DataContext, "A substantially longer collection name to exercise a bounded coloured title"); Pump(30); window.UpdateLayout();
            Require(wash.ActualWidth > shortWash && wash.ActualWidth <= Math.Min(280, title.ActualWidth) + .8,
                $"The title wash is oversized or does not follow its content: short={shortWash}, long={wash.ActualWidth}, title={title.ActualWidth}.");
            SaveControlCapture(scene, $"finishing-{dashboard}-{theme}-wide");

            var colourButton = ((StackPanel)first.Content).Children.OfType<Button>().Single(button => button.ContextMenu is { } colourMenu && GroupColourMenuBehavior.GetIsEnabled(colourMenu));
            var menu = colourButton.ContextMenu;
            Require(menu is not null && GroupColourMenuBehavior.GetIsEnabled(menu), "Group colour menu lost its selection refresh.");
            UiMotion.OpenContextMenu(menu!, colourButton); Pump(100); menu!.UpdateLayout();
            var choices = menu.Items.OfType<MenuItem>().ToArray();
            Require(choices.Length == 9 && choices.Count(c => c.IsChecked) == 1, "Colour menu does not mark exactly one saved selection.");
            Require(choices.All(c => c.FontSize == 14 && ReferenceEquals(c.Template, _app.FindResource("MenuItemLeafTemplate"))),
                "Colour choices inherited native WPF typography or a native menu template.");
            foreach (var choice in choices)
            {
                var swatch = ControlTree(choice).OfType<Border>().Single(b => b.Width == 12 && b.Height == 12);
                var expected = (SolidColorBrush)new GroupAccentConverter().Convert([choice.Tag, first.Background, first.BorderBrush], typeof(Brush), "", System.Globalization.CultureInfo.InvariantCulture);
                Require(swatch.Background is SolidColorBrush actual && actual.Color == expected.Color && actual.IsFrozen && swatch.CornerRadius == new CornerRadius(4), "Menu swatch differs from the group accent palette.");
            }
            SaveControlCapture(menu, $"finishing-{dashboard}-{theme}-colour-menu");
            var selected = choices.Single(c => c.IsChecked);
            selected.SetCurrentValue(MenuItem.IsCheckedProperty, false);
            selected.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, selected)); Pump(30);
            Require(selected.IsChecked, "Reselecting the current colour removed its checkmark.");
            first.DataContext.GetType().GetMethod("SetBackground")!.Invoke(first.DataContext, ["#FFF1F2"]); Pump(30);
            Require(choices.Count(c => c.IsChecked) == 1 && choices.Single(c => c.IsChecked).Tag as string == "#FFF1F2", "Colour selection failed to follow a changed saved value.");
            menu.IsOpen = false; Pump(30);

            ProbeGroupDisclosure(root, groups, first, dashboard, theme, scene, window);

            var viewer = ControlTree(root).OfType<ScrollViewer>().Single(v => v.Name == dashboard + "ScrollViewer");
            var creationMenu = ControlTree(root).OfType<DashboardCreateMenu>().Single();
            Require(!creationMenu.IsOpen && !creationMenu.CreateButton.IsVisible && !creationMenu.QuickAddButton.IsVisible && !creationMenu.ImportButton.IsVisible,
                "Actual dashboard did not start with one resting creation button.");
            foreach (var width in new[] { 1000d, 420d })
            {
                window.Width = width; window.UpdateLayout(); Pump(40); viewer.ScrollToBottom(); Pump(60); window.UpdateLayout();
                Require(viewer.ScrollableHeight > 0, "Group clearance fixture did not exercise scrolling.");
                var last = Headers().OrderBy(h => h.TranslatePoint(new(), root).Y).Last();
                var face = ControlTree(root).OfType<Border>().Single(b => ReferenceEquals(b.Tag, last.DataContext) && CardSurfaceMotionBehavior.GetProfile(b) == CardSurfaceMotionProfile.GroupSlide);
                var bottom = face.TranslatePoint(new(0, face.ActualHeight), root).Y;
                var floatingTop = creationMenu.LauncherButton.TranslatePoint(new(), root).Y;
                Require(bottom <= floatingTop - 12, $"{dashboard}/{theme}: last group remains behind floating actions: bottom={bottom}, actions={floatingTop}.");
                foreach (var header in Headers())
                {
                    var actions = ((StackPanel)header.Content).Children.OfType<Button>().ToArray();
                    var name = (FrameworkElement)header.Template.FindName("TitleArea", header);
                    var wrapped = header.ActualWidth < DashboardGroupHeaderPanel.CompactWidth;
                    var titleY = name.TranslatePoint(new(), header).Y;
                    Require(Math.Abs(titleY) < .01,
                        $"Compact header reordered its title/action rows: {dashboard}/{theme}, width={header.ActualWidth}, height={header.ActualHeight}, title Y={titleY}.");
                    var ordering = ((StackPanel)header.Content).Children.OfType<ItemReorderControls>().Single();
                    var orderingEdge = ordering.TranslatePoint(new(ordering.ActualWidth, 0), header).X;
                    var managementStart = actions[0].TranslatePoint(new(), header);
                    Require(managementStart.X >= orderingEdge + 7 && managementStart.X + actions.Sum(b => b.ActualWidth + b.Margin.Right) <= header.ActualWidth + .01,
                        "Group management overlaps movement controls or overflows the header.");
                    Require(wrapped ? managementStart.Y >= 44 : managementStart.X + actions.Sum(b => b.ActualWidth + b.Margin.Right) <= name.TranslatePoint(new(), header).X,
                        "Group actions did not stay together before the title.");
                }
                SaveControlCapture(scene, $"finishing-{dashboard}-{theme}-{(width > 600 ? "bottom" : "compact-bottom")}");
            }
            var beforeUnload = Headers();
            var page = root.Children[0]; root.Children.Remove(page); Pump(30);
            Require(beforeUnload.All(h => !h.IsLoaded), "Removed group headers remained loaded.");
            root.Children.Add(page); Pump(70); CheckOrder();
            checks.Add($"PASS finishing {dashboard}/{theme}: full page, left-aligned shared controls, one reorder grip through filtering/reload, proportional wash, palette/checkmarks and floating-button scroll clearance.");
            Console.WriteLine(checks[^1]);
        }
        finally { window.Close(); Pump(30); }
    }
}
