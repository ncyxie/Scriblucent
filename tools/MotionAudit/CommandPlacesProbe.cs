using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeCommandPlaces()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Full, false);
            ProbeCommandNavigation(theme, language, checks);
            ProbeCommandSettings(theme, language, checks);
            ProbeCommandChoices(theme, language, checks);
            ProbeCommandFamilies(theme, language, checks);
            Require(!RenderTransitionClock.IsAnyRunning, "Command-place fixtures left clocks after close.");
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Command-place fixtures queued settings writes.");
        File.WriteAllLines(Path.Combine(_output, "placement-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static Window CommandWindow(FrameworkElement content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height,
            ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false };
        window.SetResourceReference(Control.BackgroundProperty, "WorkspaceBackdrop");
        window.SetResourceReference(Control.ForegroundProperty, "TextColor");
        window.Show(); window.UpdateLayout(); Pump(80); return window;
    }
    private static void PointCommand(FrameworkElement scope, ButtonBase button)
    {
        var face = ControlTree(button).OfType<InteractionSurface>().Single();
        face.AnimateState(true, false, false);
        var width = double.IsFinite(face.PaintedWidth) ? face.PaintedWidth : face.ActualWidth;
        CommandLightBehavior.PreviewPointer(scope, face.TranslatePoint(new Point(width * .65, face.ActualHeight * .65), scope), true);
        Pump(220);
        Require(ReferenceEquals(CommandLightBehavior.LightFor(button), CommandLightBehavior.LightFor(face)), "The light attached to a decorative border instead of the interactive face.");
    }
    private static void ProbeCommandNavigation(string theme, string language, List<string> checks)
    {
        var group = LoadSidebarNavigation(); group.Width = 200; group.VerticalAlignment = VerticalAlignment.Top;
        var root = new Border { Padding = new Thickness(20), Child = group };
        root.SetResourceReference(Border.BackgroundProperty, "SidebarBackground");
        var window = CommandWindow(root, 280, 320);
        try
        {
            var buttons = ControlTree(group).OfType<Button>().ToArray();
            var faces = buttons.Select(button => ControlTree(button).OfType<InteractionSurface>().Single()).ToArray();
            var plate = ControlTree(group).OfType<SidebarSelectionSurface>().Single();
            foreach (var face in faces) face.PaintedWidth = 200; plate.PaintedWidth = 200;
            SidebarNavMotionBehavior.SetIsActive(buttons[0], true); Pump(260);
            PointCommand(group, buttons[1]); var large = CommandLightBehavior.LightFor(buttons[1])!.RadiusX;
            SaveControlCapture(root, $"sidebar-expanded-{theme}-{language}");
            foreach (var label in ControlTree(group).OfType<TextBlock>()) label.Visibility = Visibility.Collapsed;
            foreach (var face in faces) face.PaintedWidth = 48; plate.PaintedWidth = 48; group.Width = 48;
            window.Width = 128; window.UpdateLayout(); Pump(50); PointCommand(group, buttons[1]);
            var small = CommandLightBehavior.LightFor(buttons[1])!.RadiusX;
            Require(small < 60 && large > small * 2, "Collapsed sidebar retained an expanded light footprint.");
            SaveControlCapture(root, $"sidebar-collapsed-{theme}-{language}");
            SidebarNavMotionBehavior.SetIsActive(buttons[0], false); SidebarNavMotionBehavior.SetIsActive(buttons[3], true); Pump(300);
            Require(plate.Visibility == Visibility.Visible && Math.Abs(UiMotion.EnsureTranslate(plate).Y - buttons[3].TranslatePoint(new Point(), group).Y) < .1,
                "Shared cursor feedback broke the sidebar selection glide.");
            foreach (var mode in new[] { MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(mode, false); PointCommand(group, buttons[1]);
                var light = CommandLightBehavior.LightFor(buttons[1])!;
                Require(light.Strength == 0, "Reduced/Off retained sidebar cursor glow.");
                Require(mode != MotionPreference.Reduced || Math.Abs(light.X - 24) < .1, "Reduced feedback missed the visible collapsed face.");
                SaveControlCapture(root, $"sidebar-{mode}-{theme}-{language}");
            }
            checks.Add($"{theme}/{language}: production sidebar styles; expanded/collapsed footprint {large:F1}/{small:F1} DIP radius; shared selection glide, Reduced/Off and no full-sidebar paint.");
        }
        finally { window.Close(); Pump(100); }
    }
    private static void ProbeCommandSettings(string theme, string language, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Full, false);
        var settings = new SettingsPanel();
        typeof(SettingsPanel).GetField("_isApplyingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, true);
        settings.DataContext = new SettingsProbeModel();
        var window = CommandWindow(settings, 980, 800);
        try
        {
            var motion = (FrameworkElement)settings.FindName("PanelMotionHost"); motion.Opacity = 1; motion.RenderTransform = Transform.Identity;
            var nav = (WrapPanel)settings.FindName("CategoryNavigation"); var buttons = nav.Children.OfType<Button>().ToArray();
            var narrow = buttons.MinBy(button => button.ActualWidth)!; var wide = buttons.MaxBy(button => button.ActualWidth)!;
            PointCommand(nav, narrow); var small = CommandLightBehavior.LightFor(narrow)!.RadiusX;
            SaveControlCapture(settings, $"settings-short-choice-{theme}-{language}");
            ControlTree(narrow).OfType<InteractionSurface>().Single().AnimateState(false, false, false);
            PointCommand(nav, wide); var large = CommandLightBehavior.LightFor(wide)!.RadiusX;
            Require(large > small, "Settings labels retained a fixed-size light.");
            SaveControlCapture(settings, $"settings-long-choice-{theme}-{language}");
            MotionPolicy.Configure(MotionPreference.Off, false);
            buttons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            var viewer = (ScrollViewer)settings.FindName("SettingsScrollViewer");
            var section = (FrameworkElement)settings.FindName((string)buttons[^1].Tag);
            var expectedOffset = Math.Clamp(section.TranslatePoint(new Point(), (UIElement)viewer.Content).Y, 0, viewer.ScrollableHeight);
            Require(Math.Abs(viewer.VerticalOffset - expectedOffset) <= 2, "Settings navigation no longer follows its sections.");
            window.Width = 480; window.UpdateLayout(); Pump(80); MotionPolicy.Configure(MotionPreference.Full, false);
            PointCommand(nav, narrow); SaveControlCapture(settings, $"settings-wrapped-{theme}-{language}");
            checks.Add($"{theme}/{language}: compiled Settings categories; content-sized radii {small:F1}/{large:F1}; narrow wrapped layout and section scrolling; population/save handlers suppressed.");
        }
        finally { window.Close(); Pump(100); }
    }
    private static void ProbeCommandChoices(string theme, string language, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Full, false);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, style) in new[] { ("Cancel", "DialogSecondaryButtonStyle"), ("Delete", "DialogDangerButtonStyle"), ("Save", "DialogPrimaryButtonStyle") })
            actions.Children.Add(new Button { Content = text, Style = (Style)_app.FindResource(style), Margin = new Thickness(8, 0, 0, 0) });
        var shell = new OverlayDialogShell { Header = new TextBlock { Text = "Choose an action", FontSize = 22 },
            Body = new TextBlock { Text = "Feedback belongs to these related choices; the message stays quiet.", Margin = new Thickness(0, 12, 0, 24) }, Footer = actions };
        var window = CommandWindow(shell, 820, 420);
        try
        {
            var presenter = ControlTree(shell).OfType<ContentPresenter>().Single(p => ReferenceEquals(p.Content, actions));
            PointCommand(presenter, (Button)actions.Children[1]);
            Require(CommandLightBehavior.LightFor((FrameworkElement)shell.Body) is null, "A dialog message gained a pointer light.");
            Require(CommandLightBehavior.LightFor((Button)actions.Children[1])!.RadiusY < 80, "Compact dialog action gained a large wash.");
            SaveControlCapture(shell, $"dialog-actions-{theme}-{language}");
        }
        finally { window.Close(); Pump(100); }
        var row = new Border { Style = (Style)_app.FindResource("FeatureCombineItemBorderStyle"), Width = 400, Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "A selectable set\n12 flashcards", FontSize = 15 } };
        var rowHost = new Grid(); rowHost.Children.Add(row); rowHost.SetResourceReference(Panel.BackgroundProperty, "WorkspaceBackdrop");
        window = CommandWindow(rowHost, 500, 160);
        try
        {
            StateSurfaceMotionBehavior.SetIsSelected(row, true);
            CommandLightBehavior.PreviewPointer(row, new Point(100, 30), true); Pump(230);
            Require(CommandLightBehavior.LightFor(row)!.Strength < .5 && StateSurfaceMotionBehavior.GetIsSelected(row), "Local light replaced row selection or became too strong.");
            Require(row.RenderTransform == Transform.Identity, "A dense row gained hover travel.");
            SaveControlCapture(rowHost, $"selectable-row-{theme}-{language}");
            checks.Add($"{theme}/{language}: production dialog button styles in shared shell; compact footer-only feedback, semantic filled colours; production selectable-row style keeps selection and stationary local feedback.");
        }
        finally { window.Close(); Pump(100); }
    }

    private static void ProbeCommandFamilies(string theme, string language, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Full, false);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.SetResourceReference(Panel.BackgroundProperty, "WorkspaceBackdrop");
        root.Children.Add(new TextBlock { Text = "Control families · the same material wherever used", FontSize = 18 });
        var actions = new WrapPanel { Margin = new Thickness(0, 20, 0, 16) };
        foreach (var (text, style) in new[] { ("Ordinary", ""), ("Standalone Close", "DialogSecondaryButtonStyle"),
            ("Toolbar", "ToolbarIconButtonStyle"), ("Editor", "EditorToolButtonBaseStyle"), ("Delete", "DialogDangerButtonStyle"), ("+", "FabStyle") })
        {
            var button = new Button { Content = text, Width = text == "+" ? 52 : text == "Standalone Close" ? 180 : 132, Height = 42, Margin = new Thickness(0, 0, 8, 0) };
            if (style.Length != 0) button.Style = (Style)_app.FindResource(style);
            actions.Children.Add(button);
        }
        root.Children.Add(actions);
        var plain = new ListBox { Width = 370, Height = 50, HorizontalAlignment = HorizontalAlignment.Left };
        plain.Items.Add("A selectable list item"); root.Children.Add(plain);
        var choice = new ComboBoxItem { Content = "A dropdown choice", FontSize = 14, MinHeight = 40, Width = 370, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(choice);
        var menu = new MenuItem { Header = "A menu command", Width = 370, Height = 40, HorizontalAlignment = HorizontalAlignment.Left,
            Template = (ControlTemplate)_app.FindResource("MenuItemLeafTemplate") };
        root.Children.Add(menu);
        var day = new CalendarDayButton { Content = "17", Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)_app.FindResource("CalendarDayButtonStyle"), Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(day);
        var window = CommandWindow(root, 990, 430);
        try
        {
            foreach (var button in actions.Children.OfType<Button>())
            {
                Require(CommandLightBehavior.LightFor(button) is not null, $"{button.Content}: a button family missed the default light.");
                PointCommand(button, button);
                Require(CommandLightBehavior.LightFor(button)!.RadiusY < 80, "A standalone action gained an oversized light.");
            }
            foreach (var control in new FrameworkElement[] { plain, choice, menu })
            {
                var face = ControlTree(control).OfType<Border>().Single(border => StateSurfaceMotionBehavior.GetIsEnabled(border));
                Require(CommandLightBehavior.GetRole(face) == CommandLightRole.Item && CommandLightBehavior.LightFor(face) is not null,
                    "A list/dropdown/menu row missed the shared row treatment.");
                CommandLightBehavior.PreviewPointer(face, new Point(95, face.ActualHeight / 2), true); Pump(230);
                Require(CommandLightBehavior.LightFor(face)!.Strength is > 0 and < .5 && face.RenderTransform == Transform.Identity,
                    "A row became stronger than an action or gained hover travel.");
            }
            PointCommand(day, day);
            var dayFace = ControlTree(day).OfType<InteractionSurface>().Single();
            var marker = (System.Windows.Shapes.Rectangle)day.Template.FindName("DayMarker", day);
            Require(dayFace.CornerRadius == new CornerRadius(marker.RadiusX) && dayFace.ActualWidth == marker.ActualWidth && dayFace.ActualHeight == marker.ActualHeight,
                "Calendar feedback escaped the rounded day face.");
            SaveControlCapture(root, $"control-families-{theme}-{language}");
            foreach (var preference in new[] { MotionPreference.Reduced, MotionPreference.Off })
            {
                MotionPolicy.Configure(preference, false);
                foreach (var button in actions.Children.OfType<Button>())
                    Require(CommandLightBehavior.LightFor(button)!.Strength == 0, "Reduced/Off retained a shared button cursor glow.");
            }
            checks.Add($"{theme}/{language}: ordinary, lone footer, toolbar, editor, semantic and floating buttons acquire feedback without feature opt-ins; list, dropdown and menu rows share the quiet controller; calendar feedback fits its rounded face; Reduced/Off respected.");
        }
        finally { window.Close(); Pump(100); }
    }
}
