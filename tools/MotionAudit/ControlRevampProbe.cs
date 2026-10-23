using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeAboutLayout()
    {
        var report = new List<string>();
        var valid = true;
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme);
            Scriblucent.Localization.LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var about = new AboutPanel();
            var window = new Window { Width = 900, Height = 900, Content = about, ShowInTaskbar = false, UseLayoutRounding = true };
            try
            {
                window.Show(); about.ShowAnimated(); Pump(80);
                foreach (var height in new[] { 900d, 780d, 650d })
                {
                    window.Height = height; window.UpdateLayout(); Pump(40);
                    var card = (FrameworkElement)about.FindName("PanelCard");
                    var close = ControlTree(about).OfType<Button>().Single(button => Equals(button.Content, Scriblucent.Localization.LocalizationService.GetString("Close")));
                    var bounds = close.TransformToVisual(card).TransformBounds(new Rect(close.RenderSize));
                    var gap = card.ActualHeight - bounds.Bottom;
                    var cardBounds = card.TransformToVisual(about).TransformBounds(new Rect(card.RenderSize));
                    var visibleGap = Math.Min(card.ActualHeight, about.ActualHeight - cardBounds.Top) - bounds.Bottom;
                    report.Add($"{theme}/{language}/{height}: card={card.ActualHeight:F1}; close={bounds}; bottom gap={gap:F1}; visible gap={visibleGap:F1}; card on overlay={cardBounds}");
                    valid &= gap >= 23 && visibleGap >= 23 && cardBounds.Top >= 19 && cardBounds.Bottom <= about.ActualHeight - 19;
                    SaveControlCapture((FrameworkElement)window.Content, $"about-{theme}-{language}-{height}");
                    var scroll = (ScrollViewer)about.FindName("AboutContentScroll");
                    if (height == 650) Require(scroll.ScrollableHeight > 0, "Short About owners must keep all body content reachable.");
                    scroll.ScrollToBottom(); window.UpdateLayout(); Pump(30);
                    Require(Math.Abs(close.TransformToVisual(card).Transform(new Point()).Y - bounds.Top) < 1,
                        "Scrolling About content moved its Close footer.");
                    Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "About content cannot scroll to the bottom.");
                    scroll.ScrollToTop(); window.UpdateLayout();
                }
                InvokeOrderingButton((Button)about.FindName("AboutCloseButton")); Pump(80);
                Require(about.Visibility == Visibility.Collapsed, "About footer Close no longer dismisses the panel.");
            }
            finally { window.Close(); Pump(30); }
        }
        File.WriteAllLines(Path.Combine(_output, "about-layout-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
        Require(valid, "About Close button spacing was clipped or the dialog exceeded its owner.");
    }

    private static void LoadNativeControlResources(string theme)
    {
        LoadResources(theme);
        // App.xaml's native styles, without App startup or dashboard commands/data.
        var source = XDocument.Load("Scriblucent/App.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = new XElement(source.Descendants(wpf + "ResourceDictionary").First());
        dictionary.Element(wpf + "ResourceDictionary.MergedDictionaries")?.Remove();
        dictionary.Elements().Where(e => (string?)e.Attribute(x + "Key") == "NoteCardContextMenu").Remove();
        var wrapper = new XElement(wpf + "Grid", new XElement(wpf + "Grid.Resources", dictionary));
        var parsed = Fragment(wrapper, source.Root!);
        _app.Resources.MergedDictionaries.Add(parsed.Resources);
    }
    private static IEnumerable<DependencyObject> ControlTree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ControlTree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void SaveControlCapture(FrameworkElement root, string name)
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Snapshot(root, scale)));
            using var file = File.Create(Path.Combine(_output, $"{name}-{(int)(scale * 100)}.png")); encoder.Save(file);
        }
    }
    private static void ProbeControlRevamp()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            MotionPolicy.Configure(MotionPreference.Off, false);
            ProbeSettings(theme, checks);
            ProbeNativeControls(theme, checks);
            ProbeMenusAndUtilities(theme, checks);
            var print = new Scriblucent.PrintPreviewWindow("An idea worth keeping", "Synthetic print-preview content.\nInk remains readable on paper in either theme.");
            print.Show(); Pump(350); print.UpdateLayout();
            var ink = (TextBlock)print.FindName("PreviewTitle");
            Require(ink.Foreground is SolidColorBrush { Color.R: < 80 }, "Print ink must stay dark.");
            Require(ControlTree(print).OfType<BakedElevationLayer>().Any(s => s.Kind == BakedElevationLayer.ElevationKind.Paper), "Paper elevation missing.");
            SaveControlCapture((FrameworkElement)print.Content, $"print-{theme}"); print.Close(); Pump(30);
            checks.Add($"{theme}: compiled print preview uses dark paper ink and cached square paper elevation.");
        }
        MotionPolicy.Configure(MotionPreference.Full, false);
        Require(!RenderTransitionClock.IsAnyRunning, "A control left idle motion work behind.");
        checks.Add("Closed/unloaded controls leave no active motion tracks or frame observers.");
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        File.WriteAllText(Path.Combine(_output, "README.md"), "# Control revamp validation\n\n" +
            "Production compiled Settings and print-preview controls; native styles read from App.xaml. All content is synthetic. Settings population/save handlers were suppressed; no saved preferences or documents were changed.\n\n" +
            "PNG files are WPF RenderTargetBitmap captures at 100/125/150/200% raster scale, not live desktop captures or measurements of presented FPS. The Settings layout widths are actual DIP layouts. Forced software runs use the same controls.\n\n" +
            string.Join("\n", checks.Select(c => "- " + c)));
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void ProbeSettings(string theme, List<string> checks)
    {
        CultureInfo.CurrentUICulture = new CultureInfo("lt");
        var settings = new SettingsPanel();
        typeof(SettingsPanel).GetField("_isApplyingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, true);
        var preferences = new SettingsProbeModel();
        settings.DataContext = preferences;
        typeof(SettingsPanel).GetMethod("ApplyScrollbarPreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, [true, true, true]);
        foreach (var name in new[] { "EnableScrollbarCheckBox", "EnableVerticalScrollbarCheckBox", "EnableHorizontalScrollbarCheckBox" })
            Require(BindingOperations.IsDataBound((CheckBox)settings.FindName(name), ToggleButton.IsCheckedProperty), "Settings initialization replaced a live preference binding: " + name);
        var itemType = typeof(SettingsPanel).GetNestedType("ManagedAiToolItem", BindingFlags.NonPublic)!;
        var models = (System.Collections.IList)typeof(SettingsPanel).GetField("_managedAiTools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        foreach (var label in new[] { "Synthetic model with a descriptive name", "Synthetic cloze model" })
        {
            var item = Activator.CreateInstance(itemType, nonPublic: true)!;
            itemType.GetProperty("Name")!.SetValue(item, label);
            itemType.GetProperty("Key")!.SetValue(item, label);
            models.Add(item);
        }
        var root = (FrameworkElement)settings.FindName("PanelMotionHost");
        root.Opacity = 1; root.RenderTransform = Transform.Identity;
        ((UIElement)settings.FindName("OverlayDim")).Opacity = 1;
        var window = new Window { Width = 980, Height = 800, Content = settings, ShowInTaskbar = false, ShowActivated = false };
        window.Show(); Pump(100); window.UpdateLayout();
        foreach (var combo in ControlTree(settings).OfType<ComboBox>())
            if (combo.Items.Count == 0) { combo.Items.Add(new ComboBoxItem { Content = "Synthetic option" }); combo.SelectedIndex = 0; }
        window.UpdateLayout(); Pump(100);
        var viewer = (ScrollViewer)settings.FindName("SettingsScrollViewer");
        Require(viewer.ViewportHeight > 100 && viewer.ScrollableHeight > viewer.ViewportHeight, "Settings must have a bounded scrolling body.");
        var nav = (WrapPanel)settings.FindName("CategoryNavigation");
        Require(nav.Children.Count == 7, "All seven settings shortcuts must exist.");
        var card = (Border)settings.FindName("PanelCard");
        Require(card.ActualWidth <= 740, "Settings exceeded its width bound.");
        var originalNav = nav.TranslatePoint(new Point(), settings);
        ((Button)nav.Children[6]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(80);
        Require(viewer.VerticalOffset > viewer.ScrollableHeight - 2, "Last shortcut did not scroll to Data.");
        Require(nav.TranslatePoint(new Point(), settings) == originalNav, "Settings shortcuts scrolled away.");
        MotionPolicy.Configure(MotionPreference.Full, false);
        ((Button)nav.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(30);
        SmoothScrollBehavior.CancelTarget(viewer);
        var interrupted = viewer.VerticalOffset; Pump(160);
        Require(Math.Abs(viewer.VerticalOffset - interrupted) < 1, "Cancelled settings target kept moving.");
        MotionPolicy.Configure(MotionPreference.Off, false);
        ((Button)nav.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(60);
        var switches = ControlTree(settings).OfType<CheckBox>().Where(c => c.IsEnabled).ToArray();
        foreach (var toggle in switches)
        {
            ProbeSwitchSurface(window, toggle);
        }
        Require(preferences.EnableScrollbar && preferences.EnableVerticalScrollbar && preferences.EnableHorizontalScrollbar, "Bound settings switches failed to update/restore their model.");
        ((Button)nav.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50);
        SaveControlCapture(settings, $"settings-{theme}-wide-lt");
        window.Width = 480; window.UpdateLayout(); Pump(100);
        Require(card.ActualWidth <= window.ActualWidth, "Narrow settings overflowed its owner.");
        var visibleFields = ControlTree(settings).OfType<ComboBox>().Where(c => c.IsVisible && c.ActualWidth > 0).ToArray();
        foreach (var field in visibleFields)
        {
            var position = field.TranslatePoint(new Point(), card);
            Require(position.X >= 0 && position.X + field.ActualWidth <= card.ActualWidth + 1, $"Settings field {field.Name} escaped narrow card bounds.");
        }
        SaveControlCapture(settings, $"settings-{theme}-narrow-lt");
        ((Button)nav.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(80);
        SaveControlCapture(settings, $"settings-models-{theme}-narrow-lt");
        window.Width = 980; window.UpdateLayout(); Pump(60);
        foreach (var i in Enumerable.Range(0, 7))
        {
            ((Button)nav.Children[i]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(60);
            SaveControlCapture(settings, $"settings-section-{i}-{theme}-wide-lt");
        }
        ((Button)nav.Children[6]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(40);
        Require(viewer.VerticalOffset > viewer.ScrollableHeight - 2, "Settings navigation broke after resize.");
        window.Close(); Pump(60); CultureInfo.CurrentUICulture = new CultureInfo("en");
        checks.Add($"{theme}: compiled Settings, preserved preference bindings, all {switches.Length} enabled switch hit targets and visible thumb/fill states{(_pointerControls ? ", real mouse clicks" : "")}, seven category captures, fixed shortcuts/footer, Lithuanian wide/narrow layouts, scroll interruption and resize recovery.");
    }
    private static void ProbeNativeControls(string theme, List<string> checks)
    {
        var stack = new StackPanel { Margin = new Thickness(24), Width = 420 };
        var toggle = new CheckBox { Content = "Stay in flow", Style = (Style)_app.FindResource("SettingsSwitchStyle") };
        var model = new SwitchModel();
        BindingOperations.SetBinding(toggle, ToggleButton.IsCheckedProperty, new Binding(nameof(SwitchModel.Enabled)) { Source = model, Mode = BindingMode.TwoWay });
        var changed = 0; toggle.Checked += (_, _) => changed++; toggle.Unchecked += (_, _) => changed++;
        stack.Children.Add(toggle);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 25, TickFrequency = 10, IsSnapToTickEnabled = true, SmallChange = 10 };
        stack.Children.Add(slider);
        var hue = new Slider { Minimum = 0, Maximum = 360, Value = 180, Style = (Style)_app.FindResource("HueSliderStyle") };
        stack.Children.Add(hue);
        var combo = new ComboBox { Padding = new Thickness(17, 2, 9, 2), ItemsSource = new[] { "A precise detail", "Another idea" }, SelectedIndex = 0 };
        stack.Children.Add(combo);
        var list = new ListBox { Height = 140, ItemContainerStyle = (Style)_app.FindResource("StateListBoxItemStyle"), ItemsSource = Enumerable.Range(0, 500).Select(i => "Synthetic result " + i).ToArray() };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); stack.Children.Add(list);
        var stage = new Border { Background = (Brush)_app.FindResource("WindowBackground"), Child = stack };
        var window = new Window { Width = 620, Height = 650, Content = stage, ShowInTaskbar = false };
        window.Show(); Pump(80); window.UpdateLayout();
        var peer = new CheckBoxAutomationPeer(toggle); var automation = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
        automation.Toggle(); Require(model.Enabled && toggle.IsChecked == true && changed == 1, "Switch automation/event/binding contract failed.");
        automation.Toggle(); Require(!model.Enabled && changed == 2, "Switch unchecked contract failed.");
        var sliderPeer = new SliderAutomationPeer(slider); var range = (IRangeValueProvider)sliderPeer.GetPattern(PatternInterface.RangeValue)!;
        range.SetValue(55); Pump(15); Require(slider.Value == 55, "Slider values must update immediately.");
        var fill = (Border)slider.Template.FindName("ValueFill", slider); var track = (Track)slider.Template.FindName("PART_Track", slider);
        Require(Math.Abs(fill.Width - (track.Thumb.ActualWidth / 2 + .55 * (track.ActualWidth - track.Thumb.ActualWidth))) < .5, "Slider fill did not match native value/track geometry.");
        Require(hue.Template.FindName("ValueFill", hue) == null, "Hue spectrum is obscured by a value overlay.");
        foreach (var key in new[] { "", "EditableComboBoxStyle", "ChromeComboBoxStyle", "SetSelectorComboBoxStyle", "LinkedNoteComboBoxStyle" })
        {
            combo.Style = key == "" ? (Style)_app.FindResource(typeof(ComboBox)) : (Style)_app.FindResource(key);
            window.UpdateLayout(); Pump(20);
            var editable = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
            var content = editable as FrameworkElement ?? ControlTree(combo).OfType<ContentPresenter>().FirstOrDefault(p => p.TemplatedParent == combo)
                ?? (FrameworkElement)ControlTree(combo).OfType<TextBlock>().First(t => t.TemplatedParent == combo);
            if (key == "LinkedNoteComboBoxStyle")
            {
                var label = (TextBlock)combo.Template.FindName("LinkedNoteLabel", combo);
                var face = (Grid)label.Parent;
                Require(face.Margin == combo.Padding && label.Margin.Left == 0
                    && face.ColumnDefinitions[2].Width == new GridLength(18),
                    "Linked note content/chevron did not respect its declared padding.");
            }
            else Require(content.Margin == new Thickness(17, 2, 41, 2), "Dropdown content did not respect its declared padding: " + key);
            combo.IsDropDownOpen = true; Pump(35);
            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
            Require(Math.Abs(item.FontSize - combo.FontSize) < .01, "Closed/open dropdown density mismatch: " + key);
            Require(item.FontFamily.Source == combo.FontFamily.Source, "Dropdown font family mismatch.");
            combo.IsDropDownOpen = false; Pump(15);
        }
        combo.Style = (Style)_app.FindResource("FeatureToolbarComboBoxStyle");
        Require(combo.FontSize == 14 && combo.MinHeight == 32, "Toolbar density family was overridden.");
        foreach (var preference in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            MotionPolicy.Configure(preference, false);
            toggle.IsChecked = true; toggle.IsChecked = false;
            combo.IsDropDownOpen = true; Pump(25); combo.IsDropDownOpen = false;
            list.SelectedIndex = 2; list.ScrollIntoView(list.Items[499]); Pump(220);
            Require(ControlTree(list).OfType<ListBoxItem>().Count() < 100, "Result list virtualization was lost.");
        }
        MotionPolicy.Configure(MotionPreference.Off, false); SaveControlCapture(stage, $"controls-{theme}");
        window.Close(); Pump(60);
        checks.Add($"{theme}: native switch automation/routed events/two-way binding, immediate slider values and fill geometry, hue spectrum, five dropdown templates and popup densities, recycled results; Full/Reduced/Off.");
    }
    private sealed class SwitchModel { public bool Enabled { get; set; } }
    private sealed class SettingsProbeModel
    {
        public bool EnableScrollbar { get; set; } = true;
        public bool EnableVerticalScrollbar { get; set; } = true;
        public bool EnableHorizontalScrollbar { get; set; } = true;
        public string SelectedLanguage { get; set; } = "lt";
        public string SelectedTheme { get; set; } = "System";
    }

    private static void ProbeSwitchSurface(Window window, CheckBox toggle)
    {
        toggle.BringIntoView(); window.UpdateLayout(); Pump(80);
        var track = (Border)toggle.Template.FindName("SwitchTrack", toggle);
        var point = track.TranslatePoint(new Point(20, 11), window);
        var hit = window.InputHitTest(point) as DependencyObject;
        Require(hit != null && ControlTree(toggle).Contains(hit), $"Switch pointer target blocked: {hit?.GetType().Name}");
        var automation = (IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
        var initial = toggle.IsChecked;
        automation.Toggle(); Pump(220);
        var thumb = ControlTree(toggle).OfType<Border>().Single(b => BindingOperations.IsDataBound(b, ControlDetailMotionBehavior.IsOnProperty));
        var fill = ControlTree(toggle).OfType<Border>().Single(b => BindingOperations.IsDataBound(b, ControlDetailMotionBehavior.IsLitProperty));
        Require(toggle.IsChecked != initial && thumb.RenderTransform.Transform(new Point()).X == (initial == true ? 0 : 18) && fill.Opacity == (initial == true ? 0 : 1), "Production switch visual state did not follow toggle: " + toggle.Name);
        automation.Toggle(); Pump(220);
        Require(toggle.IsChecked == initial && thumb.RenderTransform.Transform(new Point()).X == (initial == true ? 18 : 0) && fill.Opacity == (initial == true ? 1 : 0), "Production switch visual state did not reset: " + toggle.Name);
        if (_pointerControls) ProbePointerToggle(window, toggle, point);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(CursorPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    private static void ProbePointerToggle(Window window, CheckBox toggle, Point point)
    {
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previous);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        try
        {
            SetForegroundWindow(handle); window.Activate(); Pump(80);
            Require(GetForegroundWindow() == handle, "Pointer fixture needs an interactive desktop; foreground activation was unavailable.");
            var screen = window.PointToScreen(point);
            var location = new CursorPoint { X = (int)screen.X, Y = (int)screen.Y };
            Require(WindowFromPoint(location) == handle, "Pointer fixture target is covered by another window.");
            Require(SetCursorPos(location.X, location.Y), "Pointer positioning failed."); Pump(30);
            Require(toggle.IsMouseOver, "Pointer does not hit the Settings switch.");
            var initial = toggle.IsChecked;
            for (var i = 0; i < 2; i++)
            {
                Require(WindowFromPoint(location) == handle, "Pointer target became covered.");
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); Pump(20);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); Pump(220);
                Require(toggle.IsChecked == (i == 0 ? initial != true : initial), "Native pointer click failed: " + toggle.Name);
                var thumb = (Border)toggle.Template.FindName("SwitchThumb", toggle);
                var fill = (Border)toggle.Template.FindName("SwitchOnTrack", toggle);
                Require(thumb.RenderTransform.Transform(new Point()).X == (toggle.IsChecked == true ? 18 : 0) && fill.Opacity == (toggle.IsChecked == true ? 1 : 0), "Pointer click changed value without moving the switch: " + toggle.Name);
            }
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
            SetForegroundWindow(previousWindow);
        }
    }

    private static void ProbeMenusAndUtilities(string theme, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        var anchor = new Button { Content = "Menu anchor" };
        var owner = new Window { Width = 600, Height = 500, Content = anchor, ShowInTaskbar = false };
        owner.Show(); Pump(30);
        var menu = new ContextMenu { PlacementTarget = anchor };
        var parent = new MenuItem { Header = "Arrange" };
        var check = new MenuItem { Header = "Keep this detail", IsCheckable = true };
        var invocations = 0; check.Click += (_, _) => invocations++;
        parent.Items.Add(check); menu.Items.Add(parent); menu.IsOpen = true; Pump(50);
        var expand = (IExpandCollapseProvider)new MenuItemAutomationPeer(parent).GetPattern(PatternInterface.ExpandCollapse)!;
        expand.Expand(); Pump(50);
        Require(parent.IsSubmenuOpen && check.IsLoaded, "Submenu navigation was lost.");
        var leaf = (Border)check.Template.FindName("ItemBorder", check);
        Require(StateSurfaceMotionBehavior.GetIsEnabled(leaf), "Menu leaf does not use shared feedback.");
        Require(check.FontSize == 14 && check.MinHeight == 36, "Menu density mismatch.");
        var invoke = (IInvokeProvider)new MenuItemAutomationPeer(check).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke(); Pump(40);
        Require(check.IsChecked && invocations == 1, "Menu check/click contract changed.");
        menu.IsOpen = false; owner.Close(); Pump(30);

        var notes = Enumerable.Range(0, 12000).Select(i => new Scriblucent.ViewModels.NoteCardViewModel(
            new Scriblucent.Models.NoteDocument { Title = "Synthetic note " + i }, _ => { })).ToArray();
        var link = new InsertHyperlinkDialog("", notes);
        link.Show(); Pump(80);
        ((RadioButton)link.FindName("NoteModeRadioButton")).IsChecked = true;
        var search = (TextBox)link.FindName("NoteSearchTextBox");
        var results = (ListBox)link.FindName("NoteResultsListBox");
        search.Text = "no such synthetic title";
        search.Text = "Synthetic note 11999"; Pump(250);
        Require(results.Items.Count == 1 && ReferenceEquals(results.Items[0], notes[11999]), "Stale note-search generation replaced the latest results.");
        search.Text = "no such synthetic title"; Pump(180);
        Require(((FrameworkElement)link.FindName("NoteNoResults")).Visibility == Visibility.Visible, "Localized note-search empty state was not shown.");
        SaveControlCapture((FrameworkElement)link.Content, $"note-search-empty-{theme}");
        search.Text = ""; Pump(40);
        Require(results.Items.Count == notes.Length && results.Visibility == Visibility.Visible, "Clearing note search did not restore results.");
        Require(ControlTree(results).OfType<ListBoxItem>().Count() < 100, "Note-search virtualization was lost.");
        link.Close(); Pump(40);

        var style = new StyleNodeDialog();
        style.Show(); Pump(80);
        var picker = (WrapPanel)style.FindName("IconGrid");
        var first = (Button)picker.Children[0]; var second = (Button)picker.Children[1];
        var geometry = first.RenderSize; var frame = first.BorderThickness;
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(style.Icons.Count == 2, "Icon multiselect was lost.");
        Require(first.BorderThickness == frame && first.RenderSize == geometry, "Icon selection moved the resting contour.");
        Require(StateSurfaceMotionBehavior.GetIsSelected(first), "Icon selection state is missing.");
        SaveControlCapture((FrameworkElement)style.Content, $"node-style-{theme}");
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(style.Icons.Count == 1, "Icon deselection failed.");
        style.Close(); Pump(40);

        // Full production statistics markup with synthetic data; no settings service is loaded.
        var source = XDocument.Load("Scriblucent/Views/FlashcardStatisticsWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var markup = new XElement(source.Root!); markup.Attribute(x + "Class")?.Remove();
        foreach (var attribute in markup.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Loaded" or "Click").ToArray()) attribute.Remove();
        var stats = (Window)Fragment(markup, source.Root!);
        var rows = new[] {
            new FlashcardStatisticsWindow.StatBarRow { Label = "New", Count = 18, Ratio = 1, ChipKind = "New" },
            new FlashcardStatisticsWindow.StatBarRow { Label = "Learning", Count = 8, Ratio = 8d/18, ChipKind = "Learning" },
            new FlashcardStatisticsWindow.StatBarRow { Label = "Mature", Count = 12, Ratio = 12d/18, ChipKind = "Mature" }
        };
        stats.DataContext = new { HeaderSubtitleText = "Synthetic study collection", TotalCountText = "38", DueTodayCountText = "12",
            NewCountText = "18", LearningCountText = "8", MatureCountText = "12", DifficultySignalTitleText = "Ease factor",
            MaturityRows = rows, ForecastRows = rows, IntervalRows = rows, DifficultySignalRows = rows };
        stats.Show(); Pump(80); stats.UpdateLayout();
        var tracks = ControlTree(stats).OfType<Border>().Where(b => b.Name == "StatBarTrack").ToArray();
        Require(tracks.Length == 12, "Statistics template rows did not bind.");
        foreach (var track in tracks)
        {
            var row = (FlashcardStatisticsWindow.StatBarRow)track.DataContext; var fill = (Border)track.Child;
            UiMotion.AnimateProgress(fill, track.ActualWidth, row.Ratio, animate: false);
            Require(Math.Abs(fill.Width * UiMotion.EnsureScale(fill).ScaleX - track.ActualWidth * row.Ratio) < .001, "Statistics ratio changed.");
            var grid = (Grid)track.Parent;
            Require((string)grid.ToolTip == row.CountDescription && !row.CountDescription.Contains('%'), "Statistics tooltip implies a total percentage.");
            Require(grid.Children.OfType<TextBlock>().All(t => t.FontSize == 14), "Statistics row type mismatch.");
        }
        SaveControlCapture((FrameworkElement)stats.Content, $"statistics-{theme}"); stats.Close(); Pump(40);
        checks.Add($"{theme}: native submenu/check/click contracts; latest-generation note search, localized empty results and 12,000-result virtualization; stable icon multiselect; production statistics markup with unchanged ratios/count tooltips.");
    }
}
