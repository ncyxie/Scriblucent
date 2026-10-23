using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeSettingsInput()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(mode, false);
                // Keep installed-font discovery deliberately pending while exercising input.
                var fonts = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fontTaskField = typeof(SettingsPanel).GetField("_templateFontNamesTask", BindingFlags.Static | BindingFlags.NonPublic)!;
                fontTaskField.SetValue(null, fonts.Task);
                var settings = new SettingsPanel { Visibility = Visibility.Collapsed, DataContext = new SettingsProbeModel() };
                typeof(SettingsPanel).GetField("_isApplyingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, true);
                var root = new Grid(); root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
                root.Children.Add(new TextBlock { Text = "Synthetic dashboard", Margin = new Thickness(20) });
                Panel.SetZIndex(settings, 200); root.Children.Add(settings);
                var window = new Window { Content = root, Width = 980, Height = 800, ShowActivated = false, ShowInTaskbar = false };
                ShellWindowChrome.TryAttach(window, ShellChromeKind.NativeAnimated);
                window.Show(); window.UpdateLayout(); Pump(80);
                var animator = (OverlayPanelAnimator)typeof(SettingsPanel).GetField("_overlayAnimator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
                try
                {
                    for (var cycle = 0; cycle < 2; cycle++)
                    {
                        settings.ShowAnimated();
                        if (mode == MotionPreference.Full && cycle == 0)
                        {
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                            var lease = typeof(OverlayPanelAnimator).GetField("_backdropRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animator);
                            Require(lease is not null, "Opening did not start its backdrop request.");
                            lease!.GetType().GetMethod("Restart", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lease, null);
                        }
                        Pump(1100); window.UpdateLayout();
                        var face = (FrameworkElement)settings.FindName("PanelMotionHost");
                        Require(face.IsHitTestVisible && face.Opacity > .99, $"{theme}/{mode}: Settings entrance is stuck: hit={face.IsHitTestVisible}, opacity={face.Opacity}.");
                        var nav = (WrapPanel)settings.FindName("CategoryNavigation");
                        var toggle = (CheckBox)settings.FindName("EnableScrollbarCheckBox");
                        var combo = ControlTree(settings).OfType<ComboBox>().First();
                        var close = ControlTree(settings).OfType<Button>().First(b => b.Style == settings.FindResource("OverlayCloseIconButtonStyle"));
                        foreach (var control in new FrameworkElement[] { (Button)nav.Children[0], toggle, combo, close })
                            CheckSettingsHit(window, control, theme, mode);
                        var initial = toggle.IsChecked;
                        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!).Toggle(); Pump(230);
                        Require(toggle.IsChecked != initial, "The Settings toggle did not respond.");
                        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!).Toggle();
                        var fontBox = (ComboBox)settings.FindName("DefaultNoteFontFamilyBox");
                        if (cycle == 0)
                        {
                            Require(!fonts.Task.IsCompleted, "Slow-font fixture did not remain pending.");
                            ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)!).Invoke(); Pump(400);
                            Require(settings.Visibility == Visibility.Collapsed && !fonts.Task.IsCompleted,
                                "Settings could not close while installed-font discovery was pending.");
                            settings.ShowAnimated(); Pump(1100); window.UpdateLayout();
                            CheckSettingsHit(window, close, theme, mode); CheckSettingsHit(window, toggle, theme, mode);
                            var selected = fontBox.Items.OfType<ComboBoxItem>().Last(); fontBox.SelectedItem = selected;
                            fonts.SetResult(Enumerable.Range(0, 96).Select(index => $"Synthetic installed font {index}").ToList());
                            var fontWait = System.Diagnostics.Stopwatch.StartNew();
                            while (fontBox.Items.Count < 96 && fontWait.ElapsedMilliseconds < 5000) Pump(16);
                            Require(ReferenceEquals(fontBox.SelectedItem, selected) && fontBox.Items.Count >= 96,
                                $"Background font discovery replaced the user's selection or failed to populate: count {fontBox.Items.Count}, selected preserved {ReferenceEquals(fontBox.SelectedItem, selected)}.");
                        }
                        var viewer = (ScrollViewer)settings.FindName("SettingsScrollViewer");
                        var data = (FrameworkElement)settings.FindName("SettingsSectionData");
                        var destination = Math.Clamp(data.TranslatePoint(new Point(), (UIElement)viewer.Content).Y, 0, viewer.ScrollableHeight);
                        ((Button)nav.Children[^1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var scrollWait = System.Diagnostics.Stopwatch.StartNew();
                        while (Math.Abs(viewer.VerticalOffset - destination) >= 4 && scrollWait.ElapsedMilliseconds < 5000) Pump(16);
                        // Section geometry can settle a few DIP as scrollbar/field layout rounds.
                        Require(Math.Abs(viewer.VerticalOffset - destination) < 4, $"Settings category navigation failed after opening: offset {viewer.VerticalOffset}, target {destination}, maximum {viewer.ScrollableHeight}.");
                        Scriblucent.Behaviors.SmoothScrollBehavior.CancelTarget(viewer); viewer.ScrollToTop(); Pump(250);
                        CheckSettingsHit(window, toggle, theme, mode);
                        ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)!).Invoke(); Pump(400);
                        Require(settings.Visibility == Visibility.Collapsed, "Settings Close did not finish.");
                        Console.WriteLine($"{theme}/{mode}: cycle {cycle + 1} responds.");
                    }
                    checks.Add($"{theme}/{mode}: animated open/reopen with real backdrop; pointer hits reach category, switch, dropdown and Close; switch and Close work while installed-font discovery is pending; batched font completion preserves selection; category scrolling responds; Full also restarts preparation before capture.");
                }
                finally { fonts.TrySetResult([]); window.Close(); Pump(120); fontTaskField.SetValue(null, null); }
            }
        }
        var discovery = Task.Run(() => (List<string>)typeof(SettingsPanel)
            .GetMethod("TemplateFontNames", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!discovery.IsCompleted && deadline.ElapsedMilliseconds < 10000) Pump(16);
        Require(discovery.IsCompletedSuccessfully && discovery.Result.Count > 0, "Installed fonts could not be discovered on a worker.");
        checks.Add($"Installed-font discovery completes on a worker: {discovery.Result.Count} names; no settings writes.");
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null, "Probe queued settings writes.");
        File.WriteAllLines(Path.Combine(_output, "settings-input-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void CheckSettingsHit(Window window, FrameworkElement control, string theme, MotionPreference mode)
    {
        var point = control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), window);
        var hit = window.InputHitTest(point) as DependencyObject;
        Require(hit is not null && ControlTree(control).Contains(hit),
            $"{theme}/{mode}: {control.GetType().Name}/{control.Name} blocked by {hit?.GetType().Name}/{(hit as FrameworkElement)?.Name}, enabled={control.IsEnabled}, hit={control.IsHitTestVisible}, bounds={control.RenderSize}, point={point}.");
    }
}
