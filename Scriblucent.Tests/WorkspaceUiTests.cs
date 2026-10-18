using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Controls;
using Scriblucent.Rendering;
using Scriblucent.Views;
using Scriblucent.Animations;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class WorkspaceUiTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Every_feature_shell_uses_one_canvas_and_preserves_motion_hosts() => Sta(() =>
    {
        var shell = new FeatureWindowShell();
        using var host = new ResourceHost(shell);
        var stage = (Grid)shell.Template.FindName("MotionStage", shell);
        Assert.Equal(new Thickness(10, 6, 10, 10), stage.Margin);
        Assert.Same(host.Window.FindResource("WorkspaceBackdrop"), shell.Background);
        var face = (SurfaceMaterialBorder)shell.Template.FindName("StageFace", shell);
        Assert.Equal(new CornerRadius(18), face.CornerRadius);
        Assert.Equal(SurfaceMaterialKind.Workspace, SurfaceMaterial.GetKind(face));
        Assert.Same(host.Window.FindResource("WindowBackground"), face.Background);
        Assert.Null(shell.Template.FindName("WorkspaceWash", shell));
        Assert.Null(shell.Template.FindName("StageShadow", shell));
        foreach (var name in new[] { "MotionChrome", "MotionBody", "MotionFooter" })
            Assert.NotNull(shell.Template.FindName(name, shell));
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2d)]
    public void Connected_tab_has_curved_shoulders_and_leaves_the_exposed_toolbar_corner_round(double scale) => Sta(() =>
    {
        var parent = new CountingGrid();
        parent.Children.Add(new Border { Margin = new Thickness(0, 44, 0, 0), Height = 44, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.SlateBlue, CornerRadius = new(14) });
        var face = new TabSelectionSurface { Bounds = new Rect(20, 4, 160, 40), Background = Brushes.SlateBlue,
            CornerRadius = new(10, 10, 0, 0), ConnectionDepth = 14, Rim = Brushes.Transparent, ViewportBounds = new Rect(0, 0, 240, 100) };
        parent.Children.Add(face); parent.Measure(new Size(240, 100)); parent.Arrange(new Rect(0, 0, 240, 100)); parent.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(240 * scale), (int)Math.Round(100 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(parent); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        byte Alpha(int x, int y) => pixels[((int)Math.Round(y * scale) * bitmap.PixelWidth + (int)Math.Round(x * scale)) * 4 + 3];
        Assert.Equal(0, Alpha(238, 44)); // The corner marked in tabs.png stays round.
        Assert.Equal(0, Alpha(20, 4));
        Assert.True(Alpha(18, 42) > 150); // Shoulder widens toward the toolbar, without a right-angle step.
        Assert.True(Alpha(22, 46) > 240); // Opaque join across fractional-DPI boundaries.
        var measures = parent.Measures; face.Bounds = new Rect(60, 4, 140, 40); parent.UpdateLayout();
        Assert.Equal(measures, parent.Measures);
    });

    private sealed class CountingGrid : Grid
    {
        public int Measures;
        protected override Size MeasureOverride(Size availableSize) { Measures++; return base.MeasureOverride(availableSize); }
    }

    [Fact]
    public void Connected_header_rim_keeps_side_and_bottom_lighting_without_a_line_across_the_join() => Sta(() =>
    {
        var outer = SurfaceContour.Create(new Size(240, 56), new CornerRadius(14), new Thickness());
        var inner = SurfaceContour.Create(new Size(240, 56), new CornerRadius(14), new Thickness(), 1);
        var original = SurfaceContour.Ring(outer, inner);
        var connected = SurfaceContour.WithoutTop(original, outer.Bounds, new CornerRadius(14));
        Assert.True(original.FillContains(new Point(120, .5)));
        Assert.False(connected.FillContains(new Point(120, .5)));
        Assert.True(connected.FillContains(new Point(.5, 28)));
        Assert.True(connected.FillContains(new Point(120, 55.5)));
        Assert.True(connected.IsFrozen);
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2d)]
    public void Tab_face_repaints_its_actual_contour_without_remeasuring_headers(double scale) => Sta(() =>
    {
        var face = new TabSelectionSurface { Bounds = new Rect(20, 4, 160, 40), Background = Brushes.SlateBlue,
            CornerRadius = new(10, 10, 0, 0), Rim = Brushes.Transparent };
        var parent = new CountingGrid(); parent.Children.Add(face);
        parent.Measure(new Size(240, 48)); parent.Arrange(new Rect(0, 0, 240, 48)); parent.UpdateLayout();
        var measures = parent.Measures;
        byte AlphaAt(int x, int y)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Round(240 * scale), (int)Math.Round(48 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(parent); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels[((int)Math.Round(y * scale) * bitmap.PixelWidth + (int)Math.Round(x * scale)) * 4 + 3];
        }
        Assert.Equal(0, AlphaAt(20, 4)); Assert.True(AlphaAt(30, 24) > 200);
        Assert.True(AlphaAt(20, 42) > 200); // Square lower corner joins the editor without a seam.
        face.Bounds = new Rect(70, 4, 120, 40); parent.UpdateLayout();
        Assert.Equal(measures, parent.Measures); Assert.Equal(0, AlphaAt(30, 24));
        Assert.Equal(0, AlphaAt(70, 4)); Assert.True(AlphaAt(80, 24) > 200);
        Assert.False(face.IsHitTestVisible);
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2d)]
    public void History_scroll_tiles_preserve_live_row_height_at_fractional_dpi(double scale) => Sta(() =>
    {
        var fixtures = new (string Key, object Item)[] {
            (SkiaTileRenderers.KeyStudyHistoryRow, new FlashcardStudyHistoryWindow.StudyDisplayItem {
                Title = "A deliberately long history title to exercise narrow cards", Details = "Set 1 — Practice", StatusText = "Unknown", StatusKind = "Unknown" }),
            (SkiaTileRenderers.KeyAttemptHistoryRow, new QuizAttemptHistoryWindow.AttemptDisplayItem {
                QuizTitle = "A deliberately long quiz title to exercise narrow cards", DateDisplay = "08 Oct", ScoreDisplay = "9/10", TimeDisplay = "4 min", Passed = true }) };
        foreach (var fixture in fixtures)
        foreach (var width in new[] { 380f, 560f })
        {
            var renderer = SkiaTileRenderers.ForKey(fixture.Key)!;
            using var image = renderer.Render(fixture.Item, scale, width, out var height);
            Assert.InRange(Math.Abs(height - 84), 0, .01);
            Assert.Equal((int)Math.Ceiling(84 * scale), image.Height);
            Assert.Equal((int)Math.Ceiling(width * scale), image.Width);
            Assert.Equal(0, image.GetPixel(0, 0).Alpha);
            Assert.True(image.GetPixel((int)(20 * scale), (int)(20 * scale)).Alpha > 200);
        }
    });

    private sealed class ResourceHost : IDisposable
    {
        public Window Window { get; }
        public ResourceHost(FrameworkElement element)
        {
            Window = new Window { Width = 600, Height = 400, Content = element, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            foreach (var name in new[] { "Tokens", "LightTheme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" })
                Window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml") });
            Window.Show(); Window.UpdateLayout();
        }
        public void Dispose() => Window.Close();
    }
}
