using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Rendering;
using SkiaSharp;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class SurfaceMaterialTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static ResourceDictionary Theme(string theme)
    {
        // This fixture also runs without another test having initialized WPF's pack URI parser.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        return new() { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{theme}Theme.xaml") };
    }
    private static byte[] Raster(Border face)
    {
        var bitmap = new RenderTargetBitmap(160, 40, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var parent = (Visual)VisualTreeHelper.GetParent(face);
        var bounds = face.TransformToAncestor(parent).TransformBounds(new Rect(face.RenderSize));
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(face) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, new Rect(face.RenderSize));
        bitmap.Render(visual);
        var bytes = new byte[160 * 40 * 4]; bitmap.CopyPixels(bytes, 160 * 4, 0); return bytes;
    }

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Semantic_action_material_is_readable_and_frozen_in_every_state(string theme) => Sta(() =>
    {
        var resources = Theme(theme);
        foreach (var tone in new[] { "Primary", "Success", "Warning", "Danger" })
        foreach (var state in new[] { "", "Hover", "Pressed" })
        {
            var source = ((SolidColorBrush)resources["Accent" + tone + state]).Color;
            var text = ((SolidColorBrush)resources["Accent" + tone + "Foreground"]).Color;
            var paint = SurfaceMaterialPalette.Get(source, SurfaceMaterialKind.Filled, theme == "Dark", text);
            Assert.All(new[] { paint.Colors.Top, paint.Colors.Body, paint.Colors.Bottom }, c => Assert.True(SurfaceMaterialPalette.Contrast(c, text) >= 4.5));
            Assert.NotEqual(paint.Colors.Top, paint.Colors.Bottom);
            Assert.True(paint.Fill.IsFrozen); Assert.True(paint.Edge.IsFrozen);
            Assert.Same(paint, SurfaceMaterialPalette.Get(source, SurfaceMaterialKind.Filled, theme == "Dark", text));
            Assert.Equal(source, ((SolidColorBrush)resources["Accent" + tone + state]).Color);
        }
        Assert.Equal(Color.FromRgb(67, 92, 236), ((SolidColorBrush)resources["AccentPrimary"]).Color);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Metric_surfaces_keep_labels_and_semantic_values_readable_without_a_dark_bottom(string theme) => Sta(() =>
    {
        var resources = Theme(theme);
        var neutral = ((SolidColorBrush)resources["CardBackground"]).Color;
        foreach (var key in new[] { "NoteCardBackground", "FlashcardCardBackground", "RichTextBoxBackground" })
            Assert.Equal(neutral, ((SolidColorBrush)resources[key]).Color);
        var label = ((SolidColorBrush)resources["TextColorSecondary"]).Color;
        foreach (var tone in new[] { "Neutral", "Primary", "Success", "Warning", "Danger" })
        {
            var value = ((SolidColorBrush)resources[tone == "Neutral" ? "TextColor" : "Accent" + tone + "SubtleForeground"]).Color;
            var paint = SurfaceMaterialPalette.Get(neutral, SurfaceMaterialKind.Metric, theme == "Dark", tone == "Neutral" ? null : value);
            foreach (var color in new[] { paint.Colors.Top, paint.Colors.Body, paint.Colors.Bottom })
            {
                Assert.True(SurfaceMaterialPalette.Contrast(color, value) >= 4.5, tone + " value");
                Assert.True(SurfaceMaterialPalette.Contrast(color, label) >= 4.5, tone + " label");
            }
            Assert.Equal(paint.Colors.Body, paint.Colors.Bottom);
            Assert.True(paint.Fill.IsFrozen); Assert.True(paint.Edge.IsFrozen);
        }
        Assert.Equal(neutral, ((SolidColorBrush)resources["CardBackground"]).Color);
    });

    [Theory]
    [InlineData("Light", 1d)] [InlineData("Light", 1.25)] [InlineData("Light", 1.5)] [InlineData("Light", 2d)]
    [InlineData("Dark", 1d)] [InlineData("Dark", 1.25)] [InlineData("Dark", 1.5)] [InlineData("Dark", 2d)]
    public void Workspace_lighting_distributes_colour_steps_without_full_width_bands(string theme, double dpi) => Sta(() =>
    {
        var resources = Theme(theme);
        var color = ((SolidColorBrush)resources["WindowBackground"]).Color;
        var palette = SurfaceMaterialPalette.Get(color, SurfaceMaterialKind.Workspace, theme == "Dark");
        var height = (int)(760 * dpi);
        var tile = WorkspaceGradientTexture.Get(palette.Colors, height);
        Assert.Same(tile, WorkspaceGradientTexture.Get(palette.Colors, height));
        Assert.True(tile.Image.IsFrozen);
        Assert.Equal(64 * height * 4, tile.Pixels.Length);
        double Mean(int y, int channel) => Enumerable.Range(0, 64).Average(x => tile.Pixels[(y * 64 + x) * 4 + channel]);
        for (var channel = 0; channel < 3; channel++)
        for (var y = 1; y < height; y++)
            Assert.InRange(Math.Abs(Mean(y, channel) - Mean(y - 1, channel)), 0, .04);
        Assert.Contains(Enumerable.Range(0, height / 3), y => Enumerable.Range(0, 64)
            .Select(x => tile.Pixels[(y * 64 + x) * 4]).Distinct().Count() > 1);
        var state = new WorkspaceGradientTexture.Fill();
        var first = state.Get(palette.Colors, 760, new DpiScale(dpi, dpi));
        Assert.True(first.IsFrozen);
        Assert.Same(first, state.Get(palette.Colors, 760, new DpiScale(dpi, dpi)));
        Assert.NotSame(first, state.Get(palette.Colors, 761, new DpiScale(dpi, dpi)));
    });

    [Theory]
    [InlineData(false, SurfaceMaterialKind.Filled)] [InlineData(true, SurfaceMaterialKind.Filled)]
    [InlineData(false, SurfaceMaterialKind.Tinted)] [InlineData(true, SurfaceMaterialKind.Tinted)]
    [InlineData(false, SurfaceMaterialKind.Marker)] [InlineData(true, SurfaceMaterialKind.Marker)]
    [InlineData(false, SurfaceMaterialKind.Data)] [InlineData(true, SurfaceMaterialKind.Data)]
    [InlineData(false, SurfaceMaterialKind.Neutral)] [InlineData(true, SurfaceMaterialKind.Neutral)]
    [InlineData(false, SurfaceMaterialKind.Raised)] [InlineData(true, SurfaceMaterialKind.Raised)]
    [InlineData(false, SurfaceMaterialKind.Metric)] [InlineData(true, SurfaceMaterialKind.Metric)]
    [InlineData(false, SurfaceMaterialKind.Workspace)] [InlineData(true, SurfaceMaterialKind.Workspace)]
    public void Wpf_and_skia_match_inside_the_surface_and_leave_corners_transparent(bool dark, SurfaceMaterialKind kind) => Sta(() =>
    {
        var face = new SurfaceMaterialBorder { Width = 160, Height = 40, CornerRadius = new(10), Background = new SolidColorBrush(Color.FromRgb(67, 92, 236)) };
        SurfaceMaterial.SetKind(face, kind); SurfaceMaterial.SetForeground(face, Brushes.White);
        var window = new Window { Width = 230, Height = 120, Content = new Grid { Children = { face } }, ShowInTaskbar = false, ShowActivated = false, Opacity = 1 };
        window.Resources["SurfaceMaterialIsDark"] = dark;
        try
        {
            window.Show(); window.UpdateLayout(); Pump(); window.UpdateLayout();
            var pixels = Raster(face);
            using var raster = new SKBitmap(160, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(raster); canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(67, 92, 236), IsAntialias = true };
            SurfaceMaterialRaster.Draw(canvas, SKRect.Create(0, 0, 160, 40), 10, 10, paint, dark, 1, kind, SKColors.White);
            foreach (var y in new[] { 2, 8, 16, 23, 32, 37 })
            {
                var offset = (y * 160 + 80) * 4; var sk = raster.GetPixel(80, y);
                Assert.InRange(Math.Abs(pixels[offset] - sk.Blue), 0, 3);
                Assert.InRange(Math.Abs(pixels[offset + 1] - sk.Green), 0, 3);
                Assert.InRange(Math.Abs(pixels[offset + 2] - sk.Red), 0, 3);
                Assert.Equal(255, pixels[offset + 3]);
            }
            Assert.Equal(0, pixels[3]); Assert.Equal(0, raster.GetPixel(0, 0).Alpha);
        }
        finally { window.Close(); Pump(); }
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Material_preserves_native_border_and_updates_when_background_and_theme_change() => Sta(() =>
    {
        var face = new SurfaceMaterialBorder { Width = 160, Height = 40, CornerRadius = new(10), BorderThickness = new(2), BorderBrush = Brushes.Magenta, Background = Brushes.CornflowerBlue };
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Tinted);
        var window = new Window { Width = 230, Height = 120, Content = new Grid { Children = { face } }, ShowInTaskbar = false, ShowActivated = false, Opacity = 1 };
        window.Resources["SurfaceMaterialIsDark"] = false;
        byte[] Snapshot()
        {
            window.UpdateLayout(); Pump(); window.UpdateLayout();
            return Raster(face);
        }
        try
        {
            window.Show(); var first = Snapshot(); var size = face.RenderSize;
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, first.Skip((160 + 80) * 4).Take(4));
            face.Background = Brushes.SeaGreen; window.Resources["SurfaceMaterialIsDark"] = true;
            var second = Snapshot();
            Assert.NotEqual(first[(20 * 160 + 80) * 4 + 2], second[(20 * 160 + 80) * 4 + 2]);
            Assert.Equal(size, face.RenderSize); Assert.Same(Brushes.SeaGreen, face.Background);
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, second.Skip((160 + 80) * 4).Take(4));
            SurfaceMaterial.SetKind(face, SurfaceMaterialKind.None);
            var flat = Snapshot(); Assert.Equal(Brushes.SeaGreen.Color.R, flat[(20 * 160 + 80) * 4 + 2]);
        }
        finally { window.Close(); Pump(); }
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Neutral_controls_are_readable_and_raised_content_retains_its_base_color(string theme) => Sta(() =>
    {
        var resources = Theme(theme);
        var text = ((SolidColorBrush)resources["TextColor"]).Color;
        foreach (var key in new[] { "ButtonBackground", "IconButtonBackground", "ButtonHoverBackground", "ButtonPressedBackground", "CardBackground", "InputSurface" })
        {
            var color = ((SolidColorBrush)resources[key]).Color;
            var paint = SurfaceMaterialPalette.Get(color, SurfaceMaterialKind.Neutral, theme == "Dark", text);
            Assert.All(new[] { paint.Colors.Top, paint.Colors.Body, paint.Colors.Bottom }, c => Assert.True(SurfaceMaterialPalette.Contrast(c, text) >= 4.5));
            var raised = SurfaceMaterialPalette.Get(color, SurfaceMaterialKind.Raised, theme == "Dark", text);
            Assert.Equal(color, raised.Colors.Top); Assert.Equal(color, raised.Colors.Body); Assert.Equal(color, raised.Colors.Bottom);
            Assert.IsType<SolidColorBrush>(raised.Fill); Assert.True(raised.Fill.IsFrozen);
        }
    });

    [Fact]
    public void Direct_material_paints_translucency_once_without_wrapping_or_replacing_content() => Sta(() =>
    {
        var child = new TextBlock { Text = "" };
        var face = new SurfaceMaterialBorder { Width = 160, Height = 40, CornerRadius = new(10), Child = child, Background = new SolidColorBrush(Color.FromArgb(64, 67, 92, 236)) };
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Neutral);
        var window = new Window { Width = 230, Height = 120, Content = new Grid { Children = { face } }, ShowInTaskbar = false, ShowActivated = false };
        window.Resources["SurfaceMaterialIsDark"] = false;
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            var bytes = Raster(face); Assert.Equal(64, bytes[(20 * 160 + 80) * 4 + 3]);
            Assert.Same(child, face.Child);
            var original = face.Background;
            SurfaceMaterial.SetKind(face, SurfaceMaterialKind.None); Pump();
            Assert.Same(original, face.Background); Assert.Same(child, face.Child);
            Assert.Null(SurfaceMaterial.Paint(Brushes.Transparent, SurfaceMaterialKind.Neutral, false));
        }
        finally { window.Close(); Pump(); }
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Direct_border_keeps_scoped_implicit_styles_and_explicit_background_bindings() => Sta(() =>
    {
        var inherited = new Style(typeof(Border)); inherited.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(7)));
        inherited.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.CadetBlue));
        var source = new Border { Background = Brushes.CornflowerBlue };
        var face = new SurfaceMaterialBorder { Width = 160, Height = 40 };
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Neutral);
        face.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Border.Background)) { Source = source });
        var window = new Window { Width = 230, Height = 120, Content = face, ShowInTaskbar = false, ShowActivated = false };
        window.Resources[typeof(Border)] = inherited; window.Resources["SurfaceMaterialIsDark"] = false;
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            Assert.Same(inherited, face.Style); Assert.Equal(new Thickness(7), face.Padding);
            Assert.Same(Brushes.CornflowerBlue, face.Background);
            source.Background = Brushes.SeaGreen; Pump(); Assert.Same(Brushes.SeaGreen, face.Background);
            Assert.True(System.Windows.Data.BindingOperations.IsDataBound(face, Border.BackgroundProperty));
        }
        finally { window.Close(); Pump(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Raised_rest_hairline_matches_skia_without_a_second_parallel_edge(bool dark) => Sta(() =>
    {
        var color = Color.FromRgb(44, 44, 44); var frameColor = Color.FromRgb(58, 58, 58);
        var face = new SurfaceMaterialBorder { Width = 160, Height = 40, CornerRadius = new(10), Background = new SolidColorBrush(color), BorderBrush = new SolidColorBrush(frameColor), BorderThickness = new(1) };
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Raised);
        var window = new Window { Width = 230, Height = 120, Content = new Grid { Children = { face } }, ShowActivated = false, ShowInTaskbar = false };
        window.Resources["SurfaceMaterialIsDark"] = dark;
        try
        {
            window.Show(); window.UpdateLayout(); Pump(); var pixels = Raster(face);
            using var bitmap = new SKBitmap(160, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
            using var fill = new SKPaint { Color = new SKColor(44, 44, 44), IsAntialias = true };
            SurfaceMaterialRaster.Draw(canvas, SKRect.Create(0, 0, 160, 40), 10, 10, fill, dark, 1, SurfaceMaterialKind.Raised, frameInset: 1);
            using var stroke = new SKPaint { Color = new SKColor(58, 58, 58), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(.5f, .5f, 159.5f, 39.5f), 10, 10, stroke);
            SurfaceMaterialRaster.LightRaisedFrame(canvas, SKRect.Create(0, 0, 160, 40), 10, stroke.Color, dark, 1);
            foreach (var y in new[] { 0, 1, 2, 20, 38, 39 })
            {
                var actual = bitmap.GetPixel(80, y); var offset = (y * 160 + 80) * 4;
                Assert.InRange(Math.Abs(pixels[offset] - actual.Blue), 0, 3);
                Assert.InRange(Math.Abs(pixels[offset + 3] - actual.Alpha), 0, 3);
            }
            Assert.Equal(44, pixels[(160 + 80) * 4]);
        }
        finally { window.Close(); Pump(); }
    });
}
