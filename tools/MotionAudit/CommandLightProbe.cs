using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeCommandLight()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Automatic, false);
            var window = new Scriblucent.NoteEditorTabsWindow { ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false };
            try
            {
                var tabs = (TabControl)window.FindName("EditorTabs");
                foreach (var title in new[] { "Memory and learning", "Research notes", "Project ideas" })
                {
                    var header = new StackPanel { Orientation = Orientation.Horizontal };
                    header.Children.Add(new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Notes,
                        Style = (Style)window.FindResource("EditorTabIconStyle") });
                    header.Children.Add(new TextBlock { Text = title, Style = (Style)window.FindResource("EditorTabTitleStyle") });
                    header.Children.Add(new Button { Style = (Style)window.FindResource("EditorTabCloseButtonStyle"),
                        Content = new Scriblucent.Icons.AppIcon { Kind = Scriblucent.Icons.AppIconKind.Close, Width = 10, Height = 10 } });
                    tabs.Items.Add(new TabItem { Header = header });
                }
                window.Show(); Pump(380);
                var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs);
                var editor = WorkspaceEditorFixture(); host.Content = editor; window.UpdateLayout(); Pump(80);
                var rich = ControlTree(editor).OfType<RichTextBox>().Single();
                rich.Document = new FlowDocument { FontSize = 17, Blocks = {
                    new Paragraph(new Bold(new Run("Memory and learning"))),
                    new Paragraph(new Run("A practical guide to learning that lasts.")),
                    new Paragraph(new Run("Retrieval practice strengthens memory. Explain the idea in your own words, then revisit it after a few days.")) } };
                ControlTree(editor).OfType<TextBox>().Single(t => t.Name == "TitleTextBox").Text = "Memory and learning";
                rich.CaretPosition = rich.Document.ContentEnd;
                rich.BeginChange(); rich.CaretPosition.InsertTextInRun(" Keep this edit."); rich.EndChange();
                var chrome = ControlTree(editor).OfType<Border>().Single(b => b.Name == "EditorChrome");
                var plate = (TabSelectionSurface)tabs.Template.FindName("TabSelectionPlate", tabs);
                var selected = (TabItem)tabs.SelectedItem;
                var headerFace = (Border)selected.Template.FindName("HeaderBorder", selected);
                Require(CommandLightBehavior.GetIsScope(tabs) && CommandLightBehavior.LightFor(chrome) is not null
                    && CommandLightBehavior.LightFor(plate) is not null, "Compiled editor did not attach the shared light.");
                Require(CommandLightBehavior.GetIsSelected(headerFace) && !StateSurfaceMotionBehavior.GetIsEnabled(headerFace),
                    "The selected header has a second hover wash.");
                var inactive = (TabItem)tabs.Items[1];
                Require(((Border)inactive.Template.FindName("HeaderBorder", inactive)).CornerRadius == new CornerRadius(10),
                    "The inactive hover face has sharp lower corners.");
                CommandLightBehavior.PreviewPointer(tabs, new Point(), false); Pump(260);
                CheckTabFieldVisibility(window, editor);
                SaveControlCapture((FrameworkElement)window.Content, $"command-rest-{theme}-{language}");
                var status = ControlTree(editor).OfType<EditorTitleStatus>().Single(text => text.Name == "EditedIndicatorText");
                status.Text = LocalizationService.GetString("EditedIndicator");
                status.Visibility = Visibility.Visible; status.Opacity = 1;
                var close = ((StackPanel)selected.Header).Children.OfType<Button>().Single();
                var closeFace = (InteractionSurface)close.Template.FindName("ButtonBorder", close);
                closeFace.AnimateState(true, false, false);
                window.UpdateLayout();
                SaveControlCapture((FrameworkElement)window.Content, $"command-close-hover-edited-{theme}-{language}");
                closeFace.AnimateState(false, false, false);
                status.ShowSaved(() => status.SetEdited(false));
                window.UpdateLayout();
                SaveControlCapture((FrameworkElement)window.Content, $"command-saved-status-{theme}-{language}");
                status.Visibility = Visibility.Hidden;
                UiMotion.CancelInlineStatusFlash(status);
                var pointer = selected.TranslatePoint(new Point(selected.ActualWidth * .62, selected.ActualHeight - 8), tabs);
                CommandLightBehavior.PreviewPointer(tabs, pointer, true); Pump(230);
                var chromeLight = CommandLightBehavior.LightFor(chrome)!; var tabLight = CommandLightBehavior.LightFor(plate)!;
                Require((chrome.TranslatePoint(new Point(chromeLight.X, chromeLight.Y), tabs) - pointer).Length < .001
                    && (plate.TranslatePoint(new Point(tabLight.X, tabLight.Y), tabs) - pointer).Length < .001,
                    "The light jumps between connected faces.");
                SaveControlCapture((FrameworkElement)window.Content, $"command-tab-hover-{theme}-{language}");
                pointer = chrome.TranslatePoint(new Point(chrome.ActualWidth * .72, chrome.ActualHeight - 14), tabs);
                CommandLightBehavior.PreviewPointer(tabs, pointer, true); Pump(20);
                SaveControlCapture((FrameworkElement)window.Content, $"command-toolbar-hover-{theme}-{language}");
                CommandLightBehavior.PreviewPointer(tabs, pointer, false); Pump(260);
                var switchStart = System.Diagnostics.Stopwatch.StartNew();
                tabs.SelectedIndex = 1; Pump(25);
                Require(NativeSurfaceMotion.IsRunning(tabs, MotionChannel.CommandLight),
                    $"Selection did not coordinate the chrome light: elapsed {switchStart.ElapsedMilliseconds} ms, quality {MotionPolicy.Quality}, selected {tabs.SelectedIndex}, strength {chromeLight.Strength:F3}, plate {plate.Bounds}, enabled {TabSelectionPlateBehavior.GetIsEnabled(tabs)}.");
                var destination = ((TabItem)tabs.SelectedItem).TranslatePoint(new Point(), plate);
                Require(Math.Abs(plate.Bounds.X - destination.X) > 1, "Header layout teleported the selection face before its glide.");
                SaveControlCapture((FrameworkElement)window.Content, $"command-switch-{theme}-{language}");
                tabs.SelectedIndex = 2; Pump(380);
                for (var attempt = 0; attempt < 12 && NativeSurfaceMotion.IsRunning(tabs, MotionChannel.CommandLight); attempt++) Pump(25);
                Require(!NativeSurfaceMotion.IsRunning(tabs, MotionChannel.CommandLight) && ReferenceEquals(host.Content, editor) && rich.CanUndo,
                    $"Interrupted selection: clock {NativeSurfaceMotion.IsRunning(tabs, MotionChannel.CommandLight)}, same content {ReferenceEquals(host.Content, editor)}, undo {rich.CanUndo}.");
                foreach (var mode in new[] { MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(mode, false);
                    CommandLightBehavior.PreviewPointer(tabs, pointer, true); Pump(140);
                    tabs.SelectedIndex = 0; Pump(150);
                    Require(!NativeSurfaceMotion.IsRunning(tabs, MotionChannel.CommandLight), "Reduced/Off retained a selection light clock.");
                    if (mode == MotionPreference.Off) Require(chromeLight.Strength == 0 && tabLight.Strength == 0, "Off retained a pointer light.");
                    SaveControlCapture((FrameworkElement)window.Content, $"command-{mode}-{theme}-{language}");
                }
                checks.Add($"{theme}/{language}: compiled tab styles and production editor markup; one pointer coordinate across chrome/plate; selected header has no duplicate wash; interruptible selection, retained editor/undo; Reduced and Off settle without spatial light clocks. Input is fed to the production controller without moving the desktop pointer.");
            }
            finally { window.Close(); Pump(450); }
            Require(!RenderTransitionClock.IsAnyRunning, "Command light left clocks after close.");
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Command fixtures queued a settings write.");
        File.WriteAllLines(Path.Combine(_output, "command-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void CheckTabFieldVisibility(Window window, FrameworkElement editor)
    {
        var field = (Border)editor.FindName("EditorTitleField");
        var root = (FrameworkElement)window.Content;
        // Compare the field's unoccluded top with the same pixels through the complete workspace.
        // The old foreground connector replaced these inset-field pixels with a square white patch.
        var fieldOrigin = field.TranslatePoint(new Point(), root);
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            uint Pixel(FrameworkElement element, Point point)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
                    (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                image.Render(element);
                var pixel = new byte[4];
                image.CopyPixels(new Int32Rect((int)Math.Floor(point.X * scale), (int)Math.Floor(point.Y * scale), 1, 1), pixel, 4, 0);
                return BitConverter.ToUInt32(pixel);
            }
            var point = new Point(70, 3);
            var expected = Pixel(field, point);
            var actual = Pixel(root, fieldOrigin + (Vector)point);
            // Fractional translation can differ by one channel value through antialiasing.
            for (var channel = 0; channel < 3; channel++)
                Require(Math.Abs((int)((expected >> (channel * 8)) & 255) - (int)((actual >> (channel * 8)) & 255)) <= 2,
                    $"The tab connector covers its title field at {scale:P0}: {expected:X8} vs {actual:X8}.");
        }
    }
}
