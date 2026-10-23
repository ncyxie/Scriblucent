using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Views;

internal static partial class Program
{
    private sealed class SidebarFixture : IDisposable
    {
        internal readonly Window Window;
        internal readonly Grid Root = new() { ClipToBounds = true };
        internal readonly Border Sidebar;
        internal readonly ColumnDefinition Column = new() { Width = new(64) };
        internal readonly Button[] Destinations;
        internal DashboardVirtualizedCardGrid? Cards;
        internal SidebarFixture(Window owner, bool wrapCards = false)
        {
            Root.SetResourceReference(Panel.BackgroundProperty, "SidebarBackground");
            Root.ColumnDefinitions.Add(Column); Root.ColumnDefinitions.Add(new());
            var source = XDocument.Load("Scriblucent/MainWindow.xaml");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var clone = new XElement(source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SidebarChrome"));
            StripHandlers(clone);
            var keys = new[] { "SidebarNavIconStyle", "SidebarActionButtonStyle", "SidebarLabelTextStyle", "SidebarNotesDashboardButtonStyle", "SidebarFlashcardsDashboardButtonStyle", "SidebarMindMapsDashboardButtonStyle", "SidebarQuizzesDashboardButtonStyle" };
            clone.AddFirst(new XElement(wpf + "Border.Resources", keys.Select(key => source.Descendants().FirstOrDefault(e => (string?)e.Attribute(x + "Key") == key)).Where(e => e is not null).Select(e => new XElement(e!))));
            Sidebar = (Border)Fragment(clone, source.Root!);
            Sidebar.IsEnabled = true;
            Sidebar.DataContext = new { IsNotMassSelectMode = true, IsNotesView = true, IsFlashcardsView = false, IsMindMapsView = false, IsQuizzesView = false };
            var backing = new Border { Margin = new(64, 10, 10, 10), CornerRadius = new(18), IsHitTestVisible = false };
            backing.SetResourceReference(Border.BackgroundProperty, "WindowBackground"); Grid.SetColumnSpan(backing, 2); Root.Children.Add(backing);
            var stage = new Grid { Name = "SidebarContentStage", Margin = new(0, 10, 10, 10) }; Grid.SetColumn(stage, 1);
            Panel.SetZIndex(stage, 3);
            var list = new ListBox { BorderThickness = new(0), Margin = new(20), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            list.SetResourceReference(Control.BackgroundProperty, "WindowBackground"); list.SetResourceReference(Control.ForegroundProperty, "TextColor");
            list.ItemsSource = Enumerable.Range(0, 20_000).Select(i => $"Synthetic note {i + 1}   /   A library with room to grow").ToArray();
            VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            FrameworkElement library = list;
            if (wrapCards)
            {
                Cards = new DashboardVirtualizedCardGrid { ItemCellSize = new(244, 164), VerticalAlignment = VerticalAlignment.Top,
                    ItemsSource = Enumerable.Range(0, 20_000).Select(i => $"A living library {i + 1}").ToArray(),
                    ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:controls="clr-namespace:Scriblucent.Controls;assembly=Scriblucent">
                            <Grid Width="220" Height="140" Margin="12">
                                <controls:CardShadowLayer/>
                                <Border Background="{DynamicResource NoteCardBackground}" BorderBrush="{DynamicResource NoteCardBorder}" BorderThickness="1" CornerRadius="14" Padding="16">
                                    <StackPanel><TextBlock Text="{Binding}" FontSize="16" FontWeight="SemiBold" Foreground="{DynamicResource TextColor}"/>
                                        <TextBlock Text="Ideas worth keeping.&#10;A little room to explore." Margin="0,12,0,12" FontSize="13" Foreground="{DynamicResource TextColorSecondary}"/>
                                        <TextBlock Text="Scriblucent / Design" FontSize="11" Foreground="{DynamicResource AccentPrimary}"/>
                                    </StackPanel>
                                </Border>
                            </Grid>
                        </DataTemplate>
                        """) };
                var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(20) };
                viewer.Content = new DashboardVirtualizationHost { Child = Cards, ScrollViewerElement = viewer,
                    HostMode = DashboardVirtualizationHostMode.PageVirtualized, MotionScope = Scriblucent.Services.DashboardMotionScope.Flashcards };
                library = viewer;
            }
            var face = new Border { CornerRadius = new(18), Child = library }; face.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
            stage.Children.Add(new StageShadowLayer()); stage.Children.Add(face); Root.Children.Add(stage); Root.Children.Add(Sidebar);
            Window = new Window { Content = Root, Owner = owner, Width = 960, Height = 620, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Window.Show(); Window.UpdateLayout(); Pump(60);
            Destinations = Descendants(Sidebar).OfType<Button>().Where(b => SidebarNavMotionBehavior.GetIsActive(b) || b.GetBindingExpression(SidebarNavMotionBehavior.IsActiveProperty) is not null).ToArray();
            SidebarNavMotionBehavior.SetIsActive(Destinations[0], true);
        }
        internal void Resize(double width) => UiMotion.AnimateSidebarChrome(Sidebar, Column, SidebarResizeMotion.VisibleWidth(Sidebar, Column.Width.Value), width);
        internal void Select(int index)
        { foreach (var button in Destinations) SidebarNavMotionBehavior.SetIsActive(button, ReferenceEquals(button, Destinations[index])); }
        public void Dispose() { Window.Close(); Pump(35); WaitForRest(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void StripHandlers(XElement root)
    {
        var events = new HashSet<string> { "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "Loaded", "Unloaded", "ValueChanged", "KeyDown", "PreviewKeyDown", "MouseDoubleClick", "LostFocus", "PreviewMouseLeftButtonDown", "MouseLeftButtonDown", "MouseLeftButtonUp", "SizeChanged", "PreviewTextInput", "PreviewMouseWheel", "MouseDown", "MouseEnter", "MouseLeave" };
        foreach (var node in root.DescendantsAndSelf())
            foreach (var attribute in node.Attributes().ToArray())
                if (events.Contains(attribute.Name.LocalName) && !attribute.Value.StartsWith("{")) attribute.Remove();
    }
    private static Border SettingsFixture()
    {
        var source = XDocument.Load("Scriblucent/Views/SettingsPanel.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var clone = new XElement(source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PanelCard"));
        StripHandlers(clone);
        clone.AddFirst(new XElement(wpf + "Border.Resources", source.Root!.Element(wpf + "UserControl.Resources")!.Elements().Select(e => new XElement(e))));
        var result = (Border)Fragment(clone, source.Root!); result.Width = 560; result.Height = 650; return result;
    }
    private static Border PanelFixture(string file)
    {
        var source = XDocument.Load($"Scriblucent/Views/{file}.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var clone = new XElement(source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PanelMotionHost"));
        StripHandlers(clone);
        clone.AddFirst(new XElement(wpf + "Border.Resources", source.Root!.Element(wpf + "UserControl.Resources")!.Elements().Select(e => new XElement(e))));
        var result = (Border)Fragment(clone, source.Root!); result.Height = 620;
        if (result.FindName("EmptyStateHost") is ContextualStatePanel state) state.Visibility = Visibility.Visible;
        return result;
    }
    private static Window StageFixture(Window owner, FrameworkElement body, string title = "A little more alive")
    {
        var heading = new TextBlock { Text = title, FontSize = 23, Margin = new(20) }; heading.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(20) };
        footer.Children.Add(new Button { Content = "Keep editing", Style = (Style)_app.FindResource("DialogSecondaryButtonStyle"), Margin = new(0, 0, 10, 0) });
        footer.Children.Add(new Button { Content = "Save", Style = (Style)_app.FindResource("DialogPrimaryButtonStyle") });
        var shell = new FeatureWindowShell { Header = heading, Body = body, Footer = footer };
        var window = new Window { Content = new AdornerDecorator { Child = shell }, Width = 820, Height = 630, Owner = owner, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowContentEnterHelper.AnimateOnLoaded(window); return window;
    }
    private static void RecordFifthPass(Scene scene, string theme)
    {
        var suffix = theme.ToLowerInvariant();
        using (var sidebar = new SidebarFixture(scene.Window))
        {
            SaveGif(CaptureFeedback(sidebar.Root, () => sidebar.Resize(232), 30,
                new() { [4] = () => sidebar.Select(1), [12] = () => sidebar.Resize(64), [16] = () => sidebar.Select(3), [20] = () => sidebar.Resize(232) }), $"sidebar-edge-and-selection-{suffix}.gif");
            ReducedMotionPreferences.Configure(MotionPreference.Reduced);
            SaveGif(CaptureFeedback(sidebar.Root, () => sidebar.Resize(64), 9,
                new() { [4] = () => sidebar.Select(0) }), $"reduced-sidebar-{suffix}.gif");
            ReducedMotionPreferences.Configure(MotionPreference.Full);
        }
        foreach (var kind in Enum.GetValues<StateIllustrationKind>())
        {
            var panel = new ContextualStatePanel { IllustrationKind = kind, Title = kind switch { StateIllustrationKind.Trash => "Room for a fresh start", StateIllustrationKind.Selection => "Build your next quiz", _ => "Your story starts here" }, Description = "Useful details, with a little character." };
            var window = StageFixture(scene.Window, panel);
            SaveGif(Capture((FrameworkElement)window.Content, window.Show, 650), $"context-state-{kind.ToString().ToLowerInvariant()}-{suffix}.gif");
            WindowContentEnterHelper.AllowImmediateClose(window); window.Close(); Pump(40);
        }
        var disclosure = new Expander { Header = "Details that matter", Content = new TextBlock { Text = "Created today\n4 linked notes\nEnglish\nA place for the next idea", Margin = new(0, 12, 0, 0), FontSize = 14, Height = 110 }, Margin = new(24) };
        var disclosureWindow = StageFixture(scene.Window, new StackPanel { Children = { disclosure } }); disclosureWindow.Show(); Pump(380);
        SaveGif(CaptureFeedback((FrameworkElement)disclosureWindow.Content, () => disclosure.IsExpanded = true, 26,
            new() { [10] = () => disclosure.IsExpanded = false, [13] = () => disclosure.IsExpanded = true }), $"disclosure-reversal-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(disclosureWindow); disclosureWindow.Close(); Pump(40);
        var settings = SettingsFixture(); var settingsWindow = new Window { Content = settings, Owner = scene.Window, Width = 610, Height = 700, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        SaveGif(Capture(settings, () => { settingsWindow.Show(); ArrivalGroups.Start(settings, true); }, 600), $"settings-staged-{suffix}.gif");
        settingsWindow.Close(); Pump(40);
        foreach (var panelName in new[] { "TrashPanel", "NoteSchedulePanel" })
        {
            var panel = PanelFixture(panelName);
            var panelWindow = new Window { Content = panel, Owner = scene.Window, Width = 940, Height = 690, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            SaveGif(Capture(panel, () => { panelWindow.Show(); ArrivalGroups.Start(panel, true); }, 600), $"{panelName.ToLowerInvariant()}-staged-{suffix}.gif");
            panelWindow.Close(); Pump(40);
        }
        var stats = new FlashcardStatisticsWindow(() => new FlashcardSetDocument { Title = "Material and motion", Cards = Enumerable.Range(0, 24).Select(i => new FlashcardItem { Question = $"Question {i}", Answer = "Answer", DueDate = DateTime.Today.AddDays(i % 4 - 1), IntervalDays = i % 3 * 10 }).ToList() })
            { Owner = scene.Window, ShowInTaskbar = false };
        SaveGif(Capture((FrameworkElement)stats.Content, stats.Show, 650), $"statistics-staged-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(stats); stats.Close(); Pump(40);
        var rows = new ObservableCollection<string>(); var list = new ListBox { ItemsSource = rows, Margin = new(24), Height = 180, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ItemPresenceMotionBehavior.SetIsEnabled(list, true); ItemPresenceMotionBehavior.SetEntryOffsetX(list, -8); ItemPresenceMotionBehavior.SetRise(list, 0); ItemPresenceMotionBehavior.SetEnterDuration(list, 220);
        var listWindow = StageFixture(scene.Window, list, "A selection comes together"); listWindow.Show(); Pump(380);
        SaveGif(CaptureFeedback((FrameworkElement)listWindow.Content, () => rows.Add("Material and motion"), 25,
            new() { [5] = () => rows.Add("Design should feel alive"), [12] = () => rows.RemoveAt(0), [18] = rows.Clear }), $"selection-list-continuity-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(listWindow); listWindow.Close(); Pump(40);
        var history = new EditHistoryWindow(Enumerable.Range(0, 4).Select(i => new NoteEditHistoryEntry
            { Timestamp = DateTime.UtcNow.AddHours(-i), Content = "A saved idea, with useful context and a clear next step." }))
            { Owner = scene.Window, ShowInTaskbar = false };
        history.Show(); Pump(380);
        var historyList = (ListBox)history.FindName("HistoryListBox");
        var row = Descendants(history).OfType<Border>().First(b => StateSurfaceMotionBehavior.GetIsEnabled(b));
        SaveGif(CaptureFeedback((FrameworkElement)history.Content, () => historyList.SelectedIndex = 2, 24,
            new() { [5] = () => row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }),
                [12] = () => row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }),
                [16] = () => historyList.SelectedIndex = 0 }), $"history-row-feedback-{suffix}.gif");
        var tooltip = new ToolTip { Content = "Useful context, close to the action", PlacementTarget = historyList, Placement = System.Windows.Controls.Primitives.PlacementMode.Center };
        tooltip.Style = (Style)_app.FindResource(typeof(ToolTip));
        SaveGif(Capture(tooltip, () => tooltip.IsOpen = true, 220), $"themed-tooltip-{suffix}.gif");
        tooltip.IsOpen = false;
        WindowContentEnterHelper.AllowImmediateClose(history); history.Close(); Pump(40);
        var save = new Button { Content = "Save note", Width = 140, Height = 40, Style = (Style)_app.FindResource("EditorPrimarySaveButtonStyle") };
        var saveWindow = StageFixture(scene.Window, new Grid { Children = { save } }, "Editor action feedback"); saveWindow.Show(); Pump(380);
        var saveFace = (InteractionSurface)save.Template.FindName("SaveButtonBorder", save);
        SaveGif(CaptureFeedback((FrameworkElement)saveWindow.Content, () => save.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }), 24,
            new() { [5] = () => saveFace.AnimateState(true, true), [9] = () => saveFace.AnimateState(true, false),
                [15] = () => save.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }) }), $"editor-save-feedback-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(saveWindow); saveWindow.Close(); Pump(40);
        ReducedMotionPreferences.Configure(MotionPreference.Reduced);
        var reduced = StageFixture(scene.Window, new ContextualStatePanel { IllustrationKind = StateIllustrationKind.Trash, Title = "Room for a fresh start", Description = "Simple feedback when you want less movement." });
        SaveGif(Capture((FrameworkElement)reduced.Content, reduced.Show, 250), $"reduced-staging-and-context-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(reduced); reduced.Close(); Pump(40); ReducedMotionPreferences.Configure(MotionPreference.Full);
        WaitForRest();
    }
    private static void BenchmarkFifthPass(Scene scene, string theme, MotionPreference preference)
    {
        using var sidebar = new SidebarFixture(scene.Window);
        Measure($"{theme}/{preference}/sidebar-edge-open", scene, () => sidebar.Resize(232), 330);
        Measure($"{theme}/{preference}/sidebar-edge-close", scene, () => sidebar.Resize(64), 330);
        Measure($"{theme}/{preference}/sidebar-edge-reversal", scene, () =>
        {
            sidebar.Resize(232);
            var reversal = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            reversal.Tick += (_, _) => { reversal.Stop(); sidebar.Resize(64); }; reversal.Start();
        }, 400);
        var context = new ContextualStatePanel { Title = "Your story starts here", Description = "A useful next step." };
        var window = StageFixture(scene.Window, context); window.Show(); Pump(380);
        Measure($"{theme}/{preference}/context-state-arrival", scene, () => { context.Visibility = Visibility.Collapsed; context.Visibility = Visibility.Visible; }, 380);
        Measure($"{theme}/{preference}/staged-feature-groups", scene, () => ArrivalGroups.Start((FrameworkElement)window.Content, true), 340);
        WindowContentEnterHelper.AllowImmediateClose(window); window.Close(); Pump(40);
    }
}
