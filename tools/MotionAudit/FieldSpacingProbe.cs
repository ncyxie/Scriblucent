using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeFieldSpacing()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            MotionPolicy.Configure(MotionPreference.Automatic, false);
            var stack = new StackPanel { Margin = new Thickness(24), Width = 420 };
            var plain = new TextBox { Text = "Ordinary field" };
            var embedded = new TextBox { Text = "Wrapped field", Style = (Style)_app.FindResource("EmbeddedFieldTextBoxStyle") };
            var shell = new Border { Style = (Style)_app.FindResource("ChromeInsetFieldBorderStyle"), Child = embedded };
            var multiline = new TextBox { Text = "Multiline field\nSecond line", AcceptsReturn = true, Height = 80 };
            var embeddedMultiline = new TextBox { Text = "Wrapped multiline field\nSecond line", AcceptsReturn = true, Style = (Style)_app.FindResource("EmbeddedFieldTextBoxStyle") };
            var multilineShell = new Border { Style = (Style)_app.FindResource("ChromeMultilineFieldBorderStyle"), Child = embeddedMultiline, Height = 80 };
            foreach (var element in new FrameworkElement[] { plain, shell, multiline, multilineShell })
            { element.Margin = new Thickness(0, 0, 0, 12); stack.Children.Add(element); }
            var combos = new List<ComboBox>();
            foreach (var key in new[] { "", "ChromeComboBoxStyle", "InsetEditableComboBoxStyle" })
            {
                var combo = new ComboBox { ItemsSource = new[] { "Dropdown field", "Other choice" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
                if (key.Length > 0) combo.Style = (Style)_app.FindResource(key);
                combos.Add(combo); stack.Children.Add(combo);
            }
            using (var fixture = new CompletionWindow(new Window { Width = 520, Height = 650, Content = stack, ShowInTaskbar = false, UseLayoutRounding = true, SnapsToDevicePixels = true }))
            {
                double InputX(TextBox box, FrameworkElement owner) => box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, owner).X;
                double InputY(TextBox box, FrameworkElement owner) => box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, owner).Y;
                double expected = InputX(plain, plain);
                double rounding = .5 / System.Windows.Media.VisualTreeHelper.GetDpi(plain).DpiScaleX + .01;
                // TextBoxView rounds its native caret gutter separately from a wrapped Border's
                // padding. Allow one physical pixel at fractional DPI, never another field inset.
                double pixel = 1 / System.Windows.Media.VisualTreeHelper.GetDpi(plain).DpiScaleX + .01;
                Require(Math.Abs(expected - 12 - plain.BorderThickness.Left) <= rounding + 1 && Math.Abs(InputX(embedded, shell) - expected) <= pixel,
                    $"{theme} standalone/wrapped field text insets differ: plain={InputX(plain, plain)}, wrapped={InputX(embedded, shell)}, expected={expected}, padding={plain.Padding}, shell={shell.Padding}, border={plain.BorderThickness}.");
                Require(Math.Abs(InputX(multiline, multiline) - expected) <= pixel && Math.Abs(InputX(embeddedMultiline, multilineShell) - expected) <= pixel,
                    theme + " multiline horizontal insets differ.");
                Require(Math.Abs(InputY(multiline, multiline) - 8 - multiline.BorderThickness.Top) <= rounding + 1 &&
                    Math.Abs(InputY(embeddedMultiline, multilineShell) - InputY(multiline, multiline)) <= pixel,
                    theme + " multiline vertical padding is missing or doubled.");
                foreach (var combo in combos)
                {
                    var content = combo.IsEditable
                        ? (FrameworkElement)combo.Template.FindName("PART_EditableTextBox", combo)
                        : ControlTree(combo).OfType<ContentPresenter>().First(item => ReferenceEquals(item.TemplatedParent, combo));
                    double textX = content is TextBox editBox ? InputX(editBox, combo) : content.TranslatePoint(new Point(), combo).X;
                    Require(Math.Abs(textX - expected) <= pixel && Math.Abs(combo.ActualHeight - plain.ActualHeight) < .1,
                        $"{theme} dropdown text inset or form height differs from text input: x={textX}, expected={expected}, comboHeight={combo.ActualHeight}, fieldHeight={plain.ActualHeight}.");
                    if (content is TextBox editable)
                        Require(Math.Abs(InputX(editable, combo) - expected) <= pixel, theme + " editable dropdown caret does not align with text inputs.");
                    combo.IsDropDownOpen = true; Pump(80); Require(combo.IsDropDownOpen, "Dropdown did not open after inset changes."); combo.IsDropDownOpen = false;
                }
                SaveControlCapture(stack, "field-spacing-" + theme);
                checks.Add(theme + ": standalone, wrapped, multiline and editable/noneditable dropdown text align; multiline padding is not doubled; form heights match and dropdowns open.");
            }

            var quiz = new QuizPreviewWindow(new QuizDocument { Title = "Quiet question container", Questions = [new QuizQuestion { Question = "Question field" }] }, noteOptions: null)
                { Width = 980, Height = 760, ShowInTaskbar = false };
            try
            {
                quiz.Show(); quiz.IsEditMode = true; Pump(300);
                var frame = ControlTree((ItemsControl)quiz.FindName("QuestionsItemsControl")).OfType<CollectionSelectionFrame>().First();
                Require(!frame.IsHoverTintEnabled, "Quiz question still enables a whole-card hover tint.");
                typeof(CollectionSelectionFrame).GetMethod("PlayHover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(frame, [true, true]); Pump(250);
                Require(!ControlTree(frame).Any(element => element.GetType().FullName == "Scriblucent.Animations.SurfaceLight+Light"), "Quiz hover painted the whole question area.");
                var input = ControlTree(frame).OfType<TextBox>().First(box => box.AcceptsReturn);
                input.Focus(); Pump(80); Require(input.IsKeyboardFocused, "Quiz field stopped accepting focus.");
                quiz.MarkCurrentStateAutoSaved();
                checks.Add(theme + ": quiz question hover creates no full-card wash; its editable field retains keyboard focus.");
            }
            finally { quiz.DisposeWorkspaceEditor(); Pump(100); }
        }
        File.WriteAllLines(Path.Combine(_output, "field-spacing-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
