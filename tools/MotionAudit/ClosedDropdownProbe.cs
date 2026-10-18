using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using System.Windows.Data;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeClosedDropdowns()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = Path.Combine(Directory.GetCurrentDirectory(), "Scriblucent");
        var declarations = Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(path => XDocument.Load(path).Descendants(ui + "ComboBox").Select(control => (File: path, Control: control))).ToArray();
        var report = new List<string>(); var failures = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var culture in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme); LocalizationService.SetCulture(culture); MotionPolicy.Configure(MotionPreference.Off, false);
            var stack = new StackPanel { Margin = new Thickness(24), Width = 430 };
            var cases = new List<(string Name, ComboBox Combo, string[] Choices)>();
            foreach (var declaration in declarations)
            {
                var control = declaration.Control; string name = (string?)control.Attribute(x + "Name") ?? "unnamed";
                string file = Path.GetFileName(declaration.File);
                string Resolve(string text) => Regex.Match(text, @"^\{Binding \[([^]]+)\]") is { Success: true } match ? LocalizationService.GetString(match.Groups[1].Value) : text;
                var choices = control.Elements(ui + "ComboBoxItem").Select(item => Resolve((string?)item.Attribute("Content") ?? "")).Where(text => text.Length > 0).ToList();
                if (file == "CalendarPlannerView.xaml" && name == "TypeFilter") choices.AddRange(new[] { "CalendarAllTypes", "NotesDashboard", "FlashcardsDashboard", "MindMapsDashboard", "QuizzesDashboard" }.Select(LocalizationService.GetString));
                if (choices.Count == 0) { report.Add($"{theme}/{culture}: {file}/{name}: variable/dynamic content, shared ellipsis policy."); continue; }
                string style = (string?)control.Attribute("Style") ?? "";
                bool toolbar = style.Contains("EditorFont") || style.Contains("MindMapLayout") || style.Contains("QuizEditor") || (string?)control.Attribute("Height") == "32";
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
                if (toolbar) combo.Style = (Style)_app.FindResource("FeatureToolbarComboBoxStyle");
                else if (style.Contains("ChromeComboBoxStyle") || style.Contains("InsetComboBoxStyle")) combo.Style = (Style)_app.FindResource("ChromeComboBoxStyle");
                bool fitted = control.Attributes().Any(attribute => attribute.Name.LocalName == "DropdownChoiceWidthBehavior.IsEnabled" && attribute.Value == "True");
                if (fitted) DropdownChoiceWidthBehavior.SetIsEnabled(combo, true);
                else if (double.TryParse((string?)control.Attribute("Width"), NumberStyles.Float, CultureInfo.InvariantCulture, out double width)) combo.Width = width;
                else if ((string?)control.Attribute("Width") is { } resource && Regex.Match(resource, @"^\{StaticResource ([^}]+)\}") is { Success: true } resourceMatch)
                    combo.Width = (double)_app.FindResource(resourceMatch.Groups[1].Value);
                else combo.Width = 350;
                if (double.TryParse((string?)control.Attribute("MinWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minimum)) combo.MinWidth = minimum;
                if (toolbar) combo.Height = 32;
                foreach (var text in choices.Distinct()) combo.Items.Add(new ComboBoxItem { Content = text });
                combo.SelectedIndex = 0; stack.Children.Add(combo); cases.Add(($"{file}/{name}", combo, choices.Distinct().ToArray()));
            }
            using (var fixture = new CompletionWindow(new Window { Content = new ScrollViewer { Content = stack }, Width = 520, Height = 800, UseLayoutRounding = true, ShowInTaskbar = false }))
            {
                foreach (var item in cases)
                {
                    double widest = 0, initialWidth = item.Combo.ActualWidth;
                    for (int index = 0; index < item.Choices.Length; index++)
                    {
                        item.Combo.SelectedIndex = index; Pump(5); item.Combo.UpdateLayout();
                        var presenter = ControlTree(item.Combo).OfType<ContentPresenter>().First(element => ReferenceEquals(element.TemplatedParent, item.Combo));
                        var label = ControlTree(presenter).OfType<TextBlock>().First();
                        var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                        widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
                        if (text.WidthIncludingTrailingWhitespace > label.ActualWidth + .5 || text.Height > label.ActualHeight + 1)
                            failures.Add($"{theme}/{culture}: {item.Name}: '{label.Text}' needs {text.WidthIncludingTrailingWhitespace:F1}x{text.Height:F1}, gets {label.ActualWidth:F1}x{label.ActualHeight:F1}; button={item.Combo.ActualWidth:F1}.");
                        Require(Math.Abs(initialWidth - item.Combo.ActualWidth) < .1, "Closed dropdown changed width with its selection.");
                    }
                    report.Add($"{theme}/{culture}: {item.Name}: {item.Choices.Length} choices, widest={widest:F1}, button={initialWidth:F1}.");
                }
                SaveControlCapture(stack, "closed-choices-" + theme + "-" + culture);
            }
            ProbeVariableClosedLabels(theme, culture, report);
        }
        ProbeAdaptiveChoiceChanges(report);
        ProbeActualClosedToolbar(report);
        File.WriteAllLines(Path.Combine(_output, "closed-dropdown-audit.txt"), report.Concat(failures));
        foreach (var failure in failures) Console.WriteLine(failure);
        Require(failures.Count == 0, $"{failures.Count} fixed-choice closed dropdown labels do not fit; see closed-dropdown-audit.txt.");
        Console.WriteLine($"Closed dropdown audit passed: {declarations.Length} XAML declarations, both languages and themes; measured choice widths fit without selection resizing; live language/content/font changes resize in both directions; variable names retain bounded ellipsis.");
    }

    private static void ProbeVariableClosedLabels(string theme, string culture, List<string> report)
    {
        const string title = "A long document title that cannot reasonably fit inside a compact toolbar selector";
        var native = new ComboBox { Width = 180, ItemsSource = new[] { title, "Second short title", title + " again" }, SelectedIndex = 0 };
        var owner = new ComboBox { Width = 260, ItemsSource = new[] { new { Title = title, OwnerDescription = "Additional description belongs in the open menu" } }, SelectedIndex = 0,
            ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel><TextBlock Text='{Binding Title}'/><TextBlock Text='{Binding OwnerDescription}'/></StackPanel></DataTemplate>") };
        ClosedDropdownLabelBehavior.SetDisplayMemberPath(owner, "Title");
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(native); panel.Children.Add(owner);
        var settings = new SettingsPanel();
        var model = new ComboBox { Width = 280, ItemTemplate = ((ComboBox)settings.FindName("FlashcardModelBox")).ItemTemplate,
            ItemsSource = new[] { new { Label = "A long model name that must yield room to its badges", ShowBetaBadge = true, ShowClozeOnlyBadge = true } }, SelectedIndex = 0 };
        panel.Children.Add(model);
        using var fixture = new CompletionWindow(new Window { Content = panel, Width = 360, Height = 260, UseLayoutRounding = true, ShowInTaskbar = false });
        for (int index = 0; index < native.Items.Count; index++)
        {
            native.SelectedIndex = index; Pump(30);
            var label = ControlTree(native).OfType<TextBlock>().First();
            Require(label.TextTrimming == TextTrimming.CharacterEllipsis && label.TextWrapping == TextWrapping.NoWrap, "Changing a selection lost the closed-label ellipsis policy.");
        }
        var ownerLabel = ControlTree(owner).OfType<TextBlock>().Single();
        Require(ownerLabel.Text == title && ownerLabel.TextWrapping == TextWrapping.NoWrap, "Rich reminder option was squeezed into a multi-line closed button.");
        var originalTemplate = owner.ItemTemplate; owner.IsDropDownOpen = true; Pump(80);
        Require(ReferenceEquals(originalTemplate, owner.ItemTemplate), "Closed label replaced the rich popup item template."); owner.IsDropDownOpen = false;
        var modelLabel = ControlTree(model).OfType<TextBlock>().First(label => label.Text.StartsWith("A long model"));
        Require(modelLabel.TextTrimming == TextTrimming.CharacterEllipsis && modelLabel.ActualWidth < 220 && modelLabel.ActualHeight < 30,
            "Compound model label still overflows behind its badges.");
        SaveControlCapture(panel, "closed-variable-" + theme + "-" + culture);
        report.Add($"{theme}/{culture}: native long labels retain ellipsis across changes; reminder picker uses Title only and preserves its rich open-item template.");
    }

    private static void ProbeActualClosedToolbar(List<string> report)
    {
        var editor = new Scriblucent.NoteEditorWindow { Width = 860, Height = 760, ShowInTaskbar = false };
        try
        {
            editor.Show(); Pump(550); RemainingCall(editor, "SetFontPanelOpen", true, false, false); Pump(200);
            var panel = (FrameworkElement)editor.FindName("FontPanel");
            foreach (string name in new[] { "FontFamilyBox", "FontSizeBox", "LineSpacingBox", "IndentSizeBox" })
            {
                var combo = (ComboBox)editor.FindName(name);
                if (!combo.IsVisible) continue;
                var bounds = combo.TranslatePoint(new Point(), panel);
                Require(bounds.X >= 0 && bounds.X + combo.ActualWidth <= panel.ActualWidth + 1, name + " overflows the real narrow formatting toolbar.");
            }
            SaveControlCapture(panel, "closed-real-toolbar-narrow");
            report.Add("Real Note editor at 860 DIP: fitted formatting selectors remain inside the responsive toolbar.");
        }
        finally { editor.DisposeWorkspaceEditor(); Pump(150); }
    }

    private static void ProbeAdaptiveChoiceChanges(List<string> report)
    {
        LocalizationService.SetCulture("en");
        var shape = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        foreach (string key in new[] { "ShapeRounded", "ShapeRectangle", "ShapeCircle", "ShapeEllipse" })
        {
            var option = new ComboBoxItem();
            BindingOperations.SetBinding(option, ContentControl.ContentProperty, new Binding("[" + key + "]") { Source = LocalizationProvider.Instance });
            shape.Items.Add(option);
        }
        DropdownChoiceWidthBehavior.SetIsEnabled(shape, true); shape.SelectedIndex = 0;
        var unit = new ComboBox { ItemsSource = new[] { "0.25 in", "0.5 in", "1.5 in" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) };
        DropdownChoiceWidthBehavior.SetIsEnabled(unit, true);
        var filter = new ComboBox { ItemsSource = new[] { "CalendarAllTypes", "NotesDashboard", "FlashcardsDashboard", "MindMapsDashboard", "QuizzesDashboard" }.Select(LocalizationService.GetString).ToArray(), SelectedIndex = 0, Height = 32, MinHeight = 32 };
        DropdownChoiceWidthBehavior.SetIsEnabled(filter, true);
        var header = new CalendarPlannerHeaderPanel { Width = 400 }; header.Children.Add(new Border()); header.Children.Add(filter);
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(shape); panel.Children.Add(unit); panel.Children.Add(header);
        using var fixture = new CompletionWindow(new Window { Content = panel, Width = 500, Height = 330, UseLayoutRounding = true, ShowInTaskbar = false });
        double english = shape.ActualWidth, imperial = unit.ActualWidth;
        Require(header.ActualHeight == 32, "English calendar filter retained the longer-language wrap threshold.");
        LocalizationService.SetCulture("lt");
        filter.ItemsSource = new[] { "CalendarAllTypes", "NotesDashboard", "FlashcardsDashboard", "MindMapsDashboard", "QuizzesDashboard" }.Select(LocalizationService.GetString).ToArray();
        Pump(120);
        double lithuanian = shape.ActualWidth;
        Require(lithuanian > english + 20 && header.ActualHeight == 72, "Live Lithuanian labels did not grow the selector/calendar wrap threshold.");
        LocalizationService.SetCulture("en"); Pump(100);
        Require(Math.Abs(shape.ActualWidth - english) < .1, "Returning to shorter English labels did not release width.");
        unit.ItemsSource = new[] { "0.64 cm", "1.27 cm", "3.81 cm" }; Pump(100);
        double metric = unit.ActualWidth;
        unit.SelectedIndex = 2; Pump(40); Require(Math.Abs(unit.ActualWidth - metric) < .1, "Selection changed the fitted width.");
        unit.ItemsSource = new[] { "1 in" }; Pump(80); Require(unit.ActualWidth < metric, "A shorter choice set did not release unused width.");
        unit.ItemsSource = new[] { "0.64 cm", "1.27 cm", "3.81 cm" }; Pump(80); Require(Math.Abs(unit.ActualWidth - metric) < .1, "Restoring metric options did not restore their measured width.");
        shape.FontSize = 18; Pump(80); Require(shape.ActualWidth > english, "A larger control font did not recalculate choice width.");
        shape.FontSize = 14; Pump(80); Require(Math.Abs(shape.ActualWidth - english) < .1, "Restoring typography did not restore the compact width.");
        var content = (ComboBoxItem)shape.Items[0]; string original = (string)content.Content;
        content.Content = new string('W', 1000); Pump(80);
        Require(shape.ActualWidth <= DropdownChoiceWidthBehavior.GetMaxFitWidth(shape) + 1, "An extreme choice expanded beyond the bounded fit width.");
        content.Content = original; Pump(80); Require(Math.Abs(shape.ActualWidth - english) < .1, "Shortening an option did not shrink its selector.");
        panel.Children.Remove(shape); Pump(40);
        content.Content = "Short"; Pump(40);
        panel.Children.Insert(0, shape); Pump(100);
        Require(shape.ActualWidth < english, "A reloaded selector did not remeasure its updated choices.");
        SaveControlCapture(panel, "adaptive-choice-live");
        report.Add($"Live changes: shape en={english:F1}, lt={lithuanian:F1}; indentation imperial={imperial:F1}, metric={metric:F1}. Language, content and typography changes grow/shrink; selection stays stable; calendar wrap follows measured language; extreme names are capped; reload reattaches observation.");
    }
}
