using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Rendering;

internal static partial class Program
{
    private static Window HostSurfaceFixture(FrameworkElement content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); return window;
    }
    private static void ProbeColoredSurfaces()
    {
        var checks = new List<string>(); var costs = new List<object>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            foreach (var preference in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(preference, false);
                var gallery = BuildSurfaceGallery();
                var window = HostSurfaceFixture(gallery, 940, 820); Pump(250); window.UpdateLayout();
                try
                {
                    var buttons = ControlTree(gallery).OfType<Button>().Where(b => b.Tag is string).ToArray();
                    Require(buttons.Length == 16, "Colored action gallery is incomplete.");
                    foreach (var button in buttons)
                    {
                        var clicked = 0; button.Click += (_, _) => clicked++;
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke(); Pump(5);
                        Require(clicked == 1, "Native action invocation changed.");
                        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
                        Require(SurfaceMaterial.GetKind(face) == SurfaceMaterial.GetKind(button), "Native button did not pass its material preset to its face.");
                        var baseColor = ((SolidColorBrush)button.Background).Color;
                        if (SurfaceMaterial.GetKind(face) == SurfaceMaterialKind.Filled)
                        {
                            var foreground = ((SolidColorBrush)button.Foreground).Color;
                            foreach (var brush in new[] { button.Background, face.HoverBackground!, face.PressedBackground! })
                            {
                                var material = SurfaceMaterialPalette.Get(((SolidColorBrush)brush).Color, SurfaceMaterialKind.Filled, theme == "Dark", foreground);
                                foreach (var color in new[] { material.Colors.Top, material.Colors.Body, material.Colors.Bottom })
                                    Require(SurfaceMaterialPalette.Contrast(color, foreground) >= 4.5, "An action label loses contrast in a material state.");
                            }
                        }
                        Require(baseColor == ((SolidColorBrush)button.Background).Color, "Material replaced a native background binding.");
                        var size = button.RenderSize;
                        var pose = ((string)button.Tag!).Split(':')[0];
                        face.AnimateState(pose is "Hover" or "Press", pose == "Press", false);
                        Require(button.RenderSize == size, "Surface feedback changed the native hit-root size.");
                    }
                    var calendar = ControlTree(gallery).OfType<Calendar>().Single();
                    var selected = ControlTree(calendar).OfType<CalendarDayButton>().Single(b => b.IsSelected);
                    var item = ControlTree(calendar).OfType<CalendarItem>().Single();
                    var plate = (SurfaceMaterialBorder)item.Template.FindName("CalendarSelectionPlate", item);
                    var schedule = (Border)selected.Template.FindName("ScheduleMark", selected);
                    Require(plate.IsVisible && SurfaceMaterial.GetKind(plate) == SurfaceMaterialKind.Tinted && plate.BorderThickness == new Thickness(2), "Calendar selection lost its shared material and rim.");
                    Require(schedule.Visibility == Visibility.Visible && ((SolidColorBrush)schedule.Background).Color == ((SolidColorBrush)_app.FindResource("AccentPrimary")).Color, "Selected reminder density is no longer readable.");
                    Require(ControlTree(calendar).OfType<CalendarDayButton>().Any(b => b.IsToday), "Today's native state is missing.");
                    SaveControlCapture(gallery, $"surfaces-{theme}-{preference}");
                    checks.Add($"{theme}/{preference}: 16 native actions, semantic hover/press palettes and contrast, stable hit roots, calendar selection/today/schedule states and four material families.");
                }
                finally { window.Close(); Pump(40); }
            }
            // Paired warm raster captures isolate the added paint on the same control fixture.
            MotionPolicy.Configure(MotionPreference.Off, false);
            foreach (var renderer in new[] { "flat", "attached", "direct" })
            {
                var material = renderer != "flat";
                var panel = new WrapPanel { Width = 900 };
                for (var i = 0; i < 48; i++)
                {
                    var chip = renderer == "attached" ? new Border() : NewMaterialFace();
                    chip.Width = 140; chip.Height = 36; chip.Margin = new(4); chip.CornerRadius = new(10);
                    chip.Background = (Brush)_app.FindResource("FlashcardKnownChipBackground");
                    if (material) SurfaceMaterial.SetKind(chip, SurfaceMaterialKind.Tinted);
                    panel.Children.Add(chip);
                }
                var window = HostSurfaceFixture(panel, 940, 500); Pump(60); window.UpdateLayout();
                try
                {
                    _ = Snapshot(panel, 1); var samples = new List<double>();
                    for (var i = 0; i < 30; i++) { var watch = Stopwatch.StartNew(); _ = Snapshot(panel, 1); watch.Stop(); samples.Add(watch.Elapsed.TotalMilliseconds); }
                    var sorted = samples.Order().ToArray();
                    costs.Add(new { theme, material, renderer, surfaces = 48, warmCaptureMedianMs = (sorted[14] + sorted[15]) / 2, warmCaptureP95Ms = sorted[28], warmCaptureMaxMs = sorted[^1], samples });
                }
                finally { window.Close(); Pump(30); }
            }
        }
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        Require(!RenderTransitionClock.IsAnyRunning, "Closed surface fixtures left frame observers behind.");
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        File.WriteAllText(Path.Combine(_output, "capture-costs.json"), JsonSerializer.Serialize(costs, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static Grid BuildSurfaceGallery()
    {
        var root = new Grid { Width = 900, Height = 740, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = (Brush)_app.FindResource("WindowBackground") };
        var stack = new StackPanel { Margin = new(20) }; root.Children.Add(stack);
        TextBlock Label(string text) => new() { Text = text, Foreground = (Brush)_app.FindResource("TextColor"), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 8) };
        stack.Children.Add(new TextBlock { Text = "Colored surfaces", Foreground = (Brush)_app.FindResource("TextColor"), FontSize = 24, FontWeight = FontWeights.SemiBold });
        foreach (var pose in new[] { "Flat", "Rest", "Hover", "Press" })
        {
            stack.Children.Add(Label(pose == "Flat" ? "Flat reference" : pose)); var row = new UniformGrid { Columns = 4 };
            foreach (var tone in new[] { "Primary", "Success", "Warning", "Danger" })
            {
                var button = new Button { Content = tone switch { "Primary" => "Save changes", "Success" => "Begin study", "Warning" => "Review changes", _ => "Delete item" },
                    Style = (Style)_app.FindResource("Dialog" + tone + "ButtonStyle"), Margin = new(0, 0, 12, 0), Tag = pose + ":" + tone };
                if (pose == "Flat") SurfaceMaterial.SetKind(button, SurfaceMaterialKind.None);
                row.Children.Add(button);
            }
            stack.Children.Add(row);
        }
        stack.Children.Add(Label("Status surfaces and data fills"));
        var statuses = new UniformGrid { Columns = 4 };
        foreach (var status in new[] { "New", "Unknown", "Known", "Category" })
        {
            var body = new StackPanel { Margin = new(0, 0, 12, 0) };
            var label = new TextBlock { Text = status switch { "Unknown" => "Learning · 8", "Known" => "Mature · 12", "New" => "New · 18", _ => "Biology" }, FontSize = 14, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Flashcard" + status + "ChipForeground");
            var chip = new Border { CornerRadius = new(10), Padding = new(12, 10, 12, 10), Child = label };
            chip.SetResourceReference(Border.BackgroundProperty, "Flashcard" + status + "ChipBackground"); SurfaceMaterial.SetKind(chip, SurfaceMaterialKind.Tinted); body.Children.Add(chip);
            var bar = new Border { Height = 10, CornerRadius = new(5), Margin = new(0, 8, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, status == "Known" ? "AccentSuccess" : status == "Unknown" ? "AccentWarning" : "AccentPrimary");
            SurfaceMaterial.SetKind(bar, SurfaceMaterialKind.Data); body.Children.Add(bar); statuses.Children.Add(body);
        }
        stack.Children.Add(statuses);
        stack.Children.Add(Label("Calendar and native preference states"));
        var details = new Grid(); details.ColumnDefinitions.Add(new() { Width = new(280) }); details.ColumnDefinitions.Add(new());
        var calendar = new Calendar { Style = (Style)_app.FindResource("EmbeddedCalendarControlStyle"), DisplayDate = DateTime.Today, SelectedDate = DateTime.Today.AddDays(1) };
        CalendarScheduleMarkBehavior.SetScheduledDates(calendar, new[] { DateTime.Today, DateTime.Today.AddDays(1) }); details.Children.Add(calendar);
        var controls = new StackPanel { Margin = new(24, 10, 0, 0) }; Grid.SetColumn(controls, 1);
        controls.Children.Add(new CheckBox { Content = "Expressive animation", IsChecked = true, Style = (Style)_app.FindResource("SettingsSwitchStyle"), Margin = new(0, 0, 0, 12) });
        controls.Children.Add(new Slider { Value = 65, Minimum = 0, Maximum = 100, Style = (Style)_app.FindResource("ModernSliderStyle"), Margin = new(0, 0, 0, 12) });
        controls.Children.Add(new CheckBox { Content = "Selected option", IsChecked = true });
        details.Children.Add(controls); stack.Children.Add(details);
        return root;
    }
}
