using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Reflection;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Converters;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class ControlRevampTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception e) { failure = e; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class Host : IDisposable
    {
        internal Window Window { get; }
        internal Host(FrameworkElement content, string theme = "Light", Action<Window>? beforeShow = null)
        {
            Window = new Window { Width = 760, Height = 580, Content = content, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" })
                Window.Resources.MergedDictionaries.Add(ControlTestResources.Load(name));
            beforeShow?.Invoke(Window);
            Window.Show(); Window.UpdateLayout(); Pump(35);
        }
        public void Dispose() { Window.Close(); Pump(30); }
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void SeekNative(UIElement element, MotionChannel channel, int ms)
    {
        Assert.True(NativeSurfaceMotion.IsRunning(element, channel));
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [element, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var state = ((System.Collections.IDictionary)args[1]!)[channel]!;
        var tracks = (IReadOnlyList<(NativeMotionTrack, AnimationClock)>)state.GetType().GetField("Tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        foreach (var (_, clock) in tracks) { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime); }
    }

    [Theory]
    [InlineData(BakedElevationLayer.ElevationKind.Dialog)]
    [InlineData(BakedElevationLayer.ElevationKind.Button)]
    [InlineData(BakedElevationLayer.ElevationKind.ButtonCard)]
    [InlineData(BakedElevationLayer.ElevationKind.Paper)]
    public void Baked_shadow_stays_attached_to_face_when_content_is_remeasured(BakedElevationLayer.ElevationKind kind) => Sta(() =>
    {
        var shadow = new BakedElevationLayer { Kind = kind };
        var face = new Border { Width = 560, Height = 278.4, Background = Brushes.White };
        var frame = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        frame.Children.Add(shadow); frame.Children.Add(face);
        using var host = new Host(frame, beforeShow: window => window.UseLayoutRounding = true);
        var sizes = new List<Size> { new(560, 278.4), new(360, 180), new(640, 320) };
        if (kind is BakedElevationLayer.ElevationKind.Button or BakedElevationLayer.ElevationKind.ButtonCard)
            sizes.Add(new Size(160, 40));
        foreach (var size in sizes)
        {
            face.Width = size.Width; face.Height = size.Height;
            host.Window.UpdateLayout(); Pump(35);
            var tolerance = 1 / VisualTreeHelper.GetDpi(shadow).DpiScaleX + .01;
            Assert.InRange(Math.Abs(frame.ActualWidth - face.ActualWidth), 0, tolerance);
            Assert.InRange(Math.Abs(frame.ActualHeight - face.ActualHeight), 0, tolerance);
            var slices = Tree(shadow).OfType<Image>().ToArray();
            Assert.Equal(8, slices.Length);
            foreach (var slice in slices)
            {
                var bounds = slice.TransformToAncestor(shadow).TransformBounds(new Rect(slice.RenderSize));
                Assert.InRange(bounds.Left, -tolerance, shadow.ActualWidth + tolerance);
                Assert.InRange(bounds.Top, -tolerance, shadow.ActualHeight + tolerance);
                Assert.InRange(bounds.Right, 0, shadow.ActualWidth + tolerance);
                Assert.InRange(bounds.Bottom, 0, shadow.ActualHeight + tolerance);
                if (slice.Name.StartsWith("Bottom", StringComparison.Ordinal))
                    Assert.InRange(Math.Abs(bounds.Bottom - shadow.ActualHeight), 0, tolerance);
            }
        }
    });

    [Theory]
    [InlineData("Light", "")] [InlineData("Dark", "EditableComboBoxStyle")]
    [InlineData("Light", "ChromeComboBoxStyle")] [InlineData("Dark", "SetSelectorComboBoxStyle")]
    [InlineData("Light", "LinkedNoteComboBoxStyle")]
    public void Dropdown_padding_is_honored_and_density_matches_native_popup(string theme, string key) => Sta(() =>
    {
        var combo = new ComboBox { Width = 240, Padding = new Thickness(18, 3, 7, 3), ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        var reference = new TextBox { Text = "First", Width = 240, Padding = combo.Padding, IsReadOnly = true };
        var panel = new Grid { UseLayoutRounding = true, RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } } };
        panel.Children.Add(combo); Grid.SetRow(reference, 1); panel.Children.Add(reference);
        using var host = new Host(panel, theme);
        if (key.Length > 0) combo.Style = (Style)host.Window.FindResource(key);
        host.Window.UpdateLayout(); Pump(20);
        var content = combo.Template.FindName("PART_EditableTextBox", combo) as FrameworkElement
            ?? Tree(combo).OfType<ContentPresenter>().FirstOrDefault(p => p.TemplatedParent == combo)
            ?? (FrameworkElement)Tree(combo).OfType<TextBlock>().First(t => t.TemplatedParent == combo);
        if (key == "LinkedNoteComboBoxStyle")
        {
            // The link icon, label and chevron share one padded content grid.
            var label = (TextBlock)combo.Template.FindName("LinkedNoteLabel", combo);
            Assert.Equal(combo.Padding, ((Grid)label.Parent).Margin);
            Assert.Equal(0, label.Margin.Left);
            Assert.Equal(18, ((Grid)label.Parent).ColumnDefinitions[2].Width.Value);
        }
        else
        {
            // Compare the actual text/caret origins rather than an implementation margin:
            // native TextBoxView owns a caret gutter, while plain labels supply their own.
            reference.FontFamily = combo.FontFamily; reference.FontSize = combo.FontSize;
            reference.BorderThickness = combo.BorderThickness; reference.Height = combo.ActualHeight;
            void AssertTextAlignment()
            {
                host.Window.UpdateLayout();
                double expected = reference.GetRectFromCharacterIndex(0).Left;
                double actual = content is TextBox editable
                    ? editable.TranslatePoint(editable.GetRectFromCharacterIndex(0).TopLeft, combo).X
                    : content.TranslatePoint(new Point(), combo).X;
                Assert.True(Math.Abs(actual - expected) <= 1 / VisualTreeHelper.GetDpi(combo).DpiScaleX + .01,
                    $"{theme}/{key}: dropdown text X={actual}, input caret X={expected}, padding={combo.Padding}, frame={combo.BorderThickness}.");
            }
            AssertTextAlignment();
            combo.Padding = reference.Padding = new Thickness(5, 1, 14, 1);
            AssertTextAlignment();
        }
        MotionPolicy.Configure(MotionPreference.Off, false); combo.IsDropDownOpen = true; Pump(40);
        var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.Equal(combo.FontSize, item.FontSize); Assert.Equal(combo.MinHeight, item.MinHeight);
        Assert.True(StateSurfaceMotionBehavior.GetIsEnabled((Border)item.Template.FindName("ItemBorder", item)));
        combo.IsDropDownOpen = false;
    });
    private sealed class Preference { public bool Enabled { get; set; } }
    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Switch_preserves_automation_binding_and_native_events(MotionPreference motion) => Sta(() =>
    {
        var toggle = new CheckBox { Content = "Preference", Width = 320 };
        using var host = new Host(new Grid { Children = { toggle } });
        toggle.Style = (Style)host.Window.FindResource("SettingsSwitchStyle");
        var value = new Preference();
        BindingOperations.SetBinding(toggle, ToggleButton.IsCheckedProperty, new Binding(nameof(Preference.Enabled)) { Source = value, Mode = BindingMode.TwoWay });
        var changed = 0; toggle.Checked += (_, _) => changed++; toggle.Unchecked += (_, _) => changed++;
        host.Window.UpdateLayout(); Pump(20); MotionPolicy.Configure(motion, false);
        var initial = toggle.RenderSize;
        var provider = (IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
        provider.Toggle(); Assert.True(value.Enabled); Assert.Equal(1, changed);
        Pump(220);
        var thumb = Tree(toggle).OfType<Border>().Single(b => BindingOperations.IsDataBound(b, ControlDetailMotionBehavior.IsOnProperty));
        var lit = Tree(toggle).OfType<Border>().Single(b => BindingOperations.IsDataBound(b, ControlDetailMotionBehavior.IsLitProperty));
        Assert.True(ControlDetailMotionBehavior.GetIsOn(thumb));
        Assert.True(ControlDetailMotionBehavior.GetIsLit(lit));
        Assert.Equal(18, thumb.RenderTransform.Transform(new Point()).X, 2);
        Assert.Equal(1, lit.Opacity, 2);
        provider.Toggle(); Assert.False(value.Enabled); Assert.Equal(2, changed);
        Assert.Equal(initial, toggle.RenderSize);
        Pump(210);
        Assert.Equal(0, thumb.RenderTransform.Transform(new Point()).X, 2);
        Assert.Equal(0, lit.Opacity, 2);
        foreach (var child in Tree(toggle).OfType<FrameworkElement>()) Assert.False(NativeSurfaceMotion.IsRunning(child, MotionChannel.Selection));
    });
    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Switch_starting_enabled_settles_and_can_toggle_after_reloading(MotionPreference motion) => Sta(() =>
    {
        MotionPolicy.Configure(motion, false);
        var toggle = new CheckBox { Content = "Initially enabled", IsChecked = true, Width = 320 };
        var root = new Grid { Children = { toggle } };
        using var host = new Host(root, beforeShow: window => toggle.Style = (Style)window.FindResource("SettingsSwitchStyle"));
        var thumb = (Border)toggle.Template.FindName("SwitchThumb", toggle);
        var fill = (Border)toggle.Template.FindName("SwitchOnTrack", toggle);
        Assert.Equal(18, thumb.RenderTransform.Transform(new Point()).X, 2);
        Assert.Equal(1, fill.Opacity, 2);
        var track = (Border)toggle.Template.FindName("SwitchTrack", toggle);
        var hit = (FrameworkElement)toggle.InputHitTest(track.TranslatePoint(new Point(20, 11), toggle))!;
        Assert.Same(toggle, hit.TemplatedParent);
        var provider = (IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
        provider.Toggle(); Pump(210);
        Assert.False(toggle.IsChecked); Assert.Equal(0, thumb.RenderTransform.Transform(new Point()).X, 2); Assert.Equal(0, fill.Opacity, 2);
        root.Children.Remove(toggle); host.Window.UpdateLayout(); Pump(30);
        toggle.IsChecked = true;
        root.Children.Add(toggle); host.Window.UpdateLayout(); Pump(210);
        Assert.Equal(18, thumb.RenderTransform.Transform(new Point()).X, 2); Assert.Equal(1, fill.Opacity, 2);
        provider.Toggle(); Pump(210);
        Assert.Equal(0, thumb.RenderTransform.Transform(new Point()).X, 2); Assert.Equal(0, fill.Opacity, 2);
    });
    [Fact]
    public void Slider_value_is_immediate_keyboard_snap_is_native_and_hue_stays_clear() => Sta(() =>
    {
        var slider = new Slider { Width = 320, Minimum = 0, Maximum = 100, Value = 20, SmallChange = 10, TickFrequency = 10, IsSnapToTickEnabled = true };
        using var host = new Host(new Grid { Children = { slider } });
        slider.Style = (Style)host.Window.FindResource("ModernSliderStyle"); host.Window.UpdateLayout(); Pump(25);
        var provider = (IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)!;
        provider.SetValue(55); Assert.Equal(55, slider.Value);
        slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host.Window), 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
        Assert.Equal(70, slider.Value); // Native Slider rounds the +10 step to a tick.
        host.Window.UpdateLayout();
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        var fill = (Border)slider.Template.FindName("ValueFill", slider);
        Assert.Equal(track.Thumb.ActualWidth / 2 + .7 * (track.ActualWidth - track.Thumb.ActualWidth), fill.Width, 2);
        var oldFace = (FrameworkElement)track.Thumb.Template.FindName("ThumbFace", track.Thumb);
        slider.Style = (Style)host.Window.FindResource("HueSliderStyle"); host.Window.UpdateLayout(); Pump(25);
        Assert.Null(slider.Template.FindName("ValueFill", slider));
        Assert.False(NativeSurfaceMotion.IsRunning(oldFace, MotionChannel.Interaction));
    });
    [Fact]
    public void Dense_surface_native_active_override_releases_without_layout_travel() => Sta(() =>
    {
        var row = new Border { Width = 220, Height = 36, CornerRadius = new(10), Background = Brushes.White, Child = new TextBlock { Text = "Result" } };
        StateSurfaceMotionBehavior.SetIsEnabled(row, true);
        using var host = new Host(new Grid { Children = { row } });
        var size = row.RenderSize;
        StateSurfaceMotionBehavior.SetActiveState(row, true); Pump(175);
        var fill = SurfaceFill.Get(row, SurfaceFill.Role.Hover, false)!;
        Assert.Equal(.6, fill.Opacity, 2);
        StateSurfaceMotionBehavior.SetActiveState(row, false); Pump(190);
        Assert.Equal(0, fill.Opacity); Assert.Equal(size, row.RenderSize);
        MotionPolicy.Configure(MotionPreference.Reduced, false); StateSurfaceMotionBehavior.SetActiveState(row, true); Pump(100);
        Assert.IsType<SolidColorBrush>(fill.GetValue(SurfaceFill.Layer.BrushProperty));
        StateSurfaceMotionBehavior.SetIsSelected(row, true); Assert.Equal(size, row.RenderSize);
        StateSurfaceMotionBehavior.SetIsEnabled(row, false);
        Assert.False(NativeSurfaceMotion.IsRunning(row, MotionChannel.Interaction)); Assert.Equal(0, fill.Opacity);
    });
    [Fact]
    public void Adaptive_columns_restore_after_resize_and_reveal_defaults_to_bounded_width() => Sta(() =>
    {
        var grid = new Grid { Width = 600 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new TextBlock { Text = "An ordinary label" }; var combo = new ComboBox { ItemsSource = new[] { "Value" } };
        Grid.SetColumn(combo, 1); grid.Children.Add(label); grid.Children.Add(combo); AdaptiveColumnsBehavior.SetIsEnabled(grid, true);
        var reveal = new LayoutRevealHost { Child = grid };
        using var host = new Host(new Grid { Children = { reveal } });
        Assert.Equal(Orientation.Vertical, reveal.Axis); Assert.Equal(1, Grid.GetColumn(combo));
        grid.Width = 400; host.Window.UpdateLayout(); Pump(30);
        Assert.Equal(0, Grid.GetColumn(combo)); Assert.Equal(1, Grid.GetRow(combo));
        grid.Width = 600; host.Window.UpdateLayout(); Pump(30);
        Assert.Equal(1, Grid.GetColumn(combo)); Assert.Equal(0, Grid.GetRow(combo));
    });

    [Fact]
    public void Button_selection_does_not_cancel_its_independent_interaction_clock() => Sta(() =>
    {
        var button = new Button { Width = 44, Height = 44, Content = "A" };
        using var host = new Host(new Grid { Children = { button } });
        button.Style = (Style)host.Window.FindResource("ToolbarIconButtonStyle");
        host.Window.UpdateLayout(); Pump(20);
        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
        StateSurfaceMotionBehavior.SetIsEnabled(face, true);
        var scale = UiMotion.EnsureScale(face);
        SurfaceMotion.AnimateTracks(face, MotionChannel.Interaction,
            new[] { new NativeMotionTrack(scale, ScaleTransform.ScaleXProperty, .985, true) }, 300, UiMotion.OutSine);
        StateSurfaceMotionBehavior.SetIsSelected(face, true);
        Assert.True(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
        StateSurfaceMotionBehavior.SetIsEnabled(face, false);
        Assert.True(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
        MotionPolicy.Configure(MotionPreference.Off, false);
    });

    [Fact]
    public void Chevron_reverses_from_current_angle_and_settles_before_reuse() => Sta(() =>
    {
        var detail = new Border { Width = 20, Height = 20, Background = Brushes.Blue, RenderTransform = new RotateTransform() };
        var root = new Grid { Children = { detail } };
        using var host = new Host(root);
        ControlDetailMotionBehavior.SetIsExpanded(detail, true); SeekNative(detail, MotionChannel.Navigation, 65);
        var rotation = (RotateTransform)detail.RenderTransform;
        var visible = rotation.Angle; Assert.InRange(visible, 1, 179);
        ControlDetailMotionBehavior.SetIsExpanded(detail, false);
        Assert.Equal(visible, rotation.Angle, 2); SeekNative(detail, MotionChannel.Navigation, 160);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(0, rotation.Angle);
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        ControlDetailMotionBehavior.SetIsExpanded(detail, true); SeekNative(detail, MotionChannel.Navigation, 35);
        root.Children.Remove(detail); host.Window.UpdateLayout(); Pump(25);
        Assert.Equal(180, rotation.Angle); Assert.False(NativeSurfaceMotion.IsRunning(detail, MotionChannel.Navigation));
        root.Children.Add(detail); host.Window.UpdateLayout(); Pump(25);
        MotionPolicy.Configure(MotionPreference.Reduced, false); ControlDetailMotionBehavior.SetIsExpanded(detail, false);
        Assert.Equal(0, rotation.Angle); Assert.False(NativeSurfaceMotion.IsRunning(detail, MotionChannel.Navigation));
    });
}
