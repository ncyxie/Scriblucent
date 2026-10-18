using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using Calendar = System.Windows.Controls.Calendar;

internal static partial class Program
{
    private static readonly List<object> PremiumRenderSamples = new();
    private static void ProbePremiumUi()
    {
        // Isolated process: constructors receive synthetic preferences, never read/save user files.
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme);
            LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            ProbePremiumCalendar(theme, language, checks);
            ProbePremiumWindows(theme, language, checks);
        }
        foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            MotionPolicy.Configure(mode, false);
            var tabs = new Scriblucent.NoteEditorTabsWindow();
            var control = (TabControl)tabs.FindName("EditorTabs");
            for (var i = 0; i < 3; i++)
            {
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Notes,
                    Style = (Style)tabs.FindResource("EditorTabIconStyle") });
                heading.Children.Add(new TextBlock { Text = "Synthetic tab " + i, Style = (Style)tabs.FindResource("EditorTabTitleStyle") });
                heading.Children.Add(new Button { Style = (Style)tabs.FindResource("EditorTabCloseButtonStyle"),
                    Content = new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Close, Width = 12, Height = 12 } });
                control.Items.Add(new TabItem { Header = heading });
            }
            tabs.Show(); Pump(350); control.SelectedIndex = 2; Pump(260);
            control.SelectedIndex = 0; Pump(260);
            var header = (Border)((TabItem)control.Items[1]).Template.FindName("HeaderBorder", (TabItem)control.Items[1]);
            StateSurfaceMotionBehavior.SetActiveState(header, true); Pump(170);
            SaveControlCapture((FrameworkElement)tabs.Content, "tabs-Dark-lt-" + mode);
            StateSurfaceMotionBehavior.SetActiveState(header, false); Pump(180);
            CheckOwnedChrome(tabs);
            tabs.Close(); Pump(500);
            Require(!RenderTransitionClock.IsAnyRunning, "Tabs left active motion work after close: " + mode);
            checks.Add(mode + ": compiled tab selection and close settle without idle motion tracks.");
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "A fixture queued a settings write.");
        File.WriteAllLines(Path.Combine(_output, "premium-checks.txt"), checks);
        File.WriteAllText(Path.Combine(_output, "render-samples.json"), System.Text.Json.JsonSerializer.Serialize(
            new { measurement = "Warmed offscreen WPF render and synchronous date-selection/layout durations; not presented FPS or input-to-photon latency.",
                renderingTier = RenderCapability.Tier >> 16, processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                samples = PremiumRenderSamples }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void CheckOwnedChrome(DependencyObject root)
    {
        foreach (var control in ControlTree(root).OfType<Control>().Where(c => c.IsVisible && c.Opacity > 0 &&
            c.GetType().Namespace is "System.Windows.Controls" or "System.Windows.Controls.Primitives"))
        {
            // These native templates are only content presenters, with no widget face to theme.
            if (control.GetType() == typeof(ContentControl) || control.GetType() == typeof(ItemsControl)) continue;
            if (control.Template is null) continue;
            if (control is CheckBox checkBox)
                Require(checkBox.Template.FindName("CheckBoxBorder", checkBox) is SurfaceMaterialBorder,
                    "An explicit BasedOn style resolved to framework checkbox chrome.");
            var source = DependencyPropertyHelper.GetValueSource(control, Control.TemplateProperty).BaseValueSource;
            Require(source is not BaseValueSource.DefaultStyle and not BaseValueSource.DefaultStyleTrigger,
                "Visible native chrome fallback: " + control.GetType().Name + " " + control.Name);
        }
    }

    private static void ProbePremiumCalendar(string theme, string language, List<string> checks)
    {
        var calendar = new Calendar { DisplayDate = new DateTime(2026, 10, 1), SelectedDate = new DateTime(2026, 10, 8),
            Style = (Style)_app.FindResource("EmbeddedCalendarControlStyle") };
        var host = new Window { Width = 320, Height = 310, Content = calendar, ShowInTaskbar = false };
        host.Show(); Pump(50); host.UpdateLayout();
        CheckOwnedChrome(calendar);
        var days = ControlTree(calendar).OfType<CalendarDayButton>().Where(b => !b.IsInactive && b.Content is not null).ToArray();
        foreach (var value in new[] { "1", "8", "10", "11", "20", "28", "31" })
        {
            var button = days.Single(b => Convert.ToString(b.Content, CultureInfo.CurrentCulture) == value);
            var label = ControlTree(button).OfType<CalendarDayLabel>().Single();
            var marker = (FrameworkElement)button.Template.FindName("DayMarker", button);
            var center = label.TranslatePoint(new Point(label.ActualWidth / 2, label.ActualHeight / 2), button);
            var circleCenter = marker.TranslatePoint(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2), button);
            Require((center - circleCenter).Length < .01 && marker.ActualWidth == marker.ActualHeight, "Calendar centers diverged: " + value);
            var dot = (FrameworkElement)button.Template.FindName("ScheduleMark", button);
            dot.Visibility = Visibility.Visible; host.UpdateLayout();
            Require((center - label.TranslatePoint(new Point(label.ActualWidth / 2, label.ActualHeight / 2), button)).Length < .01,
                "Schedule dot moved the numeral.");
            // Isolate the actual production label; its foreground is black only for the pixel measurement.
            label.SetCurrentValue(Control.ForegroundProperty, Brushes.Black);
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d }) CheckCalendarPixels(label, scale, value);
            label.ClearValue(Control.ForegroundProperty);
        }
        SaveControlCapture(calendar, $"calendar-{theme}-{language}");
        MeasurePremiumCalendar(calendar, theme, language);
        var dpi = VisualTreeHelper.GetDpi(calendar).DpiScaleX;
        host.Close(); Pump(30);
        checks.Add($"{theme}/{language}: native calendar marker/digit centers agree; seven single/double-digit pixel bounds within one physical pixel at 100/125/150/200%; schedule dots do not move labels. Live host DPI: {dpi:F2}.");
    }

    private static void MeasurePremiumCalendar(Calendar calendar, string theme, string language)
    {
        var dpi = VisualTreeHelper.GetDpi(calendar);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(calendar.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(calendar.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(calendar), null,
            new Rect(0, 0, calendar.ActualWidth, calendar.ActualHeight));
        bitmap.Render(visual); bitmap.Clear();
        var render = new List<double>(); var selection = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            calendar.SelectedDate = new DateTime(2026, 10, i % 2 == 0 ? 8 : 9); calendar.UpdateLayout();
            selection.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart(); bitmap.Render(visual); render.Add(watch.Elapsed.TotalMilliseconds); bitmap.Clear();
        }
        render.Sort(); selection.Sort();
        PremiumRenderSamples.Add(new { theme, language, dpi = dpi.DpiScaleX, iterations = 20,
            bitmapRenderMedianMs = render[10], bitmapRenderMaxMs = render[^1],
            selectionLayoutMedianMs = selection[10], selectionLayoutMaxMs = selection[^1] });
    }

    private static void CheckCalendarPixels(CalendarDayLabel label, double scale, string value)
    {
        var width = (int)Math.Round(label.ActualWidth * scale); var height = (int)Math.Round(label.ActualHeight * scale);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(label), null, new Rect(0, 0, label.ActualWidth, label.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var left = width; var right = -1; var top = height; var bottom = -1;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 24) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        Require(right >= left && Math.Abs((left + right + 1d) / 2 - width / 2d) <= 1 &&
            Math.Abs((top + bottom + 1d) / 2 - height / 2d) <= 1, $"Numeral {value} is off-center at {scale}: {left},{top}..{right},{bottom} / {width}x{height}");
    }

    private static MainViewModel PremiumLibraryFixture()
    {
        // These views consume only four read-only collections and INotifyPropertyChanged.
        // Avoid the application constructor's disk services, collection subscriptions and startup work.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var fields = typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var name in new[] { "FlashcardSets", "FlashcardTagFilters", "QuizTagFilters" })
        {
            var field = fields.Single(f => f.Name == "<" + name + ">k__BackingField");
            field.SetValue(model, Activator.CreateInstance(field.FieldType));
        }
        fields.Single(f => f.Name == "_quizzes").SetValue(model, new ObservableCollection<QuizViewModel>());
        for (var i = 0; i < 1000; i++)
        {
            model.FlashcardSets.Add(new FlashcardSetViewModel(new FlashcardSetDocument { Title = "Study collection " + i + " — a deliberately descriptive title",
                Cards = [new FlashcardItem { Question = "Question " + i, Answer = "Answer " + i }] }));
            model.Quizzes.Add(new QuizViewModel(new QuizDocument { Title = "Practice quiz " + i + " — a deliberately descriptive title",
                Questions = [new QuizQuestion { Question = "Question " + i }] }));
        }
        return model;
    }

    private static void ProbePremiumWindows(string theme, string language, List<string> checks)
    {
        var model = PremiumLibraryFixture();
        foreach (var window in new Window[] { new FlashcardCombineWindow(model), new QuizLibraryWindow(model) })
        {
            window.Show(); Pump(150); window.UpdateLayout();
            CheckOwnedChrome(window);
            CheckWorkspaceCaption(window);
            var library = ControlTree(window).OfType<ItemsControl>().First(i => i.Items.Count == 1000);
            Require(ControlTree(library).OfType<CheckBox>().Count() < 80, "Library virtualization was lost.");
            var check = ControlTree(library).OfType<CheckBox>().First();
            var provider = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle)!;
            provider.Toggle(); Pump(40);
            var title = ControlTree(window).OfType<TextBox>().First(t => t.Name.Length == 0);
            title.Text = "A synthetic combined title";
            var options = ControlTree(window).OfType<Expander>().Single(item => item.Name == "ConfigurationOptions");
            Require(options.IsExpanded, "Wide configuration should start expanded.");
            foreach (var width in new[] { 1180d, 680d, 920d, 680d })
            {
                window.Width = width; window.UpdateLayout(); Pump(35);
                Require(title.Text == "A synthetic combined title" && check.IsChecked == true, "Resize lost form or selection state.");
                var body = (Grid)ControlTree(window).OfType<FeatureWindowShell>().Single().Body;
                var compact = body.ActualWidth < 880;
                Require(Grid.GetRow((FrameworkElement)body.Children[0]) == (compact ? 1 : 0), "Library layout did not adapt.");
                var viewport = ControlTree(library).OfType<ScrollViewer>().First();
                Require(viewport.ViewportHeight > 20 && double.IsFinite(viewport.ViewportHeight), "Library viewport became unbounded.");
                if (compact) { options.IsExpanded = true; window.UpdateLayout(); Pump(35); }
            }
            options.IsExpanded = false; window.UpdateLayout();
            SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-compact");
            window.Width = 1180; window.UpdateLayout(); Pump(40);
            Require(options.IsExpanded, "Compact collapse leaked into the wide configuration state.");
            SaveControlCapture((FrameworkElement)window.Content, $"{window.GetType().Name}-{theme}-{language}-wide");
            var search = ControlTree(window).OfType<TextBox>().Single(t => t.Name.EndsWith("SearchTextBox"));
            var placeholder = ControlTree(window).OfType<TextBlock>().Single(t => t.Text == LocalizationService.GetString(
                window is FlashcardCombineWindow ? "PremiumSearchFlashcards" : "PremiumSearchQuizzes"));
            search.Focus(); Pump(25); Require(placeholder.IsVisible && placeholder.FontSize == 14,
                $"{window.GetType().Name}/{theme}/{language}: placeholder visible {placeholder.IsVisible}, font {placeholder.FontSize}, search '{search.Text}'.");
            search.Text = "Unmatched fixture phrase"; Pump(40); Require(!placeholder.IsVisible, "Nonempty search kept its placeholder.");
            search.Clear(); Pump(40); Require(placeholder.IsVisible, "Cleared search did not recover its placeholder.");
            window.Close(); Pump(40);
        }

        var stats = new FlashcardStatisticsWindow(() => new FlashcardSetDocument { Title = "Synthetic study collection", Cards = Enumerable.Range(0, 38)
            .Select(i => new FlashcardItem { Question = "Question " + i, Answer = "Answer " + i }).ToList() });
        stats.Show(); Pump(60); stats.UpdateLayout();
        CheckOwnedChrome(stats);
        var sections = ControlTree(stats).OfType<ResponsiveSectionPanel>().Single();
        Require(sections.ActualWidth >= sections.Breakpoint, "Default statistics window does not expose the two-column layout.");
        SaveControlCapture((FrameworkElement)stats.Content, $"statistics-compiled-{theme}-{language}-wide");
        stats.Width = 640; stats.UpdateLayout(); Pump(30);
        Require(sections.ActualWidth < sections.Breakpoint, "Statistics did not switch to one reading column.");
        SaveControlCapture((FrameworkElement)stats.Content, $"statistics-compiled-{theme}-{language}-compact"); stats.Close(); Pump(30);

        var schedule = new NoteSchedulePanel { Visibility = Visibility.Visible, IsHitTestVisible = true };
        var host = new Window { Width = 1000, Height = 700, Content = schedule };
        host.Show(); schedule.ShowAnimated(model, [], _ => { }, "Schedule"); Pump(60); host.UpdateLayout();
        var date = (DatePicker)schedule.FindName("ScheduleDatePicker");
        Require(date.Template.FindName("PART_Root", date) is Grid && date.Template.FindName("PART_TextBox", date) is DatePickerTextBox,
            "Native date-picker parts are missing.");
        var edit = (DatePickerTextBox)date.Template.FindName("PART_TextBox", date);
        edit.Text = new DateTime(2026, 10, 9).ToString("d", CultureInfo.CurrentCulture);
        ((Button)date.Template.FindName("PART_Button", date)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50);
        Require(date.SelectedDate?.Date == new DateTime(2026, 10, 9), "Native date entry did not commit.");
        Require(date.IsDropDownOpen && ((Popup)date.Template.FindName("PART_Popup", date)).Child is Calendar, "Native calendar popup did not open.");
        CheckOwnedChrome(host);
        CheckOwnedChrome(((Popup)date.Template.FindName("PART_Popup", date)).Child);
        date.IsDropDownOpen = false;
        SaveControlCapture((FrameworkElement)host.Content, $"schedule-{theme}-{language}"); host.Close(); Pump(30);

        var color = new ColorPickerDialog(); color.Show(); Pump(50);
        CheckOwnedChrome(color);
        var recentStyle = (Style)color.FindResource("RecentColorSwatchStyle");
        var swatch = new Button { Style = recentStyle, Background = Brushes.CornflowerBlue };
        AutomationProperties.SetName(swatch, "#6495ED");
        ((WrapPanel)color.FindName("RecentColorsPanel")).Children.Add(swatch);
        ((FrameworkElement)color.FindName("RecentColorsSection")).Visibility = Visibility.Visible;
        color.UpdateLayout(); var clicked = false; swatch.Click += (_, _) => clicked = true;
        ((IInvokeProvider)new ButtonAutomationPeer(swatch).GetPattern(PatternInterface.Invoke)!).Invoke(); Pump(25);
        Require(swatch.Focusable && swatch.FocusVisualStyle is not null && clicked && swatch.ActualWidth == 32,
            "Color swatch keyboard/automation contract failed.");
        SaveControlCapture((FrameworkElement)color.Content, $"color-picker-{theme}-{language}"); color.Close(); Pump(30);

        // The editor's production content-host template, with the native document engine and no app services.
        var source = XDocument.Load("Scriblucent/NoteEditorWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var styleMarkup = source.Root!.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(x + "Key") == "EditorRichTextBoxStyle");
        var standaloneStyle = new XElement(styleMarkup);
        standaloneStyle.Attribute(x + "Key")?.Remove();
        foreach (var declaration in source.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            standaloneStyle.SetAttributeValue(declaration.Name, declaration.Value);
        var editorStyle = (Style)System.Windows.Markup.XamlReader.Parse(standaloneStyle.ToString());
        var rich = new RichTextBox { Style = editorStyle, Document = new FlowDocument(new Paragraph(new Run("Synthetic editable content"))) };
        var editorHost = new Window { Width = 640, Height = 400, Content = rich }; editorHost.Show(); Pump(30);
        Require(rich.Template.FindName("PART_ContentHost", rich) is ScrollViewer, "Editor content host missing.");
        rich.CaretPosition = rich.Document.ContentEnd; rich.BeginChange(); rich.CaretPosition.InsertTextInRun(" appended"); rich.EndChange();
        Require(rich.CanUndo, "Native undo was lost."); rich.Undo();
        Require(!new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Contains("appended"), "Native undo did not restore content.");
        editorHost.Close(); Pump(30);
        checks.Add($"{theme}/{language}: compiled combination windows retain title/selection across repeated breakpoints, 1,000-item virtualization and bounded viewports; compiled statistics adapt; native localized date entry/popup works; swatches support focus/invoke; rich-text content host retains native editing/undo.");
    }
}
