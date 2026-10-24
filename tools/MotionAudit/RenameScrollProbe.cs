using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeRenameScrolling(bool baseline)
    {
        var checks = new List<string>();
        foreach (var theme in baseline ? new[] { "Dark" } : new[] { "Dark", "Light" })
        foreach (var mode in baseline ? new[] { MotionPreference.Full } : new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        foreach (var count in baseline ? new[] { 12 } : new[] { 12, 60 })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(theme == "Dark" ? "lt" : "en");
            MotionPolicy.Configure(mode, false);
            var targets = Enumerable.Range(0, count).Select(i => new RenameTarget(Guid.NewGuid(), "Original " + i)).ToArray();
            var dialog = new BulkRenameDialog(targets);
            DriveRenameDialog(current =>
            {
                var preview = (ItemsControl)current.FindName("PreviewList");
                var viewer = ControlTree(preview).OfType<ScrollViewer>().Single();
                var expected = viewer.ViewportHeight * .23;
                void Wheel(int delta)
                {
                    var source = ControlTree(preview).OfType<TextBlock>().First(block => block.IsVisible);
                    var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    source.RaiseEvent(args);
                    Require(args.Handled, "Preview did not handle wheel input.");
                }
                Wheel(-120);
                var positions = new List<double>();
                for (int i = 0; i < 12; i++) { Pump(50); positions.Add(viewer.VerticalOffset); }
                Console.WriteLine($"{theme}/{mode}/{count}: viewport={viewer.ViewportHeight:F2}, extent={viewer.ExtentHeight:F2}, wheel={viewer.VerticalOffset:F2}, expected={expected:F2}");
                if (!baseline)
                {
                    Require(viewer.ViewportHeight > 100, "Preview uses row counts instead of pixel scroll units.");
                    Require(positions.Zip(positions.Skip(1)).All(pair => pair.Second >= pair.First - .1), "Wheel coast reversed or bounced.");
                    Require(viewer.VerticalOffset > 0 && viewer.VerticalOffset < viewer.ScrollableHeight, "One wheel tick stalled or skipped straight to the end.");
                    Require(Math.Abs(viewer.VerticalOffset - expected) < 2, "Wheel travel did not match the shared pixel scroll behavior.");
                    Wheel(120); Pump(650);
                    Require(viewer.VerticalOffset < 1, "Wheel could not reverse to the top.");
                    viewer.ScrollToEnd(); current.UpdateLayout(); Pump(100);
                    Require(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 1, "Scrollbar could not reach the end.");
                    var last = (FrameworkElement?)preview.ItemContainerGenerator.ContainerFromIndex(count - 1);
                    Require(last is { IsVisible: true }, "Final preview row was not realized.");
                    Require(ControlTree(preview).OfType<ScrollContentPresenter>().Single().Clip is not null, "Scrolling lost its rounded inner viewport.");
                    if (count == 60) Require(ControlTree(preview).OfType<ScrollContentPresenter>().Single().ActualHeight < viewer.ExtentHeight && preview.ItemContainerGenerator.ContainerFromIndex(0) is null, "Preview lost virtualization.");
                    viewer.ScrollToTop(); current.UpdateLayout(); Pump(80);
                    Require(viewer.VerticalOffset < 1, "Scrollbar could not return to the top.");
                    ((TextBox)current.FindName("NameBox")).Text = "Renamed";
                    current.UpdateLayout(); Pump(100);
                    Require(current.Changes.Count == count, "Preview stopped updating after scrolling.");
                    Wheel(-120); Pump(650);
                    Require(viewer.VerticalOffset > 1, "Scrolling stopped after editing the rename rule.");
                    checks.Add($"{theme}/{mode}/{count}: wheel, reverse, scrollbar endpoints, virtualization, rounded viewport and preview refresh passed.");
                }
                RemainingCall(current, "CancelClick", current, new RoutedEventArgs());
            }, () => dialog.ShowDialog());
            Require(targets[0].Name == "Original 0", "Scroll fixture mutated an item.");
        }
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
