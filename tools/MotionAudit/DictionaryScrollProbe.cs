using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeDictionaryScrolling()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var panel = new SettingsPanel();
            typeof(SettingsPanel).GetField("_isApplyingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, true);
            var itemType = typeof(SettingsPanel).GetNestedType("ManagedSpellCheckLanguageItem", BindingFlags.NonPublic)!;
            var dictionaries = (System.Collections.IList)typeof(SettingsPanel).GetField("_managedSpellCheckLanguages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            foreach (var language in SpellCheckDictionaryService.GetSupportedLanguages())
            {
                var item = Activator.CreateInstance(itemType, nonPublic: true)!;
                itemType.GetProperty("Key")!.SetValue(item, language.Key);
                itemType.GetProperty("DisplayName")!.SetValue(item, SpellCheckDictionaryService.GetFullLanguageLabel(language.Key));
                itemType.GetProperty("IsDownloaded")!.SetValue(item, dictionaries.Count % 2 == 0);
                dictionaries.Add(item);
            }
            ((CheckBox)panel.FindName("EnableSpellCheckCheckBox")).IsChecked = true;
            var motionHost = (FrameworkElement)panel.FindName("PanelMotionHost");
            motionHost.Opacity = 1; motionHost.RenderTransform = Transform.Identity;
            var window = new Window { Width = 980, Height = 800, Content = panel, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); Pump(80); window.UpdateLayout();
            var list = (ListBox)panel.FindName("SpellCheckLanguagesListBox");
            var outer = (ScrollViewer)panel.FindName("SettingsScrollViewer");
            foreach (var width in new[] { 980d, 480d })
            {
                window.Width = width; window.UpdateLayout(); Pump(80);
                list.BringIntoView(); window.UpdateLayout(); Pump(60);
                var inner = ControlTree(list).OfType<ScrollViewer>().First();
                Console.WriteLine($"{theme}, {width}: dictionary viewport={inner.ViewportHeight:F2}, extent={inner.ExtentHeight:F2}, physical={inner.ActualHeight:F2}, scroll unit={VirtualizingPanel.GetScrollUnit(list)}");
                Require(inner.ViewportHeight > 100 && inner.ScrollableHeight > 10, "Dictionary list is reporting item counts to pixel-based smooth scrolling.");
                foreach (var preference in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(preference, false);
                    inner.ScrollToTop(); window.UpdateLayout(); Pump(50);
                    var outerOffset = outer.VerticalOffset;
                    var before = inner.VerticalOffset;
                    inner.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    var last = before;
                    for (var i = 0; i < 20; i++)
                    {
                        Pump(25); window.UpdateLayout();
                        Require(inner.VerticalOffset >= last - .5, "Dictionary scrolling reversed while coasting.");
                        last = inner.VerticalOffset;
                    }
                    Require(inner.VerticalOffset > before + 10, "One wheel notch stalled in the dictionary list.");
                    Require(Math.Abs(outer.VerticalOffset - outerOffset) < 1, "Dictionary wheel input moved the outer Settings page.");
                    // Drive to both ends, then make sure every language can be reached and clicked.
                    inner.ScrollToEnd(); window.UpdateLayout(); Pump(80);
                    var lastRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
                    Require(lastRow != null && lastRow.IsVisible, "Last dictionary was not realized.");
                    var point = lastRow!.TranslatePoint(new Point(10, lastRow.ActualHeight / 2), inner);
                    Require(point.Y >= -1 && point.Y < inner.ActualHeight + 1, "Last dictionary escaped its viewport.");
                    Require(inner.InputHitTest(point) != null, "Dictionary row stayed unclickable after scrolling.");
                    inner.ScrollToTop(); window.UpdateLayout(); Pump(80);
                    Require(inner.VerticalOffset < 1, "Dictionary scrollbar could not return to the top.");
                    checks.Add($"{theme}/{width}/{preference}: pixel scrolling, monotonic wheel coast, independent outer page, reachable final language, restored hit testing.");
                }
                SaveControlCapture(panel, $"dictionaries-{theme}-{width}");
            }
            window.Close(); Pump(60);
        }
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        Require(!RenderTransitionClock.IsAnyRunning, "Dictionary fixture left motion observers running.");
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
