using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class PremiumUiTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(1d)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2d)]
    public void Calendar_numerals_stay_optically_centered_when_reused_and_resized(double scale) => Sta(() =>
    {
        var label = new CalendarDayLabel { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif"),
            FontSize = 13, Foreground = Brushes.Black };
        foreach (var fontSize in new[] { 13d, 14d, 13d })
        foreach (var number in new[] { "1", "8", "10", "11", "20", "28", "31", "1" })
        {
            label.Content = number; label.FontSize = fontSize;
            label.Measure(new Size(28, 28)); label.Arrange(new Rect(0, 0, 28, 28)); label.UpdateLayout();
            var bitmap = Capture(label, scale);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var left = bitmap.PixelWidth; var right = -1; var top = bitmap.PixelHeight; var bottom = -1;
            for (var y = 0; y < bitmap.PixelHeight; y++) for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] < 24) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            Assert.True(right >= left, "The numeral was not rendered.");
            Assert.InRange(Math.Abs((left + right + 1d) / 2 - bitmap.PixelWidth / 2d), 0, 1);
            Assert.InRange(Math.Abs((top + bottom + 1d) / 2 - bitmap.PixelHeight / 2d), 0, 1);
        }
        label.Foreground = Brushes.Blue;
        label.UpdateLayout();
        var colored = Capture(label, scale); var data = new byte[colored.PixelWidth * colored.PixelHeight * 4];
        colored.CopyPixels(data, colored.PixelWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, data.Length / 4), index => data[index * 4] > 100 && data[index * 4 + 2] == 0);
    });

    private static RenderTargetBitmap Capture(FrameworkElement element, double scale)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, 28, 28));
        var image = new RenderTargetBitmap((int)Math.Round(28 * scale), (int)Math.Round(28 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual); return image;
    }

    [Fact]
    public void Responsive_statistics_flow_keeps_sections_and_form_state_through_breakpoints() => Sta(() =>
    {
        var panel = new ResponsiveSectionPanel();
        var entry = new TextBox { Text = "Retained text" };
        var sections = new[] { new Border { Height = 200, Child = entry }, new Border { Height = 400 },
            new Border { Height = 200 }, new Border { Height = 100 } };
        foreach (var section in sections) panel.Children.Add(section);
        foreach (var width in new[] { 900d, 640d, 900d, 640d })
        {
            panel.Measure(new Size(width, double.PositiveInfinity));
            Assert.Equal(width >= 760 ? 520 : 960, panel.DesiredSize.Height);
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            var rectangles = sections.Select(section => new Rect(section.TranslatePoint(new Point(), panel), section.RenderSize)).ToArray();
            for (var i = 0; i < rectangles.Length; i++) for (var j = i + 1; j < rectangles.Length; j++)
                Assert.False(rectangles[i].IntersectsWith(rectangles[j]));
            Assert.Equal("Retained text", entry.Text); Assert.Same(entry, sections[0].Child);
        }
        sections[1].Visibility = Visibility.Collapsed;
        panel.Measure(new Size(640, double.PositiveInfinity));
        Assert.Equal(540, panel.DesiredSize.Height);
    });
}
