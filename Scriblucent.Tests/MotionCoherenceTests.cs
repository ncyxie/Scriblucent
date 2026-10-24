using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.ViewModels;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionCoherenceTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { MotionPolicy.Configure(MotionPreference.Full, false); action(); }
            catch (Exception e) { failure = e; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Host : IDisposable
    {
        internal readonly Window Window;
        internal Host(FrameworkElement content, string theme = "Light")
        {
            Window = new Window { Width = 760, Height = 580, Content = content, Opacity = 0, IsHitTestVisible = false, ShowActivated = false, ShowInTaskbar = false };
            foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons", "Controls/Dashboard", "Controls/Calendar", "Controls/Quiz" })
                Window.Resources.MergedDictionaries.Add(ControlTestResources.Load(name));
            Window.Show(); Window.UpdateLayout(); Pump(40);
        }
        public void Dispose() { Window.Close(); Pump(35); }
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static byte[] Raster(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var bounds = VisualTreeHelper.GetParent(element) is Visual parent
            ? element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize)) : new Rect(element.RenderSize);
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, new Rect(element.RenderSize));
        bitmap.Render(visual);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static string Source(string relative)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "Scriblucent"))) folder = folder.Parent;
        return Path.Combine(folder!.FullName, "Scriblucent", relative);
    }
    private static FrameworkElement Parse(XElement fragment, XElement sourceRoot)
    {
        foreach (var node in fragment.DescendantsAndSelf())
        {
            foreach (var attr in node.Attributes().ToArray())
            {
                if (attr.Name.LocalName == "Class" || (!attr.Value.StartsWith("{") &&
                    new[] { "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "MouseDown", "MouseLeftButtonUp", "Loaded", "Unloaded", "ValueChanged", "KeyDown", "PreviewKeyDown", "SizeChanged", "LostFocus", "PreviewTextInput", "PreviewMouseWheel" }.Contains(attr.Name.LocalName))) attr.Remove();
            }
        }
        foreach (var attr in sourceRoot.Attributes().Where(a => a.IsNamespaceDeclaration)) fragment.SetAttributeValue(attr.Name, attr.Value);
        var text = fragment.ToString();
        text = text.Replace("{StaticResource Loc}", "{x:Null}");
        // Inline dimensional tokens for isolated XamlReader parsing; BAML resolves these from App resources.
        var tokens = XDocument.Load(Source("Themes/Tokens.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var entry in tokens.Root!.Elements().Where(e => new[] { "CornerRadius", "Thickness", "Double", "FontFamily" }.Contains(e.Name.LocalName)))
            if (entry.Attribute(xaml + "Key") is { } key) text = text.Replace("{StaticResource " + key.Value + "}", entry.Value.Trim());
        text = System.Text.RegularExpressions.Regex.Replace(text, "clr-namespace:(Scriblucent[^\";]*)", "clr-namespace:$1;assembly=Scriblucent");
        return (FrameworkElement)XamlReader.Parse(text);
    }

    private static void SeekNative(UIElement element, MotionChannel channel, int ms)
    {
        if (!NativeSurfaceMotion.IsRunning(element, channel)) return;
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [element, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var state = ((System.Collections.IDictionary)args[1]!)[channel]!;
        var tracks = (IReadOnlyList<(NativeMotionTrack, AnimationClock)>)state.GetType().GetField("Tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        foreach (var (_, clock) in tracks) { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime); }
    }
    [Fact]
    public void Releasing_and_reversing_before_the_first_native_tick_preserves_the_visible_pose() => Sta(() =>
    {
        var face = new Border { Background = Brushes.Blue, Width = 120, Height = 60, Opacity = .8 };
        using var host = new Host(new Grid { Children = { face } });
        UiMotion.Fade(face, 0, 140); Assert.Equal(.8, face.Opacity, 5);
        UiMotion.Fade(face, 1, 160); Assert.Equal(.8, face.Opacity, 5);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(1, face.Opacity); Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.Presence));
    });
    [Theory]
    [InlineData(64, 232)]
    [InlineData(232, 64)]
    public void Sidebar_push_and_viewport_resize_stay_together_with_twenty_thousand_virtual_cards(double from, double to) => Sta(() =>
    {
        var root = new Grid(); var column = new ColumnDefinition { Width = new(from) };
        root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new());
        var sidebar = new SidebarChrome { Background = Brushes.SlateBlue, Child = new Grid() };
        SidebarResizeMotion.SetIsEnabled(sidebar, true);
        var cards = new DashboardVirtualizedCardGrid { ItemCellSize = new(244, 164), VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = Enumerable.Range(0, 20_000).Select(i => $"Card {i}").ToArray(),
            ItemTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Width='220' Height='140' Margin='12' CornerRadius='14' Background='White'/></DataTemplate>") };
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var virtualHost = new DashboardVirtualizationHost { ScrollViewerElement = viewer,
            HostMode = DashboardVirtualizationHostMode.PageVirtualized, MotionScope = Scriblucent.Services.DashboardMotionScope.Flashcards, Child = cards };
        viewer.Content = virtualHost;
        var content = new Grid { Name = "SidebarContentStage", Children = { viewer } }; Grid.SetColumn(content, 1);
        root.Children.Add(content); root.Children.Add(sidebar); using var host = new Host(root);
        var initialWidth = content.ActualWidth;
        UiMotion.AnimateSidebarChrome(sidebar, column, from, to);
        root.UpdateLayout(); Assert.Equal(initialWidth, content.ActualWidth, 4);
        var seek = SidebarTestClock.Pause(sidebar);
        foreach (var ms in new[] { 0, 45, 90, 130, 190, 259 })
        {
            seek(ms); root.UpdateLayout();
            var edge = ((RectangleGeometry)sidebar.Clip).Rect.Width;
            Assert.Equal(edge, column.Width.Value, 4);
            Assert.InRange(Math.Abs(content.TranslatePoint(new(), root).X - edge), 0, 1);
            Assert.InRange(Math.Abs(content.ActualWidth + edge - root.ActualWidth), 0, 1);
            Assert.InRange(Tree(cards).OfType<ContentPresenter>().Count(), 1, 100);
        }
        MotionPolicy.Configure(MotionPreference.Off, false); root.UpdateLayout();
        Assert.Equal(to, column.Width.Value); Assert.False(RenderTransitionClock.IsAnyRunning);
    });
    private static byte[] ScaledRaster(FrameworkElement element, double scale)
    {
        element.UpdateLayout(); var size = element.RenderSize;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var bounds = VisualTreeHelper.GetParent(element) is Visual parent ? element.TransformToAncestor(parent).TransformBounds(new Rect(size)) : new Rect(size);
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, new Rect(size));
        bitmap.Render(visual); var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    [Theory]
    [InlineData("Light", 1)] [InlineData("Dark", 1)]
    [InlineData("Light", 1.25)] [InlineData("Dark", 1.25)]
    [InlineData("Light", 1.5)] [InlineData("Dark", 1.5)]
    [InlineData("Light", 2)] [InlineData("Dark", 2)]
    public void Filled_pointer_light_keeps_the_face_contour_without_a_separate_corner_stroke(string theme, double scale) => Sta(() =>
    {
        var button = new Button { Width = 180, Height = 40, Content = "Add flashcard" };
        using var host = new Host(new Grid { Children = { button } }, theme);
        button.Style = (Style)button.FindResource("DialogPrimaryButtonStyle"); button.ApplyTemplate(); Pump(20);
        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
        face.AnimateState(true, false, false);
        CommandLightBehavior.PreviewPointer(button, new Point(90, 4), true); Pump(230);
        var light = Assert.IsType<CommandSurfaceLight>(CommandLightBehavior.LightFor(face));
        Assert.Same(light, CommandLightBehavior.LightFor(button));
        var lit = ScaledRaster(face, scale);
        CommandLightBehavior.PreviewPointer(button, new Point(90, 4), false); Pump(260);
        // This checks the painted contour, not display scheduling. A hidden test
        // window can deliver the native clock's first tick late under load.
        for (int retry = 0; light.Strength > 0 && retry < 40; retry++) Pump(25);
        Assert.Equal(0, light.Strength);
        var unlit = ScaledRaster(face, scale);
        var width = (int)Math.Ceiling(face.ActualWidth * scale);
        var compared = 0;
        // A local radial light can vary across a row. Its curved edge must retain
        // the face's coverage and must not gain a separate brighter corner stroke.
        for (var i = 3; i < lit.Length; i += 4)
            Assert.InRange(Math.Abs(lit[i] - unlit[i]), 0, 1);
        Assert.Equal(0, lit[3]);
        Assert.Equal(0, lit[(width - 1) * 4 + 3]);
        var corner = (int)Math.Ceiling(12 * scale);
        for (var y = 0; y < (int)Math.Floor(9 * scale); y++)
        {
            var center = (y * width + width / 2) * 4;
            if (unlit[center + 3] == 0) continue;
            foreach (var x in Enumerable.Range(0, corner).Concat(Enumerable.Range(width - corner, corner)))
            {
                var pixel = (y * width + x) * 4;
                var alpha = unlit[pixel + 3];
                if (alpha < 128) continue;
                for (var channel = 0; channel < 3; channel++)
                {
                    var centerGain = (lit[center + channel] - unlit[center + channel]) * 255d / unlit[center + 3];
                    var cornerGain = (lit[pixel + channel] - unlit[pixel + channel]) * 255d / alpha;
                    Assert.InRange(cornerGain, -3, Math.Max(0, centerGain) + 4);
                }
                compared++;
            }
        }
        Assert.True(compared > 20, "Check visible pixels in both upper corners.");
        var upperFace = ((int)(4 * scale) * width + width / 2) * 4;
        Assert.True(Enumerable.Range(0, 3).Sum(c => lit[upperFace + c] - unlit[upperFace + c]) > 35,
            "The pointer light must remain visibly responsive.");
    });
    [Theory]
    [InlineData("Light", 1)] [InlineData("Dark", 1)]
    [InlineData("Light", 1.25)] [InlineData("Dark", 1.25)]
    [InlineData("Light", 1.5)] [InlineData("Dark", 1.5)]
    [InlineData("Light", 2)] [InlineData("Dark", 2)]
    public void Hover_tint_keeps_the_same_corner_coverage_at_fractional_render_scales(string theme, double scale) => Sta(() =>
    {
        var face = new InteractionSurface { Width = 160, Height = 44, Padding = new(13, 7, 11, 9),
            CornerRadius = new(10, 0, 0, 10), BorderThickness = new(1), Background = Brushes.White, Child = new Grid(),
            HoverBackground = Brushes.CornflowerBlue, HighlightWash = Brushes.LightBlue, HighlightEdge = Brushes.Blue, Role = InteractionRole.Compact };
        using var host = new Host(new Grid { Children = { face } }, theme);
        face.AnimateState(false, false, false); Pump(20); var rest = ScaledRaster(face, scale);
        face.AnimateState(true, false, false); Pump(20); var hover = ScaledRaster(face, scale);
        Assert.Equal(rest.Length, hover.Length);
        var changed = 0;
        for (int i = 0; i < rest.Length; i += 4)
        {
            Assert.InRange(Math.Abs(rest[i + 3] - hover[i + 3]), 0, 1);
            if (rest[i] != hover[i] || rest[i + 1] != hover[i + 1] || rest[i + 2] != hover[i + 2]) changed++;
        }
        Assert.True(changed > 1000); // Equal silhouettes must still deliver a visible tint.
        face.AnimateState(false, false, false);
        Assert.Equal(rest, ScaledRaster(face, scale));
    });
    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Native_fields_are_flat_and_focus_follows_the_real_face_with_two_dip_directional_light(string theme) => Sta(() =>
    {
        var field = new TextBox { Width = 220, Height = 56, Text = "A focused field" };
        using var host = new Host(new Grid { Children = { field } }, theme); field.ApplyTemplate();
        Assert.Empty(Tree(field).OfType<BakedElevationLayer>());
        var face = (Border)field.Template.FindName("TextBoxBorder", field);
        var ring = (AccentRim)field.Template.FindName("FocusRing", field);
        Assert.Same(face, ring.Surface); Assert.Equal(2, ring.Thickness);
        var before = face.RenderSize; MotionPolicy.Configure(MotionPreference.Off, false); ring.IsActive = true; Pump(20);
        Assert.Equal(before, face.RenderSize); Assert.Equal(1, ring.Opacity);
        var pixels = Raster(ring); var upper = (12 * 220 + 1) * 4; var lower = (43 * 220 + 1) * 4;
        Assert.True(pixels[upper] + pixels[upper + 1] + pixels[upper + 2] > pixels[lower] + pixels[lower + 1] + pixels[lower + 2] + 15);
        ring.IsActive = false; Assert.Equal(0, ring.Opacity);
    });
    [Theory]
    [InlineData("Light", null)] [InlineData("Dark", null)]
    [InlineData("Light", "EditableComboBoxStyle")] [InlineData("Dark", "EditableComboBoxStyle")]
    [InlineData("Light", "ChromeComboBoxStyle")] [InlineData("Dark", "ChromeComboBoxStyle")]
    [InlineData("Light", "SetSelectorComboBoxStyle")] [InlineData("Dark", "SetSelectorComboBoxStyle")]
    [InlineData("Light", "LinkedNoteComboBoxStyle")] [InlineData("Dark", "LinkedNoteComboBoxStyle")]
    public void Every_combo_template_uses_the_same_flat_face_and_focus_contour(string theme, string? styleKey) => Sta(() =>
    {
        var field = new ComboBox { Width = 220, Height = 56, ItemsSource = new[] { "A focused choice", "Another idea" }, SelectedIndex = 0 };
        using var host = new Host(new Grid { Children = { field } }, theme);
        if (styleKey is not null) field.Style = (Style)host.Window.FindResource(styleKey);
        host.Window.UpdateLayout(); Pump(20);
        Assert.Empty(Tree(field).OfType<BakedElevationLayer>());
        var ring = (AccentRim)field.Template.FindName("FocusRing", field);
        var face = ring.Surface as Border ?? SurfacePaint.FindBorder(ring.Surface!);
        Assert.NotNull(face); Assert.Null(face.Effect); Assert.Equal(2, ring.Thickness);
        var before = field.RenderSize; MotionPolicy.Configure(MotionPreference.Off, false); ring.IsActive = true; Pump(20);
        Assert.Equal(before, field.RenderSize); Assert.Equal(1, ring.Opacity);
        Assert.Equal(face.RenderSize, ring.RenderSize); Assert.Equal(new Point(), ring.TranslatePoint(new(), face));
        Assert.Contains(Raster(ring), value => value > 0);
        ring.IsActive = false; Assert.Equal(0, ring.Opacity);
    });
    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Selection_and_drop_feedback_share_the_card_footprint_and_release_reverses_cleanly(string theme) => Sta(() =>
    {
        var card = new Border { Width = 220, Height = 140, CornerRadius = new(14), BorderThickness = new(1), Padding = new(14), Child = new Grid() };
        RoundedClipBehavior.SetIsEnabled(card, true);
        using var host = new Host(new Grid { Children = { card } }, theme);
        SelectionRimBehavior.SetIsSelected(card, true); Pump(180); var selected = Tree(card).OfType<SurfacePaint>().Single();
        Assert.Equal(new Point(), selected.TranslatePoint(new(), card));
        var drop = new DropTargetSurface { Surface = card, Opacity = 0, IsHitTestVisible = false }; ((Grid)card.Child).Children.Add(drop);
        host.Window.UpdateLayout(); Pump(20);
        DashboardDropGlowVisual.AnimateToActive(drop); SeekNative(drop, MotionChannel.Presence, 160); host.Window.UpdateLayout();
        Assert.Equal(1, drop.Opacity, 5);
        Assert.Equal(card.RenderSize, drop.RenderSize); Assert.Equal(new Point(), drop.TranslatePoint(new(), card));
        Assert.Equal(2.5, drop.BorderThickness.Left); Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.Equal(card.CornerRadius, drop.CornerRadius);
        DashboardDropGlowVisual.Reset(drop); Assert.False(DashboardDropGlowVisual.IsAtActiveState(drop));
        Assert.True(drop.Opacity > 0, $"Release pose {drop.Opacity}, loaded {drop.IsLoaded}, visible {drop.IsVisible}, quality {MotionPolicy.Quality}, running {NativeSurfaceMotion.IsRunning(drop, MotionChannel.Presence)}"); SeekNative(drop, MotionChannel.Presence, 60); Assert.InRange(drop.Opacity, .01, .99);
        DashboardDropGlowVisual.AnimateToActive(drop); SeekNative(drop, MotionChannel.Presence, 160);
        Assert.True(DashboardDropGlowVisual.IsAtActiveState(drop)); Assert.Equal(2.5, drop.BorderThickness.Left);
        DashboardDropGlowVisual.Reset(drop); SeekNative(drop, MotionChannel.Presence, 140);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(0, drop.Opacity); Assert.Equal(new Thickness(), drop.BorderThickness);
    });
    [Fact]
    public void Scroll_content_keeps_its_rounded_viewport_during_scroll_resize_and_workspace_reattachment() => Sta(() =>
    {
        var viewer = new ScrollViewer { Width = 220, Height = 140, Padding = new Thickness(6), Content = new Border { Width = 480, Height = 1600, Background = Brushes.White } };
        RoundedClipBehavior.SetCornerRadius(viewer, new CornerRadius(14));
        RoundedClipBehavior.SetIsEnabled(viewer, true);
        var root = new Grid { Children = { viewer } };
        using var host = new Host(root);
        var presenter = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
        Assert.NotNull(presenter.Clip);
        Assert.Equal(new CornerRadius(8), RoundedClipBehavior.GetCornerRadius(presenter));
        Assert.False(presenter.Clip.FillContains(new Point(.5, .5)));
        var clip = presenter.Clip;
        viewer.ScrollToVerticalOffset(200); host.Window.UpdateLayout(); Pump(20);
        Assert.True(viewer.VerticalOffset > 0);
        Assert.Same(clip, presenter.Clip);
        viewer.Width = 320; host.Window.UpdateLayout(); Pump(20);
        Assert.Equal(presenter.ActualWidth, presenter.Clip.Bounds.Width, 1);
        viewer.Padding = new Thickness(4); host.Window.UpdateLayout(); Pump(20);
        Assert.Equal(new CornerRadius(10), RoundedClipBehavior.GetCornerRadius(presenter));
        root.Children.Remove(viewer); host.Window.UpdateLayout(); Pump(20);
        Assert.Null(presenter.Clip);
        root.Children.Add(viewer); host.Window.UpdateLayout(); Pump(20);
        Assert.NotNull(presenter.Clip);
        Assert.False(presenter.Clip.FillContains(new Point(.5, .5)));
        RoundedClipBehavior.SetIsEnabled(viewer, false);
        Assert.Null(viewer.Clip); Assert.Null(presenter.Clip);
    });
    [Fact]
    public void Raised_cards_reserve_no_shadow_layout_and_keep_their_shadow_outside_the_face_clip() => Sta(() =>
    {
        var face = new Border { Width = 220, Height = 140, CornerRadius = new(14), Background = Brushes.White };
        RoundedClipBehavior.SetIsEnabled(face, true);
        var surface = new ElevationHost { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { face } };
        using var host = new Host(surface); var shadow = Tree(surface).OfType<CardShadowLayer>().Single();
        Assert.Equal(face.RenderSize, surface.RenderSize); Assert.Null(surface.Clip); Assert.NotNull(face.Clip);
        Assert.True(shadow.ActualWidth > face.ActualWidth); Assert.Equal(new Thickness(-17), shadow.Margin);
        var move = new TranslateTransform(0, -2); face.RenderTransform = move; Assert.Same(move, shadow.RenderTransform);
    });
    [Fact]
    public void Default_lists_accept_pixel_wheel_input_without_losing_virtualization() => Sta(() =>
    {
        var list = new ListBox { Width = 260, Height = 160, ItemsSource = Enumerable.Range(0, 200).Select(i => "Item " + i).ToArray() };
        using var host = new Host(new Grid { Children = { list } });
        var viewer = Tree(list).OfType<ScrollViewer>().Single();
        SmoothScrollBehavior.SetIsEnabled(viewer, true);
        MotionPolicy.Configure(MotionPreference.Off, false);
        var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
        viewer.RaiseEvent(wheel); host.Window.UpdateLayout(); Pump(30);
        Assert.True(wheel.Handled);
        Assert.True(viewer.ViewportHeight > 100);
        Assert.InRange(viewer.VerticalOffset, 10, 80);
        Assert.True(Tree(list).OfType<ListBoxItem>().Count() < list.Items.Count);
        viewer.ScrollToEnd(); host.Window.UpdateLayout(); Pump(30);
        Assert.True(list.ItemContainerGenerator.ContainerFromIndex(199) is ListBoxItem { IsVisible: true });
    });
    [Theory]
    [InlineData("Light", "QuizLibraryRow", 2)]
    [InlineData("Dark", "QuizLibraryRow", 2)]
    [InlineData("Light", "CombineRow", 2)]
    [InlineData("Dark", "CombineRow", 2)]
    public void Dense_collection_rows_stay_flat_and_selection_survives_bitmap_cache_changes(string theme, string key, double thickness) => Sta(() =>
    {
        var source = XDocument.Load(Source("Controls/CombineWorkspaceView.xaml")); XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var tile = new XElement(source.Descendants().First(e => e.Name.LocalName == "DashboardTileHost"));
        tile.SetAttributeValue("RendererKey", key);
        var resources = new XElement(wpf + "ResourceDictionary",
            new XElement(wpf + "ResourceDictionary.MergedDictionaries", new[] { "Tokens", theme + "Theme", "Generic", "Controls/Chrome", "Controls/Buttons" }
                .Select(name => new XElement(wpf + "ResourceDictionary", new XAttribute("Source", $"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml")))));
        var fragment = new XElement(wpf + "Grid", new XAttribute("Width", "400"), new XAttribute("VerticalAlignment", "Center"),
            new XElement(wpf + "Grid.Resources", resources), new XElement(tile));
        var row = Parse(fragment, source.Root!);
        var quiz = new Scriblucent.Views.QuizLibraryItemViewModel(new QuizViewModel(new QuizDocument { Title = "A living library" }));
        var combine = new Scriblucent.Views.FlashcardCombineWindow.FlashcardCombineItem(new FlashcardSetViewModel(new FlashcardSetDocument { Title = "Ideas to keep" }));
        var model = key == "QuizLibraryRow" ? (object)quiz : combine; row.DataContext = model;
        using var host = new Host(row, theme);
        var face = Tree(row).OfType<Border>().Single(StateSurfaceMotionBehavior.GetIsEnabled);
        Assert.Empty(Tree(row).OfType<CardShadowLayer>()); Assert.Equal(new Thickness(), face.BorderThickness);
        Assert.Equal(thickness, StateSurfaceMotionBehavior.GetSelectionThickness(face));
        var renderer = SkiaTileRenderers.ForKey(key)!; var signature = renderer.BuildSignature(model); var before = face.RenderSize;
        MotionPolicy.Configure(MotionPreference.Off, false);
        if (key == "QuizLibraryRow") quiz.IsSelected = true; else combine.IsSelected = true;
        Pump(30); host.Window.UpdateLayout(); Assert.True(StateSurfaceMotionBehavior.GetIsSelected(face));
        Assert.Equal(before, face.RenderSize); Assert.NotEqual(signature, renderer.BuildSignature(model));
        var outline = Tree(face).OfType<SurfacePaint>().Single(p => p.GetType().Name == "Outline");
        Assert.Equal(1, outline.Opacity); Assert.Equal(face.RenderSize, outline.RenderSize); Assert.Equal(new Point(), outline.TranslatePoint(new(), face));
        using var bitmap = renderer.Render(model, 1, 400, out _);
        var upper = bitmap.GetPixel(0, 16); var lower = bitmap.GetPixel(0, bitmap.Height - 24);
        Assert.True(upper.Red + upper.Green + upper.Blue > lower.Red + lower.Green + lower.Blue);
    });
    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Quiz_question_focus_survives_scroll_tiles_without_tinting_the_resting_face(string theme) => Sta(() =>
    {
        using var host = new Host(new Grid(), theme);
        var question = new Scriblucent.Views.QuizPreviewWindow.QuizPreviewQuestion(1, new QuizQuestion { Question = "A focused question", Options = [new QuizOption { Text = "An idea" }] });
        var renderer = SkiaTileRenderers.ForKey("QuizQuestion")!; var signature = renderer.BuildSignature(question);
        using var rest = renderer.Render(question, 1, 400, out _);
        question.IsFocused = true; Assert.NotEqual(signature, renderer.BuildSignature(question));
        using var focused = renderer.Render(question, 1, 400, out _);
        Assert.Equal(rest.GetPixel(200, 8), focused.GetPixel(200, 8));
        var blue = focused.GetPixel(0, 24); Assert.True(blue.Blue > blue.Red + 30);
        question.IsFocused = false; Assert.Equal(signature, renderer.BuildSignature(question));
    });
    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Collection_selection_follows_the_rendered_face_including_its_scale(string theme) => Sta(() =>
    {
        var selection = new EditorSelectionController(); var id = Guid.NewGuid(); selection.SetItems([id]); selection.Begin(id);
        var face = new Border { Name = "CardFace", Width = 300, Height = 164, Margin = new Thickness(10), CornerRadius = new(14),
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(.88, .88), Background = Brushes.White };
        var frame = new CollectionSelectionFrame { Content = face, FaceName = face.Name, ItemId = id, Selection = selection,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        using var host = new Host(new Grid { Children = { frame } }, theme);
        var paint = Tree(frame).OfType<CollectionSelectionFeedback>().Single();
        var bounds = face.TransformToAncestor(frame).TransformBounds(new Rect(face.RenderSize));
        Assert.Equal(264, paint.ActualWidth, 4); Assert.Equal(164 * .88, paint.ActualHeight, 4);
        Assert.Equal(bounds.TopLeft, paint.TranslatePoint(new(), frame));
        Assert.Equal(14 * .88, paint.CornerRadius.TopLeft, 4);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Dashboard_selection_never_changes_cached_note_paint_or_its_signature(string theme) => Sta(() =>
    {
        using var host = new Host(new Grid(), theme);
        var note = new NoteCardViewModel(new NoteDocument { Title = "Selected" }, _ => { });
        var colors = new CardRenderColors(SkiaSharp.SKColors.White, SkiaSharp.SKColors.Transparent, SkiaSharp.SKColors.Black,
            SkiaSharp.SKColors.Black, SkiaSharp.SKColors.White, SkiaSharp.SKColors.Black);
        var signature = SkiaTileRenderers.Notes.BuildSignature(note);
        using var rest = SkiaCardRenderer.Render(note, 1.5, colors, out _);
        note.IsSelectedInMassSelect = true;
        Assert.Equal(signature, SkiaTileRenderers.Notes.BuildSignature(note));
        using var selected = SkiaCardRenderer.Render(note, 1.5, colors, out _);
        Assert.Equal(rest.Bytes, selected.Bytes);
    });
}
