using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class CommandLightTests
{
    private static Button Choice(string text, double width = double.NaN)
    {
        var button = new Button { Content = text, Width = width, Height = 40, Padding = new Thickness(10, 0, 10, 0) };
        button.Template = ChoiceTemplate(); return button;
    }
    private static ControlTemplate ChoiceTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:c="clr-namespace:Scriblucent.Controls;assembly=Scriblucent" TargetType="Button">
          <c:InteractionSurface x:Name="Face" Background="Transparent" CornerRadius="10" Padding="{TemplateBinding Padding}" Role="Compact">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </c:InteractionSurface>
        </ControlTemplate>
        """);

    [Fact]
    public void Shared_navigation_uses_the_hovered_content_footprint_and_follows_collapsed_painted_width() => Sta(() =>
    {
        using var host = new Host();
        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        CommandLightBehavior.SetIsScope(group, true);
        var shortChoice = Choice("Data"); var longChoice = Choice("A much longer translated category");
        foreach (var choice in new[] { shortChoice, longChoice })
        { CommandLightBehavior.SetRole(choice, CommandLightRole.Navigation); CommandLightBehavior.SetIsEnabled(choice, true); group.Children.Add(choice); }
        host.Root.Children.Add(group); host.Root.UpdateLayout(); Pump(40);
        var shortFace = (InteractionSurface)shortChoice.Template.FindName("Face", shortChoice);
        var longFace = (InteractionSurface)longChoice.Template.FindName("Face", longChoice);
        var point = shortChoice.TranslatePoint(new Point(shortChoice.ActualWidth / 2, 20), group);
        CommandLightBehavior.PreviewPointer(group, point, true); Pump(230);
        var small = CommandLightBehavior.LightFor(shortChoice)!; var large = CommandLightBehavior.LightFor(longChoice)!;
        Assert.Same(small, CommandLightBehavior.LightFor(shortFace)); Assert.Equal(small.RadiusX, large.RadiusX);
        var compact = small.RadiusX; Assert.InRange(compact, 18, 80);
        point = longChoice.TranslatePoint(new Point(20, 20), group);
        CommandLightBehavior.PreviewPointer(group, point, true);
        Assert.True(large.RadiusX > compact); Assert.Equal(small.RadiusX, large.RadiusX);
        var measures = host.Root.Measures;
        longFace.PaintedWidth = 48; host.Root.UpdateLayout();
        Assert.True(large.RadiusX < 60); Assert.Equal(small.RadiusX, large.RadiusX);
        Assert.Equal(measures, host.Root.Measures);
        longFace.PaintedWidth = 120; Assert.True(large.RadiusX > 80);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.Equal(60, large.X);
        longFace.PaintedWidth = 48; Assert.Equal(0, large.Strength);
        longChoice.IsEnabled = false; Pump(230); Assert.Equal(0, large.Strength);
    });

    [Fact]
    public void Button_faces_automatically_share_a_scope_preserve_explicit_exclusions_and_cleanup_on_reload() => Sta(() =>
    {
        using var host = new Host();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        CommandLightBehavior.SetIsScope(actions, true);
        var accept = Choice("Save", 80); var cancel = Choice("Cancel", 90); var excluded = Choice("Excluded", 80);
        CommandLightBehavior.SetIsEnabled(excluded, false);
        actions.Children.Add(accept); actions.Children.Add(cancel); actions.Children.Add(excluded);
        host.Root.Children.Add(actions); host.Root.UpdateLayout(); Pump(40);
        Assert.NotNull(CommandLightBehavior.LightFor(accept)); Assert.NotNull(CommandLightBehavior.LightFor(cancel));
        Assert.Null(CommandLightBehavior.LightFor(excluded));
        var point = accept.TranslatePoint(new Point(40, 20), actions);
        CommandLightBehavior.PreviewPointer(actions, point, true); Pump(230);
        var light = CommandLightBehavior.LightFor(accept)!;
        Assert.True(light.RadiusX < 80); Assert.True(light.RadiusY < 80); Assert.True(light.Strength > .5);
        host.Root.Children.Remove(actions); Pump(40);
        Assert.Null(CommandLightBehavior.LightFor(accept));
        host.Root.Children.Add(actions); Pump(40);
        var face = (InteractionSurface)accept.Template.FindName("Face", accept);
        Assert.Single(((Grid)face.Child).Children.OfType<SurfacePaint>(), paint => paint.GetType().Name == "Layer");
        Assert.NotNull(CommandLightBehavior.LightFor(accept));
        Assert.False(CommandLightBehavior.GetIsEnabled(excluded));
    });

    [Fact]
    public void A_single_button_gets_the_same_light_and_retemplating_reattaches_to_the_new_face() => Sta(() =>
    {
        using var host = new Host();
        var group = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        CommandLightBehavior.SetIsScope(group, true);
        var button = Choice("Close", 90); group.Children.Add(button); host.Root.Children.Add(group); host.Root.UpdateLayout(); Pump(40);
        var old = CommandLightBehavior.LightFor(button); Assert.NotNull(old);
        CommandLightBehavior.PreviewPointer(group, button.TranslatePoint(new Point(45, 20), group), true); Pump(230);
        Assert.InRange(old.Strength, .5, .71);
        button.Template = ChoiceTemplate(); host.Root.UpdateLayout(); Pump(80);
        var face = (InteractionSurface)button.Template.FindName("Face", button);
        Assert.NotNull(CommandLightBehavior.LightFor(button)); Assert.NotSame(old, CommandLightBehavior.LightFor(button));
        Assert.Same(CommandLightBehavior.LightFor(button), CommandLightBehavior.LightFor(face));
    });

    [Fact]
    public void Selectable_items_keep_the_light_local_quiet_and_stationary_without_changing_selection() => Sta(() =>
    {
        using var host = new Host();
        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        CommandLightBehavior.SetIsScope(group, true);
        var rows = new[] { new Border { Width = 80, Height = 40, CornerRadius = new(10), Background = Brushes.Transparent },
            new Border { Width = 180, Height = 40, CornerRadius = new(10), Background = Brushes.Transparent } };
        foreach (var row in rows)
        { StateSurfaceMotionBehavior.SetIsEnabled(row, true); group.Children.Add(row); }
        host.Root.Children.Add(group); host.Root.UpdateLayout(); Pump(40);
        var measures = host.Root.Measures;
        CommandLightBehavior.PreviewPointer(group, new Point(40, 20), true); Pump(230);
        Assert.InRange(CommandLightBehavior.LightFor(rows[0])!.Strength, .3, .45);
        Assert.Equal(0, CommandLightBehavior.LightFor(rows[1])!.Strength);
        Assert.Equal(Transform.Identity, rows[0].RenderTransform); Assert.False(StateSurfaceMotionBehavior.GetIsSelected(rows[0]));
        CommandLightBehavior.PreviewPointer(group, new Point(140, 20), true); Pump(230);
        Assert.Equal(0, CommandLightBehavior.LightFor(rows[0])!.Strength);
        Assert.InRange(CommandLightBehavior.LightFor(rows[1])!.Strength, .3, .45);
        host.Root.UpdateLayout(); Assert.Equal(measures, host.Root.Measures);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.Equal(0, CommandLightBehavior.LightFor(rows[1])!.Strength);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(0, CommandLightBehavior.LightFor(rows[1])!.Strength);
    });
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Shared_defaults_respect_style_values_and_follow_later_style_changes() => Sta(() =>
    {
        using var host = new Host();
        var button = Choice("Styled", 90);
        var quiet = new Style(typeof(Button)); quiet.Setters.Add(new Setter(CommandLightBehavior.IsEnabledProperty, false));
        button.Style = quiet; host.Root.Children.Add(button); host.Root.UpdateLayout(); Pump(40);
        Assert.Null(CommandLightBehavior.LightFor(button));
        button.Style = new Style(typeof(Button)); host.Root.UpdateLayout(); Pump(40);
        Assert.NotNull(CommandLightBehavior.LightFor(button));
        button.Style = quiet; Pump(40); Assert.Null(CommandLightBehavior.LightFor(button));
    });

    [Fact]
    public void Disabling_the_row_controller_releases_its_automatic_light_and_reenabling_restores_one_layer() => Sta(() =>
    {
        using var host = new Host();
        var row = new Border { Width = 120, Height = 40, Background = Brushes.Transparent, CornerRadius = new(10) };
        StateSurfaceMotionBehavior.SetIsEnabled(row, true); host.Root.Children.Add(row); host.Root.UpdateLayout(); Pump(40);
        Assert.Equal(CommandLightRole.Item, CommandLightBehavior.GetRole(row)); Assert.NotNull(CommandLightBehavior.LightFor(row));
        StateSurfaceMotionBehavior.SetIsEnabled(row, false); Assert.Null(CommandLightBehavior.LightFor(row));
        StateSurfaceMotionBehavior.SetIsEnabled(row, true); Pump(40);
        Assert.NotNull(CommandLightBehavior.LightFor(row));
        Assert.Single(((Grid)row.Child).Children.OfType<SurfacePaint>(), paint => paint.GetType().Name == "Layer");
    });

    [Fact]
    public void Value_controls_light_the_rim_without_recolouring_the_sample() => Sta(() =>
    {
        var owner = new InteractionSurface(); CommandLightBehavior.SetRole(owner, CommandLightRole.Value);
        var visual = new DrawingVisual(); var light = new CommandSurfaceLight(owner, owner) { X = 40, Y = 20, Strength = .7 };
        var contour = new RectangleGeometry(new Rect(0, 0, 80, 40), 10, 10);
        var inner = new RectangleGeometry(new Rect(1, 1, 78, 38), 9, 9);
        var rim = Geometry.Combine(contour, inner, GeometryCombineMode.Exclude, null);
        using (var drawing = visual.RenderOpen()) { drawing.DrawGeometry(Brushes.Red, null, contour); light.Draw(drawing, contour, rim); }
        var bitmap = new RenderTargetBitmap(80, 40, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[80 * 40 * 4]; bitmap.CopyPixels(pixels, 80 * 4, 0);
        var center = (20 * 80 + 40) * 4;
        Assert.Equal(0, pixels[center]); Assert.Equal(0, pixels[center + 1]); Assert.Equal(255, pixels[center + 2]);
        Assert.True(pixels[40 * 4] > 0); // The blue response is visible on the top rim.
    });
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { MotionPolicy.Configure(MotionPreference.Full, false); action(); }
            catch (Exception error) { failure = error; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class CountingGrid : Grid
    {
        internal int Measures;
        protected override Size MeasureOverride(Size size) { Measures++; return base.MeasureOverride(size); }
    }
    private sealed class Host : IDisposable
    {
        internal readonly CountingGrid Root = new() { Width = 440, Height = 180 };
        internal readonly Border Chrome = new() { Margin = new(20, 60, 20, 0), Height = 80, VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new(14), Padding = new(12), Background = Brushes.Transparent };
        internal readonly TabSelectionSurface Plate = new() { Bounds = new(30, 20, 130, 40), CornerRadius = new(10, 10, 0, 0),
            ConnectionDepth = 14, Background = Brushes.Transparent, Rim = Brushes.Transparent, ViewportBounds = new(0, 0, 440, 180) };
        internal readonly Window Window;
        internal Host()
        {
            CommandLightBehavior.SetIsScope(Root, true);
            CommandLightBehavior.SetLightColor(Root, Color.FromArgb(80, 67, 92, 236));
            Root.Children.Add(Chrome); Root.Children.Add(Plate);
            CommandLightBehavior.SetIsEnabled(Chrome, true); CommandLightBehavior.SetIsEnabled(Plate, true);
            Window = new() { Content = Root, SizeToContent = SizeToContent.WidthAndHeight, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false };
            Window.Show(); Window.UpdateLayout(); Pump(40);
        }
        public void Dispose() { Window.Close(); Pump(40); }
    }

    [Fact]
    public void One_pointer_coordinate_crosses_the_attached_tab_and_toolbar_without_layout_work() => Sta(() =>
    {
        using var host = new Host(); var measures = host.Root.Measures;
        CommandLightBehavior.PreviewPointer(host.Root, new Point(90, 70), true); Pump(230);
        var plate = CommandLightBehavior.LightFor(host.Plate)!; var chrome = CommandLightBehavior.LightFor(host.Chrome)!;
        Assert.True(plate.Strength > .5); Assert.Equal(plate.Strength, chrome.Strength, 6);
        for (var i = 0; i < 100; i++)
        {
            var point = new Point(90 + i, 70);
            CommandLightBehavior.PreviewPointer(host.Root, point, true);
            Assert.Equal(point, host.Plate.TranslatePoint(new Point(plate.X, plate.Y), host.Root));
            Assert.Equal(point, host.Chrome.TranslatePoint(new Point(chrome.X, chrome.Y), host.Root));
        }
        host.Root.UpdateLayout(); Assert.Equal(measures, host.Root.Measures);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        CommandLightBehavior.PreviewPointer(host.Root, new Point(190, 70), false); Pump(260);
        Assert.Equal(0, plate.Strength); Assert.Equal(0, chrome.Strength);
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2d)]
    public void The_light_obeys_the_same_rounded_contour_and_live_padding_at_each_dpi(double scale) => Sta(() =>
    {
        using var host = new Host(); CommandLightBehavior.PreviewPointer(host.Root, new Point(28, 68), true); Pump(230);
        var bitmap = new RenderTargetBitmap((int)(440 * scale), (int)(180 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host.Root); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        byte Alpha(int x, int y) => pixels[((int)(y * scale) * bitmap.PixelWidth + (int)(x * scale)) * 4 + 3];
        Assert.Equal(0, Alpha(20, 60)); Assert.Equal(0, Alpha(19, 90)); Assert.Equal(0, Alpha(421, 90));
        Assert.True(Alpha(38, 78) > 8);
        var model = CommandLightBehavior.LightFor(host.Chrome); host.Chrome.Padding = new Thickness(22, 6, 18, 8);
        host.Root.UpdateLayout(); Assert.Same(model, CommandLightBehavior.LightFor(host.Chrome));
    });

    [Fact]
    public void Reduced_clears_cursor_glow_skips_pointer_work_and_full_restores_it() => Sta(() =>
    {
        using var host = new Host(); CommandLightBehavior.PreviewPointer(host.Root, new Point(100, 95), true); Pump(40);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        var chrome = CommandLightBehavior.LightFor(host.Chrome)!; var plate = CommandLightBehavior.LightFor(host.Plate)!;
        Assert.Equal(0, chrome.Strength); Assert.Equal(0, plate.Strength);
        Assert.Equal(host.Chrome.ActualWidth / 2, chrome.X);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        for (var i = 0; i < 50; i++) CommandLightBehavior.PreviewPointer(host.Root, new Point(100 + i, 95), true);
        Assert.Equal(host.Chrome.ActualWidth / 2, chrome.X);
        Assert.Equal(0, chrome.Strength);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        CommandLightBehavior.SettleSelection(host.Root, new Point(90, 50), new Point(280, 50));
        Assert.Equal(0, plate.Strength);
        MotionPolicy.Configure(MotionPreference.Full, false);
        CommandLightBehavior.PreviewPointer(host.Root, new Point(130, 95), true); Pump(240);
        Assert.True(chrome.Strength > .5);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(0, chrome.Strength);
        CommandLightBehavior.PreviewPointer(host.Root, new Point(110, 35), true); Assert.Equal(0, plate.Strength);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
    });

    [Fact]
    public void Tab_press_preserves_the_drag_transform_and_policy_change_resets_the_releasing_header() => Sta(() =>
    {
        using var host = new Host();
        var tab = new TabItem { Header = "Document", RenderTransform = new TranslateTransform(11, 0) };
        tab.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
              <Border x:Name="HeaderBorder" Background="Transparent"><ContentPresenter ContentSource="Header"/></Border>
            </ControlTemplate>
            """);
        var tabs = new TabControl { Width = 150, Height = 50, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        tabs.Items.Add(tab); host.Root.Children.Add(tabs); host.Root.UpdateLayout();
        var drag = tab.RenderTransform; var header = (Border)tab.Template.FindName("HeaderBorder", tab);
        CommandLightBehavior.PreviewPress(host.Root, tab, true); Pump(130);
        Assert.Same(drag, tab.RenderTransform); Assert.True(UiMotion.EnsureScale(header).ScaleY < 1); Assert.True(host.Plate.PressProgress > .9);
        CommandLightBehavior.PreviewPress(host.Root, tab, false); Pump(35);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(1, UiMotion.EnsureScale(header).ScaleX); Assert.Equal(1, UiMotion.EnsureScale(header).ScaleY);
        Assert.Equal(0, host.Plate.PressProgress); Assert.Same(drag, tab.RenderTransform);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandPress));
    });

    [Fact]
    public void Selection_settle_is_interruptible_and_unload_reload_reuses_only_one_paint_layer() => Sta(() =>
    {
        using var host = new Host(); var measures = host.Root.Measures;
        CommandLightBehavior.SettleSelection(host.Root, new Point(90, 52), new Point(270, 52));
        // Assert before yielding: a busy dispatcher may resume after the complete 260 ms settle.
        Assert.True(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        CommandLightBehavior.PreviewPointer(host.Root, new Point(130, 90), true); Pump(250);
        Assert.Equal(new Point(130, 90), host.Chrome.TranslatePoint(new Point(CommandLightBehavior.LightFor(host.Chrome)!.X,
            CommandLightBehavior.LightFor(host.Chrome)!.Y), host.Root));
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        host.Root.UpdateLayout(); Assert.Equal(measures, host.Root.Measures);
        host.Root.Children.Remove(host.Chrome); Pump(40); Assert.Null(CommandLightBehavior.LightFor(host.Chrome));
        Assert.Empty(((Grid)host.Chrome.Child).Children.OfType<SurfacePaint>());
        host.Root.Children.Add(host.Chrome); Pump(40);
        Assert.Single(((Grid)host.Chrome.Child).Children.OfType<SurfacePaint>());
        Assert.True(CommandLightBehavior.LightFor(host.Chrome)!.Strength > .5);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
        CommandLightBehavior.SettleSelection(host.Root, new Point(90, 52), new Point(270, 52));
        host.Window.Close(); Pump(40); Assert.Null(CommandLightBehavior.LightFor(host.Chrome)); Assert.Null(host.Plate.CommandLight);
        Assert.False(NativeSurfaceMotion.IsRunning(host.Root, MotionChannel.CommandLight));
    });
}
