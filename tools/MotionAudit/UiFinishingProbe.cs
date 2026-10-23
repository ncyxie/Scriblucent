using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeUiFinishing(bool quick)
    {
        var settings = new AppSettings { EnableAutoSave = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, settings);
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            LoadNativeControlResources(theme);
            Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(language);
            MotionPolicy.Configure(mode, false);
            var suffix = $"{theme}-{language}-{mode}";
            if (!quick)
            {
                foreach (var count in new[] { 0, 1, 8 }) FinishingPicker(settings, count, 0, 0, suffix);
                FinishingPicker(settings, 8, 520, 420, suffix);
                FinishingPicker(settings, 8, 980, 800, suffix);
            }
            FinishingDisclosure(suffix);
            FinishingTrash(suffix);
            FinishingNested(suffix);
            Pump(400);
            Require(!RenderTransitionClock.IsAnyRunning, "Unloaded finishing controls retained a motion clock: " + suffix);
            checks.Add(suffix + (quick ? ": disclosure full-face/keyboard/reversal/wrapping/gap, Trash selection/focus/logical exclusions/recycling and compiled nested surface/swatch checks." : ": picker fallback/owner bounds, 0/1/8 recents, all HSV corners, reachable footer and recents, exact hovered/pressed color; disclosure full-face/keyboard/reversal/wrapping/gap; Trash selection/focus/logical exclusions/recycling and compiled nested surfaces checked."));
            Console.WriteLine("Completed " + suffix);
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Synthetic fixtures queued a preferences write.");
        File.WriteAllLines(Path.Combine(_output, quick ? "chrome-checks.txt" : "checks.txt"), checks);
        var previousChecks = quick && File.Exists(Path.Combine(_output, "checks.txt")) ? File.ReadAllLines(Path.Combine(_output, "checks.txt")) : Array.Empty<string>();
        File.WriteAllText(Path.Combine(_output, "README.md"), "# UI finishing validation\n\nCompiled production controls with synthetic preferences and Trash entries. No document stores or saved preferences are used.\n\nCaptures use WPF RenderTargetBitmap at 100/125/150/200% raster scale. These validate layout and paint, not physical pointer interaction, per-monitor DPI transitions or presented FPS.\n\n" + string.Join("\n", previousChecks.Concat(checks).Select(c => "- " + c)));
    }

    private static Rect FinishingBounds(FrameworkElement child, Visual parent)
        => child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));

    private static void FinishingPicker(AppSettings settings, int count, double width, double height, string suffix)
    {
        settings.RecentColors = new[] { "#2381DE", "#CC4F52", "#8AC282", "#DEC257", "#8971BD", "#303B49", "#FFFFFF", "#000000" }.Take(count).ToList();
        Window? owner = null;
        if (width > 0)
        {
            owner = new Window { Width = width, Height = height, Content = new Grid(), ShowInTaskbar = false, ShowActivated = false };
            owner.Show(); Pump(20);
        }
        var picker = new ColorPickerDialog { Owner = owner };
        picker.Show(); Pump(380); picker.UpdateLayout();
        var root = (FrameworkElement)picker.Content;
        var shell = (OverlayDialogShell)root;
        var viewer = (ScrollViewer)picker.FindName("ColorBodyScrollViewer");
        var viewport = ControlTree(viewer).OfType<ScrollContentPresenter>().First();
        var footer = (FrameworkElement)shell.Footer;
        var footerBounds = FinishingBounds(footer, root);
        Require(footerBounds.Bottom <= root.ActualHeight && footerBounds.Top >= 0, "Picker footer was clipped: " + suffix);
        Require(viewer.ViewportHeight > 0 && viewer.ActualWidth > 0, "Picker body was unbounded.");
        if (owner is null) Require(picker.Width == 400 && picker.Height == 480, "Standalone picker fallback changed.");
        else Require(picker.ActualWidth <= owner.ActualWidth && picker.ActualHeight <= owner.ActualHeight, "Picker exceeded owner bounds.");
        var spectrum = (Border)picker.FindName("SpectrumBorder");
        var thumb = (FrameworkElement)picker.FindName("SpectrumThumb");
        viewer.ScrollToTop(); Pump(20);
        foreach (var (x, y, hex) in new[] { (0d, 0d, "#FFFFFF"), (1d, 0d, "#FF0000"), (0d, 1d, "#000000"), (1d, 1d, "#000000") })
        {
            RemainingCall(picker, "SampleSpectrum", new Point(x * spectrum.ActualWidth, y * spectrum.ActualHeight));
            Pump(20);
            // A short owner scrolls the body; bring the marker's full extent into view.
            thumb.BringIntoView(); Pump(20);
            var bounds = FinishingBounds(thumb, viewport);
            Require(bounds.Left >= 0 && bounds.Right <= viewport.ActualWidth + .1 && bounds.Top >= 0 && bounds.Bottom <= viewport.ActualHeight + .1,
                $"HSV marker clipped by viewport {bounds}, viewport={viewport.RenderSize}, fixture={suffix}/{width}/{count}.");
            Require(((TextBox)picker.FindName("HexTextBox")).Text == hex, "HSV endpoint color changed.");
        }
        var recents = (Panel)picker.FindName("RecentColorsPanel");
        Require(recents.Children.Count == count, "Picker lost recent samples.");
        viewer.ScrollToBottom(); Pump(40); picker.UpdateLayout();
        foreach (var sample in recents.Children.OfType<Button>())
        {
            var bounds = FinishingBounds(sample, viewport);
            Require(bounds.Top >= -.1 && bounds.Bottom <= viewport.ActualHeight + .1, "Recent sample cannot be reached by scrolling.");
            Require(CommandLightBehavior.GetRole(sample) == CommandLightRole.Value, "Sample inherited command lighting.");
            var face = (InteractionSurface)sample.Template.FindName("SwatchBorder", sample);
            var expected = ((SolidColorBrush)sample.Background).Color;
            foreach (var pressed in new[] { false, true })
            {
                face.AnimateState(true, pressed, false);
                CommandLightBehavior.PreviewPointer(root, sample.TranslatePoint(new Point(16, 16), root), true);
                CommandLightBehavior.PreviewPress(root, sample, pressed); Pump(170);
                var image = Snapshot(face, 1); var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Require(pixel[0] == expected.B && pixel[1] == expected.G && pixel[2] == expected.R, $"Hover/press changed sample {expected}: {string.Join(',', pixel)}.");
            }
            face.AnimateState(false, false, false);
            CommandLightBehavior.PreviewPress(root, sample, false);
            CommandLightBehavior.PreviewPointer(root, new Point(), false);
        }
        var hexField = (Border)picker.FindName("HexFieldShell");
        var hexText = (TextBox)picker.FindName("HexTextBox"); hexText.Focus(); Pump(180);
        Require(hexText.FontFamily.Source == "Consolas" && FieldFocusRingBehavior.GetIsEnabled(hexField), "Hex field lost shared focus rim or monospace.");
        hexText.Text = "#56A7CB"; Pump(20);
        Require(((SolidColorBrush)((Border)picker.FindName("PreviewSwatch")).Background).Color == Color.FromRgb(0x56, 0xA7, 0xCB), "Hex parsing changed.");
        if (count == 8) SaveControlCapture(root, $"picker-{(width == 0 ? "standalone" : width.ToString())}-{suffix}");
        picker.Close(); owner?.Close(); Pump(220);
    }

    private static void FinishingDisclosure(string suffix)
    {
        var disclosure = new Expander { Header = new TextBlock { Text = "A descriptive expandable section header that wraps at narrow widths", TextWrapping = TextWrapping.Wrap },
            Content = new Border { Height = 52, Style = (Style)_app.FindResource("ContentInsetBorderStyle"), Child = new TextBlock { Text = "Inset content" } } };
        var swatch = new Button { Width = 32, Height = 32, Background = new SolidColorBrush(Color.FromRgb(40, 120, 210)), Style = (Style)_app.FindResource("EditorColorSwatchButtonStyle") };
        var stack = new StackPanel { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24), Children = { disclosure, swatch } };
        var root = new Grid { Children = { stack } };
        var window = new Window { Width = 380, Height = 400, Content = root, ShowInTaskbar = false };
        window.Show(); Pump(50); window.UpdateLayout();
        var header = (ToggleButton)disclosure.Template.FindName("HeaderSite", disclosure);
        var face = (InteractionSurface)header.Template.FindName("DisclosureHeaderFace", header);
        var content = (FrameworkElement)disclosure.Template.FindName("DisclosureContent", disclosure);
        var reveal = (LayoutRevealHost)VisualTreeHelper.GetParent(content);
        Require(header.FocusVisualStyle is not null && header.MinHeight == 36, "Disclosure lost focus/target size.");
        foreach (var x in new[] { 1d, header.ActualWidth / 2, header.ActualWidth - 1 })
        {
            var hit = header.InputHitTest(new Point(x, header.ActualHeight / 2)) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, header)) hit = VisualTreeHelper.GetParent(hit);
            Require(ReferenceEquals(hit, header), "Disclosure header has an inert face gap.");
        }
        for (var i = 0; i < 7; i++) { disclosure.IsExpanded = i % 2 == 0; Pump(20); }
        Pump(360); window.UpdateLayout();
        Require(disclosure.IsExpanded && reveal.ActualHeight >= 64, "Disclosure reversed without settling content/gap.");
        Require(header.IsChecked == true && header.BorderThickness == new Thickness(), "Expansion acquired a selection border.");
        ((IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle)!).Toggle(); Pump(360);
        Require(!disclosure.IsExpanded && reveal.ActualHeight < .1, "Collapsed disclosure retained body spacing.");
        RemainingKeyboardClickForToggle(header); Pump(360);
        Require(disclosure.IsExpanded, "Disclosure did not respond to Space.");
        Require(CommandLightBehavior.GetRole(swatch) == CommandLightRole.Value && swatch.FocusVisualStyle is not null, "Editor/sketch sample role/focus missing.");
        face.AnimateState(true, false, false); SaveControlCapture(root, "disclosure-" + suffix);
        window.Close(); Pump(100);
    }

    private static void FinishingNested(string suffix)
    {
        void Check(FrameworkElement root, string styleName, int minimum)
        {
            var shared = (Style)root.FindResource(styleName);
            bool Inherits(Style? style) => style is not null && (ReferenceEquals(style, shared) || Inherits(style.BasedOn));
            var faces = ControlTree(root).OfType<Border>().Where(b => Inherits(b.Style)).ToArray();
            Require(faces.Length >= minimum, "Compiled nested surface missing: " + styleName);
            foreach (var face in faces)
                Require(face.Effect is null && face.CornerRadius == new CornerRadius(14) && face.BorderThickness == new Thickness()
                    && ReferenceEquals(face.Background, face.FindResource("ContentInsetSurface")), "Nested face bypassed shared neutral recess.");
        }
        var node = new StyleNodeDialog(); node.Show(); Pump(350);
        Check((FrameworkElement)node.Content, "ContentInsetBorderStyle", 1);
        foreach (var swatch in ControlTree(node).OfType<Button>().Where(b => ReferenceEquals(b.Style, node.FindResource("StyleNodeColorSwatchButtonStyle"))))
            Require(CommandLightBehavior.GetRole(swatch) == CommandLightRole.Value && swatch.FocusVisualStyle is not null, "Node sample role/focus differs from editor samples.");
        SaveControlCapture((FrameworkElement)node.Content, "node-style-" + suffix); node.Close(); Pump(100);
        var caption = new InsertCaptionDialog("Figure", "A useful caption", CaptionNumbering.Arabic, ": ", CaptionPosition.Below, false, new[] { "Figure", "Diagram" }, new[] { "Figure" }, _ => 1, false);
        caption.Show(); Pump(350); Check((FrameworkElement)caption.Content, "ContentInsetBorderStyle", 1);
        SaveControlCapture((FrameworkElement)caption.Content, "caption-" + suffix); caption.Close(); Pump(100);
        var schedule = new NoteSchedulePanel { Visibility = Visibility.Visible, IsHitTestVisible = true };
        var scheduleMotion = (FrameworkElement)schedule.FindName("PanelMotionHost"); scheduleMotion.Opacity = 1; scheduleMotion.RenderTransform = Transform.Identity;
        var owner = new Window { Width = 980, Height = 800, Content = schedule, ShowInTaskbar = false };
        owner.Show(); Pump(100); Check(schedule, "ScheduleSectionCardStyle", 2);
        SaveControlCapture(schedule, "schedule-" + suffix); owner.Close(); Pump(100);
        var quiz = new QuizModeWindow(new QuizDocument { Questions = new() { new() { Question = "A question", Options = new() { new() { Text = "An answer", IsCorrect = true } } } } });
        quiz.Show(); Pump(200); RemainingCall(quiz, "ShowResults"); Pump(850);
        Check((FrameworkElement)quiz.Content, "ContentInsetBorderStyle", 1);
        var reviewText = ControlTree((ItemsControl)quiz.FindName("ResultsQuestionsList")).OfType<TextBlock>().Select(t => t.Text).ToArray();
        Require(reviewText.Any(t => t.Contains("A question")) && reviewText.Any(t => t.Contains("An answer")), "Native quiz review lost bound question/answer text.");
        SaveControlCapture((FrameworkElement)quiz.Content, "quiz-results-" + suffix); quiz.Close(); Pump(350);
    }

    private static void RemainingKeyboardClickForToggle(ToggleButton toggle)
    {
        toggle.Focus();
        foreach (var routed in new[] { System.Windows.Input.Keyboard.KeyDownEvent, System.Windows.Input.Keyboard.KeyUpEvent })
            toggle.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(toggle)!, Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = routed });
    }

    private static void FinishingTrash(string suffix)
    {
        var panel = new TrashPanel { Visibility = Visibility.Visible };
        var motion = (FrameworkElement)panel.FindName("PanelMotionHost"); motion.Opacity = 1; motion.RenderTransform = Transform.Identity;
        var list = (ListBox)panel.FindName("TrashedItemsListBox");
        var items = new LazyTrashItemList(Enumerable.Range(0, 12_000).Select(i => new TrashItemEntry((TrashItemKind)(i % 4), Guid.NewGuid(), "Synthetic deleted item " + i, DateTime.UtcNow, new object())).ToArray(), 30);
        list.ItemsSource = items;
        var window = new Window { Width = 900, Height = 760, Content = panel, ShowInTaskbar = false };
        window.Show(); Pump(100); window.UpdateLayout();
        ListBoxItem Container(int index) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        SurfaceMaterialBorder Face(int index) => ControlTree(Container(index)).OfType<SurfaceMaterialBorder>().First();
        var first = Face(0); var second = Face(1);
        list.SelectedItems.Add(items[0]); list.SelectedItems.Add(items[1]); Pump(180);
        Require(StateSurfaceMotionBehavior.GetIsSelected(first) && StateSurfaceMotionBehavior.GetIsSelected(second), "Native multi-selection lost row feedback.");
        list.SelectedItems.Remove(items[1]); Pump(180);
        Require(!StateSurfaceMotionBehavior.GetIsSelected(second), "Removed native selection retained feedback.");
        Require(first.CornerRadius == new CornerRadius(14) && StateSurfaceMotionBehavior.GetSelectionThickness(first) == 2 && SurfaceMaterial.GetKind(first) == SurfaceMaterialKind.Raised, "Trash row material/rim mismatch.");
        var outer = (Border)Container(0).Template.FindName("Row", Container(0));
        Require(outer.Background is SolidColorBrush { Color.A: 0 }, "Trash still has a second container paint.");
        Require(Container(0).FocusVisualStyle is null, "Trash acquired a second native focus decoration.");
        Container(1).Focus(); Pump(180);
        Require(StateSurfaceMotionBehavior.GetActiveState(second) == true, "Keyboard focus has no row feedback.");
        RemainingCall(panel, "SelectAllTrashItems"); Pump(180);
        Require(panel.IsLogicalAllSelected && list.SelectedItems.Count == 0 && StateSurfaceMotionBehavior.GetIsSelected(first), "Logical select-all became physical selection or lost feedback.");
        items[0].IsExcludedFromLogicalSelection = true; Pump(180);
        Require(!StateSurfaceMotionBehavior.GetIsSelected(first) && StateSurfaceMotionBehavior.GetIsSelected(second), "Logical exclusion paint is wrong.");
        list.ScrollIntoView(items[900]); Pump(200); window.UpdateLayout();
        Require(StateSurfaceMotionBehavior.GetIsSelected(Face(900)), "Recycled row retained excluded selection state.");
        list.ScrollIntoView(items[0]); Pump(200); window.UpdateLayout();
        Require(!StateSurfaceMotionBehavior.GetIsSelected(Face(0)), "Recycled exclusion was lost.");
        Require(items.CreatedItemCount < 300 && ControlTree(list).OfType<ListBoxItem>().Count() < 100, "Trash lost lazy virtualization.");
        SaveControlCapture(panel, "trash-" + suffix);
        window.Close(); Pump(150);
    }
}
