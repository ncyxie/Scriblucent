using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeWorkspaces()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var document = new FlashcardSetDocument { Title = "Memory and learning", Cards = Enumerable.Range(0, 1000)
                .Select(i => new FlashcardItem { Question = "Practice " + i + ": Explain retrieval and spaced repetition",
                    Category = "Learning science", Answer = "Recall strengthens memory.", IsKnown = i % 3 == 0, IsUnknown = i % 3 != 0 }).ToList() };
            var quiz = new QuizDocument { Title = "Learning science", Attempts = Enumerable.Range(0, 1000)
                .Select(i => new QuizAttempt { QuizTitle = "Learning science", Date = new DateTime(2026, 10, 8, 14, 30, 0).AddDays(-i),
                    CorrectCount = i % 3 == 0 ? 5 : 9, TotalQuestions = 10, TimeTaken = TimeSpan.FromMinutes(4), PassingScorePercent = 70 }).ToList() };
            foreach (var window in new Window[] {
                new FlashcardStudyHistoryWindow(() => document, () => { }), new QuizAttemptHistoryWindow(quiz),
                new EditHistoryWindow(Enumerable.Range(0, 1000).Select(i => new NoteEditHistoryEntry {
                    Timestamp = new DateTime(2026, 10, 8, 14, 30, 0, DateTimeKind.Utc).AddMinutes(-i),
                    Content = "Memory and learning\nRetrieval practice strengthens long-term memory. Space your practice across several days." })) })
            {
                window.Show(); Pump(100); window.UpdateLayout(); CheckOwnedChrome(window);
                var shell = ControlTree(window).OfType<FeatureWindowShell>().Single();
                Require(ReferenceEquals(shell.Background, window.FindResource("WorkspaceBackdrop")), "History canvas differs from the shared workspace.");
                CheckWorkspaceCaption(window);
                var list = ControlTree(window).OfType<ItemsControl>().First(c => c.Items.Count == 1000);
                Require(ControlTree(list).OfType<ContentPresenter>().Count() < 120, "History virtualization was lost.");
                SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-populated");
                var scroll = ControlTree(list).OfType<ScrollViewer>().First();
                scroll.ScrollToVerticalOffset(1200); Pump(100); window.UpdateLayout();
                Require(scroll.VerticalOffset > 0, "History cannot scroll.");
                SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-scrolled");
                var tiles = ControlTree(list).OfType<DashboardTileHost>().Where(t => t.IsVisible).ToArray();
                var heights = tiles.Select(t => t.ActualHeight).ToArray();
                foreach (var tile in tiles) tile.IsBitmapMode = true;
                if (tiles.Length > 0)
                {
                    Pump(450); window.UpdateLayout();
                    for (var i = 0; i < tiles.Length; i++)
                    {
                        var image = ControlTree(tiles[i]).OfType<Image>().First();
                        Require(image.Source is BitmapSource, "History tile did not resolve.");
                        Require(Math.Abs(tiles[i].ActualHeight - heights[i]) <= 1,
                            $"{window.GetType().Name}/{tiles[i].RendererKey}: history height {heights[i]} -> {tiles[i].ActualHeight}; bitmap {((BitmapSource)image.Source).PixelWidth}x{((BitmapSource)image.Source).PixelHeight}, DPI {((BitmapSource)image.Source).DpiX}; width {tiles[i].ActualWidth}.");
                    }
                    SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-cached");
                    foreach (var tile in tiles) tile.IsBitmapMode = false;
                    Pump(40);
                }
                window.Width = window.MinWidth; window.UpdateLayout(); Pump(40);
                SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-compact");
                window.Close(); Pump(30);
            }
            foreach (var window in new Window[] { new FlashcardStudyHistoryWindow(() => new FlashcardSetDocument(), () => { }),
                new QuizAttemptHistoryWindow(new QuizDocument()), new EditHistoryWindow([]) })
            {
                window.Show(); Pump(60); SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-empty");
                window.Close(); Pump(30);
            }
            ProbeWorkspaceTabs(theme, language, checks);
            checks.Add($"{theme}/{language}: three compiled history views retain 1,000-item virtualization, scrolling, compact layout and empty states. Fixtures do not invoke delete/restore against user data.");
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "A workspace fixture queued a settings write.");
        File.WriteAllLines(Path.Combine(_output, "workspace-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static FrameworkElement WorkspaceEditorFixture()
    {
        // Production editor markup and native document engine, without editor services/storage/AI startup.
        var source = XDocument.Load("Scriblucent/NoteEditorWindow.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var body = new XElement(source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RootGrid"));
        var resources = new XElement(wpf + "Grid.Resources", source.Root!.Element(wpf + "Window.Resources")!.Elements());
        var wrapper = new XElement(wpf + "Grid", resources, body);
        foreach (var node in wrapper.DescendantsAndSelf())
        {
            var type = typeof(Control).Assembly.GetType("System.Windows.Controls." + node.Name.LocalName)
                ?? typeof(Control).Assembly.GetType("System.Windows.Controls.Primitives." + node.Name.LocalName);
            if (type is null && node.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                var clrNamespace = node.Name.NamespaceName["clr-namespace:".Length..].Split(';')[0];
                type = typeof(TableSizePicker).Assembly.GetType(clrNamespace + "." + node.Name.LocalName);
            }
            foreach (var attribute in node.Attributes().Where(a => a.Name.NamespaceName.Length == 0 &&
                type?.GetEvent(a.Name.LocalName) is not null).ToArray()) attribute.Remove();
        }
        var content = Fragment(wrapper, source.Root);
        return content;
    }

    private static void CheckWorkspaceCaption(Window window)
    {
        var captionProperty = (DependencyProperty)typeof(ShellWindowChrome).GetField("CaptionHostProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var caption = (Border)window.GetValue(captionProperty);
        Require(ReferenceEquals(caption.Background, window.FindResource("WorkspaceBackdrop")), "Workspace caption retained the old frame brush.");
    }

    private static void ProbeWorkspaceTabs(string theme, string language, List<string> checks)
    {
        foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            MotionPolicy.Configure(mode, false);
            var window = new Scriblucent.NoteEditorTabsWindow();
            var tabs = (TabControl)window.FindName("EditorTabs");
            var originals = new List<TabItem>();
            for (var i = 0; i < 12; i++)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Notes,
                    Style = (Style)window.FindResource("EditorTabIconStyle") });
                header.Children.Add(new TextBlock { Text = i == 0 ? "Memory and learning" : "Research notes " + i + " — a deliberately long document name",
                    Style = (Style)window.FindResource("EditorTabTitleStyle") });
                header.Children.Add(new Button { Style = (Style)window.FindResource("EditorTabCloseButtonStyle"),
                    Content = new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Close, Width = 10, Height = 10 } });
                var tab = new TabItem { Header = header }; originals.Add(tab); tabs.Items.Add(tab);
            }
            window.Show(); Pump(350); tabs.ApplyTemplate();
            CheckWorkspaceCaption(window);
            var face = (TabSelectionSurface)tabs.Template.FindName("TabSelectionPlate", tabs);
            Require(face.CornerRadius == new CornerRadius(10, 10, 0, 0) && !face.IsHitTestVisible, "Tab contour/input contract failed.");
            var editor = WorkspaceEditorFixture();
            var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs); host.Content = editor;
            window.UpdateLayout(); Pump(40);
            var rich = ControlTree(editor).OfType<RichTextBox>().Single();
            rich.Document = new FlowDocument { FontSize = 17 };
            rich.Document.Blocks.Add(new Paragraph(new Bold(new Run("Memory and learning"))));
            rich.Document.Blocks.Add(new Paragraph(new Run("A practical guide to learning that lasts.")));
            rich.Document.Blocks.Add(new Paragraph(new Run("Retrieval practice strengthens memory. Explain the idea in your own words, then revisit it after a few days.")));
            foreach (var field in ControlTree(editor).OfType<TextBox>().Where(t => t.Name == "TitleTextBox")) field.Text = "Memory and learning";
            rich.CaretPosition = rich.Document.ContentEnd; rich.BeginChange(); rich.CaretPosition.InsertTextInRun(" Keep this edit across tab transitions."); rich.EndChange();
            tabs.SelectedIndex = 1; Pump(80); tabs.SelectedIndex = 2; Pump(40); tabs.SelectedIndex = 0; Pump(280);
            Require(ReferenceEquals(host.Content, editor) && rich.CanUndo, "Tab changes replaced editor content/undo.");
            void CheckFace()
            {
                var selected = (TabItem)tabs.SelectedItem;
                var point = selected.TranslatePoint(new Point(), face);
                // A busy software compositor can deliver its completion after the nominal clock duration.
                for (var attempt = 0; attempt < 12 &&
                    (Math.Abs(face.Bounds.X - point.X) >= .01 || Math.Abs(face.Bounds.Width - selected.ActualWidth) >= .01); attempt++) Pump(25);
                Require(Math.Abs(face.Bounds.X - point.X) < .01 && Math.Abs(face.Bounds.Width - selected.ActualWidth) < .01,
                    $"{theme}/{language}/{mode}: tab face {face.Bounds} vs header {point}, {selected.ActualWidth}; clocks {RenderTransitionClock.IsAnyRunning}.");
            }
            CheckFace();
            var headerScroll = (ScrollViewer)tabs.Template.FindName("TabHeaderScrollViewer", tabs);
            var previous = (Button)tabs.Template.FindName("ScrollTabsLeft", tabs);
            var next = (Button)tabs.Template.FindName("ScrollTabsRight", tabs);
            Require(headerScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden && next.Visibility == Visibility.Visible,
                "Tab overflow retained a scrollbar or lost its arrows.");
            var chrome = ControlTree(editor).OfType<Border>().Single(border => border.Name == "EditorChrome");
            Require(chrome.CornerRadius == new CornerRadius(14) && chrome.Margin.Top == 0,
                "Hosted editor chrome does not join the tab surface.");
            Require(face.ConnectionDepth == 14 && face.ViewportBounds.Width > 0, "Tab connection lost its viewport or rounded-corner bridge.");
            Require(ControlTree(window).OfType<SurfaceMaterialBorder>().Count(border => SurfaceMaterial.GetKind(border) == SurfaceMaterialKind.Workspace) == 1,
                "A hosted document introduced a second workspace frame.");
            var selectedBeforeScroll = tabs.SelectedItem;
            var sawIntermediateOffset = false;
            ScrollChangedEventHandler sampled = (_, _) =>
            {
                if (headerScroll.HorizontalOffset > .1 && headerScroll.HorizontalOffset < headerScroll.ViewportWidth * .75 - 1)
                    sawIntermediateOffset = true;
            };
            headerScroll.ScrollChanged += sampled;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (mode == MotionPreference.Automatic)
                Require(headerScroll.HorizontalOffset < .1, "The arrow immediately teleported the tab strip.");
            for (var sample = 0; sample < 20 && headerScroll.HorizontalOffset < .1; sample++)
            { Pump(25); window.UpdateLayout(); }
            headerScroll.ScrollChanged -= sampled;
            Require(headerScroll.HorizontalOffset > 0 && ReferenceEquals(tabs.SelectedItem, selectedBeforeScroll) && ReferenceEquals(host.Content, editor),
                $"Scrolling headers changed the document or failed to move: {theme}/{language}/{mode}, offset {headerScroll.HorizontalOffset}, clocks {RenderTransitionClock.IsAnyRunning}.");
            if (mode == MotionPreference.Automatic)
                Require(sawIntermediateOffset,
                    "The tab scroll arrow teleported to its final offset.");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var sample = 0; sample < 40 && (headerScroll.HorizontalOffset >= 1 || previous.IsEnabled); sample++)
            { Pump(25); window.UpdateLayout(); }
            Require(headerScroll.HorizontalOffset < 1 && !previous.IsEnabled,
                $"Tab overflow does not settle at its left edge: {theme}/{language}/{mode}, offset {headerScroll.HorizontalOffset}, enabled {previous.IsEnabled}, clocks {RenderTransitionClock.IsAnyRunning}.");
            if (mode == MotionPreference.Automatic)
            {
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var sample = 0; sample < 40 && headerScroll.HorizontalOffset <= headerScroll.ViewportWidth; sample++)
                { Pump(25); window.UpdateLayout(); }
                Require(headerScroll.HorizontalOffset > headerScroll.ViewportWidth,
                    "Repeated arrow clicks lost their pending scroll distance.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(35);
                tabs.SelectedItem = originals[0];
                // Selecting an already-selected tab does not raise SelectionChanged; switch first.
                tabs.SelectedItem = originals[1]; tabs.SelectedItem = originals[0]; Pump(300);
                Require(headerScroll.HorizontalOffset < 1, "A pending arrow glide overrode the newly selected tab.");
            }
            SaveControlCapture((FrameworkElement)window.Content, $"tabs-editor-{theme}-{language}-{mode}");
            tabs.Items.Remove(originals[2]); tabs.Items.Insert(1, originals[2]); tabs.SelectedItem = originals[0]; Pump(280);
            Require(ReferenceEquals(host.Content, editor) && rich.CanUndo, "Header reorder replaced editor state.");
            CheckFace();
            var title = ((StackPanel)originals[0].Header).Children.OfType<TextBlock>().Single();
            title.Text = "Short"; Pump(280); CheckFace();
            title.Text = "Memory and learning"; Pump(280); CheckFace();
            tabs.SelectedItem = originals[11]; originals[11].BringIntoView(); Pump(300);
            CheckFace();
            SaveControlCapture((FrameworkElement)window.Content, $"tabs-overflow-{theme}-{language}-{mode}");
            Require(headerScroll.HorizontalOffset > 0 && !next.IsEnabled, "Selecting the last document does not reveal its header.");
            tabs.SelectedItem = originals[0]; Pump(280);
            foreach (var item in originals.Skip(3)) tabs.Items.Remove(item);
            Pump(80);
            Require(previous.Visibility == Visibility.Collapsed && next.Visibility == Visibility.Collapsed,
                "Overflow arrows remain after the documents fit the rail.");
            SaveControlCapture((FrameworkElement)window.Content, $"tabs-fitted-{theme}-{language}-{mode}");
            tabs.SelectedItem = originals[1]; Pump(280); CheckFace();
            SaveControlCapture((FrameworkElement)window.Content, $"tabs-middle-{theme}-{language}-{mode}");
            rich.Undo(); Require(!new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Contains("Keep this edit"), "Editor undo failed.");
            window.Close(); Pump(500); Require(!RenderTransitionClock.IsAnyRunning, "Workspace tabs left idle motion work.");
            checks.Add($"{theme}/{language}/{mode}: populated production editor markup, interrupted tab selection, face alignment after rename/reorder/overflow, native undo and retained hosted content; no clocks remain after close.");
        }
    }
}
