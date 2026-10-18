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

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionRepairTests
{
    [Fact]
    public void Synchronizing_a_card_already_at_rest_does_not_start_a_hover_out_animation() => Sta(() =>
    {
        var card = new Border { Width = 220, Height = 140 };
        CardSurfaceMotionBehavior.SetProfile(card, CardSurfaceMotionProfile.NoteCard);
        CardSurfaceMotionBehavior.SetIsEnabled(card, true);
        using var host = new Host(card);
        CardSurfaceMotionBehavior.ForceRestHoverState(card);
        for (int i = 0; i < 20; i++) CardSurfaceMotionBehavior.PrepareRecycledNoteCard(card);
        Assert.False(NativeSurfaceMotion.IsRunning(card, MotionChannel.Interaction));
        Assert.True(UiMotion.ChannelTranslate(card, MotionChannel.Interaction).Value.IsIdentity);
    });

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
            try { MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception e) { failure = e; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Labels { public string this[string key] => key; }
    private sealed class Host : IDisposable
    {
        internal readonly Window Window;
        internal Host(FrameworkElement content, string theme = "Light")
        {
            Window = new Window { Width = 760, Height = 580, Content = content, Opacity = 0, IsHitTestVisible = false, ShowActivated = false, ShowInTaskbar = false };
            foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons", "Controls/Dashboard", "Controls/Calendar", "Controls/Quiz" })
                Window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml") });
            Window.Resources["Loc"] = new Labels();
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
                    new[] { "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "MouseDown", "Loaded", "Unloaded", "ValueChanged", "KeyDown", "PreviewKeyDown", "SizeChanged", "LostFocus", "PreviewTextInput", "PreviewMouseWheel" }.Contains(attr.Name.LocalName))) attr.Remove();
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

    [Fact]
    public void Production_settings_has_a_pixel_viewport_and_wheel_reaches_lower_sections() => Sta(() =>
    {
        var doc = XDocument.Load(Source("Views/SettingsPanel.xaml"));
        // Plain UserControl, production layout/resources; no settings constructor or personal data.
        var clone = new XElement(doc.Root!);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        // This read-only notice loads App-scoped BAML resources in its constructor.
        // Its fixed placeholder preserves the scroll layout without creating Scriblucent.App.
        foreach (var notice in clone.Descendants().Where(e => e.Name.LocalName == "AiReviewNotice").ToArray())
            notice.ReplaceWith(new XElement(wpf + "Border", new XAttribute("Height", "64"), new XAttribute("Margin", "0,12,0,12")));
        var resources = clone.Element(wpf + "UserControl.Resources")!;
        var tokens = XDocument.Load(Source("Themes/Tokens.xaml"));
        XElement InlineDictionary(string name)
        {
            var original = XDocument.Load(Source("Themes/" + name + ".xaml")).Root!;
            return new XElement(wpf + "ResourceDictionary", original.Attributes().Where(a => a.IsNamespaceDeclaration),
                tokens.Root!.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.MergedDictionaries").Select(e => new XElement(e)),
                original.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.MergedDictionaries" && (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is not "Loc").Select(e => new XElement(e)));
        }
        var dictionary = new XElement(wpf + "ResourceDictionary",
            new XElement(wpf + "ResourceDictionary.MergedDictionaries", new[] { "LightTheme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" }.Select(InlineDictionary)),
            tokens.Root!.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.MergedDictionaries").Select(e => new XElement(e)),
            resources.Elements().Select(e => new XElement(e)));
        resources.ReplaceNodes(dictionary);
        var panel = Parse(clone, doc.Root!);
        using var host = new Host(panel);
        var viewer = Tree(panel).OfType<ScrollViewer>().First(v => v.Content is StackPanel);
        Assert.False(viewer.CanContentScroll); Assert.InRange(viewer.ViewportHeight, 100, 540);
        Assert.True(viewer.ScrollableHeight > 500);
        SmoothScrollBehavior.SetIsEnabled(viewer, true);
        viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        // Invisible test windows do not reliably receive compositor frames, especially after
        // other STA fixtures. Advance the real scroll frame callback at a deterministic cadence.
        var render = typeof(SmoothScrollBehavior).GetMethod("OnRendering", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int tick = 0; tick < 100; tick++)
        {
            var args = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [TimeSpan.FromMilliseconds(100_000 + tick * 16)], null)!;
            render.Invoke(null, [null, args]); viewer.UpdateLayout();
        }
        Assert.True(viewer.VerticalOffset > 20);
        viewer.ScrollToEnd(); Pump(160);
        Assert.InRange(viewer.VerticalOffset, viewer.ScrollableHeight - 1, viewer.ScrollableHeight);
    });

    [Fact]
    public void Selection_outline_moves_with_compositor_drift_and_offset_commits() => Sta(() =>
    {
        var card = new Border { Width = 220, Height = 100, Padding = new(16), BorderThickness = new(2), CornerRadius = new(14), Child = new TextBlock { Text = "Selected" } };
        var content = new StackPanel { Children = { card, new Border { Height = 1800 } } };
        var viewer = new ScrollViewer { Content = content, CanContentScroll = false };
        using var host = new Host(viewer);
        AccentMotion.Set(card, true, Brushes.Blue, animate: false); host.Window.UpdateLayout();
        var outline = Tree(card).OfType<SurfacePaint>().Single();
        Assert.Equal(new Point(), outline.TranslatePoint(new(), card));
        SmoothScrollCompositor.Begin(viewer, []);
        foreach (var offset in new[] { 8d, 23, 55, 110, 170 })
        {
            Assert.True(SmoothScrollCompositor.TryApply(viewer, 0, offset, offset == 110, out _, out _));
            viewer.UpdateLayout();
            Assert.Equal(card.TranslatePoint(new(), viewer), outline.TranslatePoint(new(), viewer));
        }
        SmoothScrollCompositor.End(viewer, 0, 170); Pump(70);
        Assert.Equal(card.TranslatePoint(new(), viewer), outline.TranslatePoint(new(), viewer));
        AccentMotion.Clear(card); Assert.Empty(Tree(card).OfType<SurfacePaint>());
    });

    [Fact]
    public void Paint_matches_asymmetric_corners_and_live_padding_border_changes() => Sta(() =>
    {
        var card = new Border { Width = 200, Height = 70, Padding = new(11), BorderThickness = new(2), CornerRadius = new(10, 0, 0, 10), Child = new TextBlock { Text = "Split action" } };
        using var host = new Host(new Grid { Children = { card } });
        var paint = SurfaceFill.Get(card, SurfaceFill.Role.Hover, true)!; paint.SetValue(SurfaceFill.Layer.BrushProperty, Brushes.Blue); paint.Opacity = 1;
        host.Window.UpdateLayout(); Assert.Equal(card.RenderSize, paint.RenderSize); Assert.Equal(new Point(), paint.TranslatePoint(new(), card));
        var bytes = Raster(paint); var stride = 200 * 4;
        Assert.False(SurfaceContour.Create(card.RenderSize, card.CornerRadius, card.BorderThickness).FillContains(new Point(1, 1)));
        var radiusProperty = (DependencyProperty)typeof(SurfacePaint).GetField("RadiusProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(card.CornerRadius, paint.GetValue(radiusProperty));
        Assert.True(bytes[stride + 4 + 3] == 0, $"Paint corner alpha {bytes[stride + 4 + 3]}, radius {paint.GetValue(radiusProperty)}, size {paint.RenderSize}, margin {paint.Margin}"); Assert.Equal(255, bytes[stride + 198 * 4 + 3]);
        card.CornerRadius = new(0, 14, 14, 0); card.Padding = new(17, 9, 22, 13); card.BorderThickness = new(1, 2, 3, 4);
        host.Window.UpdateLayout(); Assert.Equal(card.RenderSize, paint.RenderSize); Assert.Equal(new Point(), paint.TranslatePoint(new(), card));
        bytes = Raster(paint); Assert.Equal(255, bytes[stride + 4 + 3]); Assert.Equal(0, bytes[stride + 198 * 4 + 3]);
    });

    [Fact]
    public void Rounded_clip_releases_metric_watchers_on_unload_and_restores_after_reuse() => Sta(() =>
    {
        var border = new Border { Width = 120, Height = 70, CornerRadius = new(14), BorderThickness = new(1) };
        RoundedClipBehavior.SetIsEnabled(border, true);
        var root = new Grid { Children = { border } }; using var host = new Host(root);
        Assert.NotNull(border.Clip); root.Children.Remove(border); Pump(30); Assert.Null(border.Clip);
        border.CornerRadius = new(0, 14, 14, 0); root.Children.Add(border); Pump(30);
        Assert.True(border.Clip!.FillContains(new Point(1, 1)));
        Assert.False(border.Clip.FillContains(new Point(119, 1)));
        border.BorderThickness = new(2); Assert.True(border.Clip.FillContains(new Point(1, 1)));
        RoundedClipBehavior.SetIsEnabled(border, false); Assert.Null(border.Clip);
    });

    [Fact]
    public void Compact_sidebar_hover_has_a_complete_rounded_right_edge()
        => Sta(() =>
        {
            var face = new InteractionSurface { Width = 216, Height = 44, CornerRadius = new(14), PaintedWidth = 48, Child = new Grid() };
            using var host = new Host(new Grid { Children = { face } });
            var layer = SurfaceFill.Get(face, SurfaceFill.Role.Hover, true)!; layer.SetValue(SurfaceFill.Layer.BrushProperty, Brushes.Blue); layer.Opacity = 1;
            var compact = Raster(face);
            Assert.Equal(0, compact[(216 + 46) * 4 + 3]); Assert.Equal(0, compact[(22 * 216 + 60) * 4 + 3]);
            Assert.Equal(255, compact[(22 * 216 + 24) * 4 + 3]);
            face.PaintedWidth = 216; var expanded = Raster(face);
            Assert.Equal(255, expanded[(22 * 216 + 60) * 4 + 3]);
        });

    [Fact]
    public void Closing_sidebar_retains_visible_pixels_and_keeps_its_column_at_the_visible_edge() => Sta(() =>
    {
        var root = new Grid(); var column = new ColumnDefinition { Width = new(232) }; root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new());
        var button = new Button { Content = "Dashboard", Height = 44, Style = null };
        var sidebar = new SidebarChrome { Background = Brushes.SlateBlue, Padding = new(8), Child = new StackPanel { Children = { button } } };
        SidebarResizeMotion.SetIsEnabled(sidebar, true);
        var content = new Grid { Name = "SidebarContentStage", Background = Brushes.Azure }; Grid.SetColumn(content, 1); root.Children.Add(content); root.Children.Add(sidebar);
        using var host = new Host(root);
        UiMotion.AnimateSidebarChrome(sidebar, column, 232, 64); root.UpdateLayout();
        SidebarTestClock.Pause(sidebar)(130);
        var width = ((RectangleGeometry)sidebar.Clip).Rect.Width; Assert.InRange(width, 100, 200); Assert.Equal(width, column.Width.Value, 5); root.UpdateLayout();
        var bytes = Raster(root); var index = ((int)root.ActualWidth * 100 + 100) * 4;
        Assert.True(bytes[index] - bytes[index + 1] > 50); // Real sidebar pixels, rather than only a running clip clock.
        MotionPolicy.Configure(MotionPreference.Reduced, false); Assert.Equal(64, ((RectangleGeometry)sidebar.Clip).Rect.Width);
    });

    [Theory]
    [InlineData("Light", "DialogPrimaryButtonStyle")]
    [InlineData("Dark", "DialogPrimaryButtonStyle")]
    [InlineData("Light", "DialogDangerButtonStyle")]
    [InlineData("Dark", "DialogDangerButtonStyle")]
    public void Filled_button_light_has_visible_upper_surface_contrast(string theme, string style) => Sta(() =>
    {
        var button = new Button { Width = 160, Height = 44 }; using var host = new Host(new Grid { Children = { button } }, theme);
        button.Style = (Style)button.FindResource(style); button.ApplyTemplate(); Pump(20);
        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
        // Hold the hover material constant so this measures the cursor light itself.
        face.AnimateState(true, false, false); Pump(20); var rest = Raster(face);
        CommandLightBehavior.PreviewPointer(button, new Point(80, 4), true); Pump(230);
        var lit = Raster(face); var index = (4 * 160 + 80) * 4;
        var delta = Enumerable.Range(0, 3).Sum(c => (int)lit[index + c] - rest[index + c]);
        var light = Assert.IsType<CommandSurfaceLight>(CommandLightBehavior.LightFor(face));
        Assert.Same(light, CommandLightBehavior.LightFor(button));
        Assert.True(delta > 35, $"Upper face brightness gain was {delta}; rest {string.Join(',', rest.Skip(index).Take(4))}; lit {string.Join(',', lit.Skip(index).Take(4))}; light strength {light.Strength}");
    });

    [Fact]
    public void Empty_color_swatch_receives_rim_feedback_without_recoloring_its_face() => Sta(() =>
    {
        var button = new Button { Background = Brushes.Orange };
        using var host = new Host(new Grid { Children = { button } });
        button.Style = (Style)button.FindResource("EditorColorSwatchButtonStyle"); button.ApplyTemplate(); Pump(20);
        var face = (InteractionSurface)button.Template.FindName("SwatchBorder", button);
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        face.AnimateState(true, false, false); host.Window.UpdateLayout();
        Assert.Single(Tree(face).OfType<SurfacePaint>());
        Assert.Same(Brushes.Orange, face.Background); Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(face.HighlightWash).Color);
    });

    [Fact]
    public void Scroll_rest_clears_a_shared_chrome_faces_partially_faded_hover_fill() => Sta(() =>
    {
        var factory = new FrameworkElementFactory(typeof(InteractionSurface), "Face");
        factory.SetValue(Border.BackgroundProperty, Brushes.White); factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        factory.SetValue(InteractionSurface.HoverBackgroundProperty, Brushes.LightBlue);
        var button = new Button { Width = 170, Height = 54, Template = new ControlTemplate(typeof(Button)) { VisualTree = factory } };
        ChromeHoverMotionBehavior.SetMotionHostName(button, "Face"); ChromeHoverMotionBehavior.SetIsEnabled(button, true);
        using var host = new Host(new Grid { Children = { button } });
        var face = (InteractionSurface)button.Template.FindName("Face", button);
        face.AnimateState(true, false, false);
        var fill = SurfaceFill.Get(face, SurfaceFill.Role.Hover, false)!;
        Assert.Equal(1, fill.Opacity);
        face.AnimateState(false, false, true); Pump(20);
        Assert.True(fill.Opacity > 0);
        ChromeHoverMotionBehavior.ForceRestHoverState(button);
        Assert.Equal(0, fill.Opacity);
        Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
        Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.InteractionPaint));
    });

    [Fact]
    public void Cursor_tint_reaches_card_edges_when_content_desired_width_exceeds_its_slot() => Sta(() =>
    {
        var face = new InteractionSurface { Width = 200, Height = 64, Padding = new(11), BorderThickness = new(1),
            CornerRadius = new(12), Background = Brushes.White, Child = new Border { MinWidth = 180, Height = 32 } };
        CommandLightBehavior.SetIsEnabled(face, true); CommandLightBehavior.SetRole(face, CommandLightRole.Item);
        CommandLightBehavior.SetLightColor(face, Colors.Blue);
        using var host = new Host(new Grid { Children = { face } });
        // Reproduce an overfull content slot without depending on the test monitor's DPI.
        var paint = Tree(face).OfType<SurfacePaint>().Single();
        ((Grid)paint.Parent).MinWidth = 180;
        var light = Assert.IsType<CommandSurfaceLight>(CommandLightBehavior.LightFor(face));
        SurfaceMotion.Stop(face, MotionChannel.CommandLight);
        light.Strength = 1; light.X = 100; light.Y = 32; light.RadiusX = light.RadiusY = 2000;
        var pixels = Raster(face);
        foreach (var point in new[] { new Point(3, 32), new Point(196, 32), new Point(100, 3), new Point(100, 60) })
        {
            var p = ((int)point.Y * 200 + (int)point.X) * 4;
            Assert.True(pixels[p] > 200 && pixels[p + 1] < 60 && pixels[p + 2] < 60,
                $"Cursor tint clipped inside the card at {point}: {string.Join(',', pixels.Skip(p).Take(4))}");
        }
        Assert.Equal(0, pixels[3]); // Outer rounded silhouette still clips the corner.
        ((Grid)paint.Parent).ClipToBounds = true;
        var explicitlyClipped = Raster(face); var left = (32 * 200 + 3) * 4;
        Assert.True(explicitlyClipped[left + 1] > 200 && explicitlyClipped[left + 2] > 200);
    });

    [Fact]
    public void Shadow_does_not_size_the_face_and_shares_its_entry_and_hover_transform() => Sta(() =>
    {
        var face = new Border { Width = 190, Height = 100, CornerRadius = new(14), Background = Brushes.White };
        var shadow = new CardShadowLayer(); var group = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { shadow, face } };
        using var host = new Host(group); Assert.Equal(face.RenderSize, group.RenderSize);
        var move = new TranslateTransform(0, -3); var scale = new ScaleTransform(.88, .88);
        face.RenderTransformOrigin = new(.5, .5); face.RenderTransform = new TransformGroup { Children = { scale, move } };
        Assert.Same(face.RenderTransform, shadow.RenderTransform); Assert.Equal(face.RenderTransformOrigin, shadow.RenderTransformOrigin);
        Assert.Equal(new Thickness(-17), shadow.Margin);
    });

    [Theory]
    [InlineData(220, 140)] [InlineData(350, 200)] [InlineData(430, 112)] [InlineData(334, 96)]
    public void Arriving_card_shadow_has_no_interior_backing_and_fades_with_the_face(double width, double height) => Sta(() =>
    {
        var face = new Border { Width = width, Height = height, CornerRadius = new(14), Background = Brushes.White, Visibility = Visibility.Hidden };
        var shadow = new CardShadowLayer();
        var pair = new Grid { Margin = new Thickness(17), Children = { shadow, face } };
        var scene = new Grid { Width = width + 34, Height = height + 34, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Children = { pair } };
        using var host = new Host(scene);
        Assert.Equal(shadow.RenderSize, ((FrameworkElement)shadow.Content).RenderSize);
        var pixels = Raster(scene); var stride = (int)Math.Ceiling(scene.ActualWidth) * 4;
        byte Alpha(byte[] image, int x, int y) => image[y * stride + x * 4 + 3];
        Assert.Equal(0, Alpha(pixels, 22, (int)(17 + height / 2)));
        Assert.Equal(0, Alpha(pixels, (int)(17 + width / 2), 22));
        Assert.Equal(0, Alpha(pixels, 25, 25));
        var exterior = Alpha(pixels, 13, (int)(17 + height / 2)); Assert.True(exterior > 0);
        face.Opacity = .5; Pump(20);
        foreach (var tile in Tree(shadow).OfType<Image>()) Assert.Equal(.29, tile.Opacity, 5);
        var faded = Raster(scene); Assert.InRange(Alpha(faded, 13, (int)(17 + height / 2)), exterior / 2 - 2, exterior / 2 + 2);
        Assert.Equal(1, shadow.Opacity); Assert.Null(shadow.CacheMode); Assert.Null(shadow.Effect);
        face.Opacity = 0; Pump(20); var hidden = Raster(scene);
        Assert.All(hidden.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(0, alpha));
        face.Opacity = 1; Pump(20);
        foreach (var tile in Tree(shadow).OfType<Image>()) Assert.Equal(.58, tile.Opacity, 5);
    });

    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void Note_scroll_paint_uses_the_face_size_without_counting_grid_margins_twice(double scale) => Sta(() =>
    {
        int width = (int)Math.Ceiling(244 * scale), height = (int)Math.Ceiling(164 * scale);
        var tile = BitmapSource.Create(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, new byte[width * height * 4], width * 4);
        tile.Freeze();
        var face = NoteCardControl.CropScrollTile(tile);
        Assert.Equal(220 * scale, face.PixelWidth);
        Assert.Equal(140 * scale, face.PixelHeight);
        Assert.Equal(220, face.Width, 4); Assert.Equal(140, face.Height, 4);
        Assert.True(face.IsFrozen); Assert.Same(face, NoteCardControl.CropScrollTile(tile));
    });

    [Theory]
    [InlineData("Quiz")]
    [InlineData("MindMap")]
    [InlineData("Flashcard")]
    public void Dashboard_selection_keeps_listing_tiles_and_signatures_reusable(string kind) => Sta(() =>
    {
        object model = kind switch { "Quiz" => new QuizViewModel(new QuizDocument()), "MindMap" => new MindMapViewModel(new MindMapDocument()), _ => new FlashcardSetViewModel(new FlashcardSetDocument()) };
        var renderer = kind switch { "Quiz" => SkiaTileRenderers.QuizUngrouped, "MindMap" => SkiaTileRenderers.MindMapUngrouped, _ => SkiaTileRenderers.FlashcardSetUngrouped };
        var signature = renderer.BuildSignature(model); using var rest = renderer.Render(model, 1.5, 0, out _);
        model.GetType().GetProperty("IsSelectedInMassSelect")!.SetValue(model, true);
        Assert.Equal(signature, renderer.BuildSignature(model)); using var selected = renderer.Render(model, 1.5, 0, out _);
        Assert.Equal(rest.Bytes, selected.Bytes);
    });
}
