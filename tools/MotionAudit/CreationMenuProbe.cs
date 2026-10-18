using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent;

internal static partial class Program
{
    private static void ProbeCreationMenuPaint()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            var root = new Grid { Background = (Brush)_app.FindResource("WindowBackground") };
            var menu = new DashboardCreateMenu { CreateLabel = "New note", QuickAddLabel = "Quick add note", ImportLabel = "Import notes", CreateIcon = Scriblucent.Icons.AppIconKind.Notes };
            root.Children.Add(menu);
            var window = new Window { Content = root, Width = 360, Height = 360, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show(); Pump(60); menu.IsOpen = true; Pump(60);
                var viewport = (ScrollViewer)menu.FindName("ActionViewport");
                var presenter = ControlTree(viewport).OfType<ScrollContentPresenter>().Single();
                var first = menu.ImportButton.TranslatePoint(new Point(), presenter);
                var last = menu.CreateButton.TranslatePoint(new Point(menu.CreateButton.ActualWidth, menu.CreateButton.ActualHeight), presenter);
                Require(first.X >= 12 && first.Y >= 12 && presenter.ActualWidth - last.X >= 12 && presenter.ActualHeight - last.Y >= 13,
                    "The scroll presenter clips the baked action-shadow falloff.");
                var gap = menu.LauncherButton.TranslatePoint(new Point(), root).Y
                    - menu.CreateButton.TranslatePoint(new Point(0, menu.CreateButton.ActualHeight), root).Y;
                Require(Math.Abs(gap - 12) < .01, "Shadow clearance changed the 12 DIP launcher gap.");
                foreach (var label in ControlTree(menu).OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text)))
                {
                    Require(label.Background is null or SolidColorBrush { Color.A: 0 }, "Creation label painted its own background rectangle.");
                }
                var icons = new[] { menu.CreateButton, menu.QuickAddButton, menu.ImportButton }.Select(b => ControlTree(b).OfType<Scriblucent.Icons.AppIcon>().Single()).ToArray();
                Require(icons.Select(i => ((SolidColorBrush)i.Foreground).Color).Distinct().Count() == 1,
                    "Creation/import icon foregrounds do not match.");
                SaveControlCapture(root, $"creation-paint-{theme}");
                MotionPolicy.Configure(MotionPreference.Automatic, false);
                var face = ControlTree(menu.CreateButton).OfType<InteractionSurface>().Single();
                face.AnimateState(true, false, false);
                var scope = (FrameworkElement)menu.FindName("ActionPanel");
                Scriblucent.Behaviors.CommandLightBehavior.PreviewPointer(scope, face.TranslatePoint(new Point(face.ActualWidth * .65, face.ActualHeight * .5), scope), true);
                Pump(250);
                SaveControlCapture(root, $"creation-paint-{theme}-hover");
                face.AnimateState(true, true, false); Pump(30);
                SaveControlCapture(root, $"creation-paint-{theme}-pressed");
                face.AnimateState(false, false, false);
                Scriblucent.Behaviors.CommandLightBehavior.PreviewPointer(scope, new Point(), false); Pump(230);
                menu.IsOpen = false;
                var row = (Grid)menu.FindName("CreateRow");
                SurfaceMotion.Stop(row, MotionChannel.Presence);
                SurfaceMotion.Stop((Grid)menu.FindName("ImportRow"), MotionChannel.Presence);
                SurfaceMotion.Stop((Grid)menu.FindName("QuickAddRow"), MotionChannel.Presence);
                row.Opacity = .7;
                Require(menu.CreateButton.IsVisible, "Closing-paint probe missed the visible exit pose.");
                Require(!menu.CreateButton.IsEnabled && !((Panel)menu.FindName("ActionPanel")).IsHitTestVisible,
                    "Closing actions remained interactive.");
                Require(menu.CreateButton.Opacity == 1, "Closing action stacked disabled opacity over its row fade.");
                CheckCreationLabelPaint(root, menu.CreateButton, theme);
                SaveControlCapture(root, $"creation-paint-{theme}-closing");
                menu.CloseImmediately();
                AwaitCreationMotion(menu);
                menu.IsOpen = true; Pump(65);
                SaveControlCapture(root, $"creation-paint-{theme}-opening");
                AwaitCreationMotion(menu);
                Console.WriteLine($"PASS {theme}: matching icons; rest/hover/press/open/close paint; disabled closing actions; no label rectangle.");
            }
            finally { window.Close(); }
            ProbeCreationMenuLifecycle(theme, Console.WriteLine);
        }
    }

    private static void CheckCreationLabelPaint(FrameworkElement root, Button button, string theme)
    {
        root.UpdateLayout();
        var label = ControlTree(button).OfType<TextBlock>().Single();
        var point = label.TranslatePoint(new Point(label.ActualWidth - 2, label.ActualHeight / 2), root);
        var above = label.TranslatePoint(new Point(label.ActualWidth - 2, -3), root);
        int Difference()
        {
            var pixels = Snapshot(root, 1);
            var bytes = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
            pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
            var inside = ((int)point.Y * pixels.PixelWidth + (int)point.X) * 4;
            var outside = ((int)above.Y * pixels.PixelWidth + (int)above.X) * 4;
            return Enumerable.Range(0, 3).Max(c => Math.Abs(bytes[inside + c] - bytes[outside + c]));
        }
        var delta = Difference();
        Require(delta <= 2, $"{theme}: the label's empty area still paints a rectangular patch (delta={delta}).");
        // Recreate the former disabled template opacity to prove this checks the reported artifact.
        TextOptions.SetTextRenderingMode(button, TextRenderingMode.Grayscale);
        button.Opacity = .45;
        Pump(40);
        var baseline = Difference();
        SaveControlCapture(root, $"creation-rectangle-{theme}-baseline");
        button.ClearValue(UIElement.OpacityProperty);
        button.ClearValue(TextOptions.TextRenderingModeProperty);
        Pump(40);
        if (theme == "Light")
            Require(baseline >= 8 && baseline > delta + 4, $"{theme}: pixel regression did not distinguish the reported bright rectangle ({baseline}/{delta}).");
        Console.WriteLine($"{theme} label rectangle: former delta={baseline}, fixed delta={delta}");
    }

    private static void InvokeCreationButton(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void AwaitCreationMotion(DashboardCreateMenu menu)
    {
        var targets = new[] { "PlusIcon", "CreateRow", "QuickAddRow", "ImportRow" }.Select(name => (FrameworkElement)menu.FindName(name)).ToArray();
        for (var i = 0; i < 60 && targets.Any(t => NativeSurfaceMotion.IsRunning(t, MotionChannel.Presence)); i++) Pump(20);
        Require(targets.All(t => !NativeSurfaceMotion.IsRunning(t, MotionChannel.Presence)), "Creation motion did not release its native clocks.");
    }

    private static void ProbeDashboardCreationMenu(Window window, FrameworkElement page, MainViewModel vm, string theme, Action<string> mark)
    {
        var menu = ControlTree(page).OfType<DashboardCreateMenu>().Single();
        Require(!menu.IsOpen && !menu.CreateButton.IsVisible && menu.LauncherButton.IsVisible, "Dashboard did not have one resting creation button.");
        var launcherPosition = menu.LauncherButton.TranslatePoint(new(), page);
        var content = (FrameworkElement)page.FindName(vm.ActiveDashboard + "DashboardContentHost");
        var contentSize = content.RenderSize;
        foreach (var preference in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            MotionPolicy.Configure(preference, false);
            InvokeCreationButton(menu.LauncherButton); Pump(320); AwaitCreationMotion(menu); window.UpdateLayout();
            Require(menu.IsOpen && menu.CreateButton.IsVisible && menu.QuickAddButton.IsVisible && menu.ImportButton.IsVisible, "Creation menu did not reveal its actions.");
            Require(menu.LauncherButton.TranslatePoint(new(), page) == launcherPosition && content.RenderSize == contentSize,
                "Opening creation actions moved the anchor or dashboard layout.");
            var actionBottom = menu.CreateButton.TranslatePoint(new Point(0, menu.CreateButton.ActualHeight), page).Y;
            Require(Math.Abs(launcherPosition.Y - actionBottom - 12) < .01, "Viewport padding changed the 12 DIP action/launcher gap.");
            Require(menu.CreateButton.ActualHeight >= 44 && menu.ImportButton.ActualHeight >= 44
                && menu.QuickAddButton.ActualHeight >= 44 && Math.Abs(menu.CreateButton.ActualWidth - menu.QuickAddButton.ActualWidth) < .01
                && Math.Abs(menu.CreateButton.ActualWidth - menu.ImportButton.ActualWidth) < .01, "Action row dimensions are inconsistent.");
            var angle = ((RotateTransform)((FrameworkElement)menu.FindName("PlusIcon")).RenderTransform).Angle;
            Require(Math.Abs(angle - 45) < .01, $"Plus did not become an X: {angle}, {preference}/{MotionPolicy.Quality}.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(menu)!;
            Require(((IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)!).ExpandCollapseState == ExpandCollapseState.Expanded,
                "Automation did not expose expansion state.");
            if (preference == MotionPreference.Automatic) SaveControlCapture(page, $"creation-{vm.ActiveDashboard}-{theme}-open");
            InvokeCreationButton(menu.LauncherButton); Pump(300); AwaitCreationMotion(menu);
            Require(!menu.IsOpen && !menu.CreateButton.IsVisible, "Creation menu did not finish closing.");
        }
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        for (var i = 0; i < 10; i++) { InvokeCreationButton(menu.LauncherButton); Pump(15); }
        menu.IsOpen = true; Pump(320);
        Require(menu.CreateButton.IsVisible && menu.CreateButton.Opacity == 1, "Interrupted creation animation did not settle.");
        int Count() => vm.IsNotesView ? vm.Notes.Count : vm.IsFlashcardsView ? vm.FlashcardSets.Count : vm.IsMindMapsView ? vm.MindMaps.Count : vm.Quizzes.Count;
        var before = Count();
        var windowsBefore = _app.Windows.Count;
        for (var i = 0; i < 16; i++) { InvokeCreationButton(menu.QuickAddButton); Pump(25); }
        Pump(900);
        Require(menu.IsOpen && Count() == before + 16 && _app.Windows.Count == windowsBefore,
            "Quick add closed the menu, opened an editor or dispatched duplicate/missing items.");
        if (vm.IsNotesView) AssertRapidAddLayout((FrameworkElement)page.FindName("UngroupedSectionBody"));
        var previousLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name;
        try
        {
            foreach (var language in new[] { LocalizationService.English, LocalizationService.Lithuanian })
            {
                LocalizationService.SetCulture(language); Pump(60);
                var createKey = vm.ActiveDashboard switch { "Notes" => "NewNoteAction", "Flashcards" => "NewFlashcardsAction", "MindMaps" => "NewMindMapAction", _ => "NewQuizAction" };
                Require(menu.CreateLabel == LocalizationService.GetString(createKey)
                    && menu.QuickAddLabel == LocalizationService.GetString(vm.ActiveDashboard switch { "Notes" => "QuickAddNote", "Flashcards" => "QuickAddFlashcards", "MindMaps" => "QuickAddMindMap", _ => "QuickAddQuiz" })
                    && menu.LauncherButton.ToolTip as string == LocalizationService.GetString("CloseCreationActions"),
                    "Creation labels or expansion tooltip did not follow the current language.");
                SaveControlCapture(page, $"creation-{vm.ActiveDashboard}-{theme}-{language}");
            }
        }
        finally { LocalizationService.SetCulture(previousLanguage); }
        menu.CloseImmediately();
        // Hiding, disabling and leaving the page must never leave an active input observer.
        menu.IsOpen = true; menu.SetCurrentValue(UIElement.IsEnabledProperty, false); Pump(25);
        Require(!menu.IsOpen && !menu.CreateButton.IsVisible, "Disabling left creation actions open.");
        menu.SetCurrentValue(UIElement.IsEnabledProperty, true);
        menu.IsOpen = true; menu.Visibility = Visibility.Collapsed; Pump(25);
        Require(!menu.IsOpen, "Hidden menu retained its open state.");
        menu.Visibility = Visibility.Visible;
        // Exercise the actual dashboard callback, rather than only the synthetic menu event.
        var service = ((MainWindow)window).EditorWorkspace;
        var kind = vm.ActiveDashboard switch { "Notes" => EditorDocumentKind.Note, "Flashcards" => EditorDocumentKind.Flashcards,
            "MindMaps" => EditorDocumentKind.MindMap, _ => EditorDocumentKind.Quiz };
        menu.IsOpen = true; Pump(260);
        InvokeCreationButton(menu.CreateButton); Pump(500);
        var workspace = service.Window!;
        Require(!menu.IsOpen && workspace is { IsVisible: true }, "New did not close the menu and open a workspace.");
        var session = workspace.Sessions.Single();
        var tabs = (TabControl)workspace.FindName("EditorTabs");
        var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs);
        Require(session.Key.Kind == kind && ReferenceEquals(host.Content, session.Content), "New did not select the matching editor.");
        Require(workspace.SaveSession(session, false), "The newly created editor document could not be saved.");
        workspace.CloseAfterPreflight();
        for (int i = 0; i < 100 && workspace.IsVisible; i++) Pump(25);
        Require(!workspace.IsVisible, "The new editor did not close cleanly.");
        Pump(150);
        mark($"PASS {vm.ActiveDashboard}/{theme}: resting anchor, staged actions, all motion modes, reversals, accessibility, lifecycle and creation policy");
    }

    private static void ProbeCreationMenuLifecycle(string theme, Action<string> mark)
    {
        var panel = new Grid();
        var menu = new DashboardCreateMenu { CreateLabel = "New note", QuickAddLabel = "Quick add note", ImportLabel = "Import notes" };
        var field = new TextBox { Width = 120, Height = 32, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(16) };
        panel.Children.Add(field); panel.Children.Add(menu);
        var window = new Window { Content = panel, Width = 420, Height = 420, ShowInTaskbar = false, ShowActivated = false };
        int created = 0, imported = 0, quickAdded = 0;
        menu.QuickAddCommand = new RelayCommand(() => { quickAdded++; Require(menu.IsOpen, "Quick add closed the menu."); });
        menu.CreateRequested += (_, _) => { created++; Require(!menu.IsOpen && !menu.CreateButton.IsVisible, "Creation callback saw an open menu."); };
        menu.ImportRequested += (_, _) => { imported++; Require(!menu.IsOpen && !menu.CreateButton.IsVisible, "Import handoff saw an open menu."); };
        try
        {
            window.Show(); Pump(100); MotionPolicy.Configure(MotionPreference.Automatic, false);
            menu.IsOpen = true; Pump(260);
            var gap = menu.TranslatePoint(new Point(8, menu.ActualHeight - 26), window);
            for (var hit = window.InputHitTest(gap) as DependencyObject; hit is not null;
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
                Require(!ReferenceEquals(hit, menu), "The menu's transparent layout area blocked underlying dashboard input.");
            InvokeCreationButton(menu.QuickAddButton); Pump(30);
            Require(quickAdded == 1 && menu.IsOpen, "Quick add callback failed.");
            InvokeCreationButton(menu.CreateButton); Pump(30);
            Require(created == 1 && !menu.IsOpen, "Editor handoff did not close first.");
            menu.IsOpen = true; Pump(260); InvokeCreationButton(menu.ImportButton); Pump(30);
            Require(imported == 1 && !menu.IsOpen, "Import was lost or duplicated.");
            menu.IsOpen = true; Pump(60);
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            field.RaiseEvent(mouse);
            Require(!menu.IsOpen && !mouse.Handled && Mouse.Captured is null, "Outside click was swallowed or left mouse capture active.");
            Pump(220); menu.IsOpen = true; Pump(40);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            field.RaiseEvent(wheel);
            Require(!menu.IsOpen && !wheel.Handled, "Outside scrolling did not dismiss without consuming the wheel.");
            Pump(250); menu.IsOpen = true; Pump(60);
            window.Activate(); menu.CreateButton.Focus(); Pump(30); field.Focus(); Pump(40);
            Require(!menu.IsOpen && field.IsKeyboardFocused, "Focus did not leave the menu normally.");
            menu.IsOpen = true; Pump(260); menu.LauncherButton.Focus();
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            menu.LauncherButton.RaiseEvent(escape); Pump(220);
            Require(!menu.IsOpen && escape.Handled && menu.LauncherButton.IsKeyboardFocused, "Escape did not return to the launcher.");
            var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            menu.LauncherButton.RaiseEvent(up); Pump(260);
            Require(menu.IsOpen && menu.CreateButton.IsKeyboardFocused, "Keyboard opening did not focus Add.");
            var upAgain = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            menu.CreateButton.RaiseEvent(upAgain); Pump(30);
            Require(menu.QuickAddButton.IsKeyboardFocused, "Arrow navigation skipped Quick add.");
            menu.CreateLabel = new string('W', 42); menu.ImportLabel = "Import"; window.Width = 320; Pump(80); window.UpdateLayout();
            Require(menu.ActualWidth <= window.ActualWidth - 40 && menu.CreateButton.ActualHeight > 44
                && menu.CreateButton.ActualWidth == menu.ImportButton.ActualWidth, "Long labels did not wrap within the available menu width.");
            SaveControlCapture(panel, $"creation-{theme}-wrapped");
            window.Height = 250; Pump(80); window.UpdateLayout();
            var viewport = (ScrollViewer)menu.FindName("ActionViewport");
            Require(viewport.ScrollableHeight > 0 && menu.LauncherButton.TranslatePoint(new Point(), panel).Y >= 0,
                "A short viewport clipped the launcher or did not offer scrolling for wrapped actions.");
            SaveControlCapture(panel, $"creation-{theme}-short");
            menu.IsOpen = true; panel.Children.Remove(menu); Pump(30);
            Require(!menu.IsOpen && Mouse.Captured is null, "Unloaded menu retained input state.");
            panel.Children.Add(menu); Pump(40);
            Require(!menu.IsOpen, "Reattached menu reopened unexpectedly.");
            menu.IsOpen = true; Pump(40);
            var other = new Window { Width = 180, Height = 120, ShowInTaskbar = false };
            try { other.Show(); other.Activate(); Pump(60); Require(!menu.IsOpen, "Window deactivation retained the menu."); }
            finally { other.Close(); }
            mark($"PASS {theme}: outside input/focus, Escape/arrows, event handoff, narrow wrapping and unload cleanup");
        }
        finally { window.Close(); }
    }
}
