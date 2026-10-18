using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void InvokeOrderingButton(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void ProbeDirectItemSteps(ItemsControl list)
    {
        var original = list.Items.Cast<object>().ToArray();
        var moving = original[1];
        ItemReorderControls Controls() => ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(moving))
            .OfType<ItemReorderControls>().First(control => ReferenceEquals(ItemReorderBehavior.FindReorderList(control), list));
        Pump(100);
        Require(Controls().EarlierButton.IsEnabled, "Direct earlier arrow disabled a valid step.");
        InvokeOrderingButton(Controls().EarlierButton); Pump(300);
        Require(ReferenceEquals(list.Items[0], moving) && !Controls().EarlierButton.IsEnabled, "Direct arrow did not move immediately or failed to update its boundary.");
        InvokeOrderingButton(Controls().LaterButton); Pump(300);
        Require(original.SequenceEqual(list.Items.Cast<object>()), "Direct arrows could not restore the authored order.");
    }

    private static void ProbeSettingsOrderingOnly(string theme)
    {
        LoadNativeControlResources(theme);
        var (window, model, _) = LoadingWindow(theme, MotionPreference.Automatic, 0);
        try
        {
            window.Show(); WaitLoading(() => model.IsActiveDashboardReady); Pump(300);
            var report = new List<string>();
            ProbeSettingsOrdering(window, theme, MotionPreference.Automatic, report);
            File.WriteAllLines(Path.Combine(_output, "ordering-settings.txt"), report);
            foreach (var line in report) Console.WriteLine(line);
        }
        finally { CloseLoadingWindow(window); }
    }

    private static void ProbeSettingsOrdering(MainWindow window, string theme, MotionPreference mode, List<string> report)
    {
        var panel = (SettingsPanel)window.FindName("SettingsPanelControl");
        try
        {
            var list = (ListBox)panel.FindName("AiToolsListBox");
            panel.ShowAnimated();
            WaitLoading(() => list.Items.Count >= 2 && ((FrameworkElement)panel.FindName("PanelMotionHost")).Opacity >= .999);
            Pump(120);
            // Reveal the configuration list for the fixture without enabling any AI backend.
            UiMotion.AnimateLayoutReveal((FrameworkElement)panel.FindName("AiToolsDetailPanel"), true, animate: false);
            Pump(400); window.UpdateLayout();
            Require(list.Items.Count >= 2 && ItemReorderBehavior.GetKind(list) == ItemReorderKind.AiTools, "Settings tools did not attach shared ordering.");
            list.BringIntoView(); Pump(350); window.UpdateLayout();
            var originals = list.Items.Cast<object>().ToArray();
            var second = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
            var grip = ControlTree(second).OfType<ItemReorderGrip>().Single();
            InvokeOrderingButton(grip); Pump(100);
            Require(grip.ContextMenu.IsOpen && grip.ContextMenu.Items.Count == 2, "Settings grip click did not open its move menu.");
            ((MenuItem)grip.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); grip.ContextMenu.IsOpen = false; Pump(350);
            Require(ReferenceEquals(list.Items[0], originals[1]) && ReferenceEquals(list.SelectedItem, originals[1]), "Settings move menu did not preserve the moved selection.");
            var selectedGrip = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).OfType<ItemReorderGrip>().Single();
            Require(ItemReorderBehavior.MoveFromHandle(selectedGrip, true), "Settings keyboard-step route did not move the tool."); Pump(350);
            Require(originals.SequenceEqual(list.Items.Cast<object>()), "Settings precise moves disagreed with each other.");
            var direct = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1)).OfType<ItemReorderControls>().Single();
            Require(direct.EarlierButton.IsEnabled, "Settings direct arrow is unavailable on the second item.");
            InvokeOrderingButton(direct.EarlierButton); Pump(220);
            Require(ReferenceEquals(list.Items[0], originals[1]), "Settings direct arrow required another interaction to move.");
            direct = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).OfType<ItemReorderControls>().Single();
            InvokeOrderingButton(direct.LaterButton); Pump(220);
            Require(originals.SequenceEqual(list.Items.Cast<object>()), "Settings direct arrows could not restore the order.");
            var adapter = panel.CreateAiToolReorderAdapter();
            var keys = originals.Select(item => (string)item.GetType().GetProperty("Key")!.GetValue(item)!).ToArray();
            var enabled = originals.Select(item => (bool)item.GetType().GetProperty("IsEnabled")!.GetValue(item)!).ToArray();
            // Re-applying a checked binding must not save and refresh the same rows forever.
            for (int refresh = 0; refresh < 3; refresh++) { list.Items.Refresh(); Pump(120); window.UpdateLayout(); }
            var switchItem = originals.First(item => (bool)item.GetType().GetProperty("IsSupported")!.GetValue(item)!
                && (!(bool)item.GetType().GetProperty("IsEnabled")!.GetValue(item)! || enabled.Count(value => value) > 1));
            int switchIndex = Array.IndexOf(originals, switchItem);
            void ToggleTool()
            {
                var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(switchItem);
                var toggle = ControlTree(row).OfType<CheckBox>().Single();
                ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!).Toggle();
                Pump(160);
            }
            ToggleTool(); AppSettingsService.Flush();
            Require((bool)switchItem.GetType().GetProperty("IsEnabled")!.GetValue(switchItem)! != enabled[switchIndex]
                && AppSettingsService.Load().AiTools.Single(tool => tool.Key == keys[switchIndex]).IsEnabled != enabled[switchIndex], "Settings switch stopped applying real changes.");
            ToggleTool();
            Require((bool)switchItem.GetType().GetProperty("IsEnabled")!.GetValue(switchItem)! == enabled[switchIndex], "Settings switch could not restore its state.");
            if (_pointerControls)
            {
                list.BringIntoView(); Pump(220); window.UpdateLayout();
                ProbeReorderPointer(list, originals[1], originals[0], $"pointer-{theme}-{mode}-settings-tool");
            }
            else Require(adapter.Move([originals[1]], originals[0], false), "Settings insertion route did not move the tool.");
            AppSettingsService.Flush();
            var saved = AppSettingsService.Load().AiTools;
            Require(saved[0].Key == keys[1] && saved[1].Key == keys[0], "Settings ordering did not persist its new sequence.");
            Require(originals.Select(item => (bool)item.GetType().GetProperty("IsEnabled")!.GetValue(item)!).SequenceEqual(enabled), "Settings ordering changed enabled states.");
            Require(adapter.Move([originals[1]], originals[0], true), "Settings ordering could not restore its original sequence."); Pump(300);
            foreach (double width in new[] { 1180d, 780d })
            {
                double previous = window.Width;
                window.Width = width; Pump(300); list.BringIntoView(); Pump(200); window.UpdateLayout();
                foreach (var row in ControlTree(list).OfType<ItemReorderGrip>())
                    Require(row.ActualWidth == 24 && row.ActualHeight == 24, "Settings handle changed size in compact layout.");
                SaveControlCapture(list, $"ordering-tools-{theme}-{mode}-{width}");
                window.Width = previous; Pump(200);
            }
            for (int cycle = 0; cycle < 2; cycle++)
            {
                object previous = list.Items[0];
                panel.HideAnimated(); Pump(500); panel.ShowAnimated();
                WaitLoading(() => list.Items.Count >= 2 && !ReferenceEquals(list.Items[0], previous)
                    && ((FrameworkElement)panel.FindName("PanelMotionHost")).Opacity >= .999);
                Pump(150); window.UpdateLayout();
            }
            report.Add($"{theme}/{mode}: Settings uses the shared grip/menu/step/insertion routes, retains selection and enabled states, persists tool order and fits wide/compact layouts; repeated row refresh/opening stays stable and actual switch changes persist.");
        }
        finally { panel.HideAnimated(); Pump(500); }
    }
}
