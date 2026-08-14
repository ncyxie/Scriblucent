using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Scriblucent.Tests;

/// <summary>
/// Regression test for the scrollbar thumb's "rounded top, flat bottom" artifact. The stock
/// WPF Track computes the thumb slot from the viewport/extent ratio (fractional in general);
/// with a min-height on the thumb the template's border overflows the slot and its bottom
/// gets cut square. With no min-height the border matches the slot and both ends render
/// rounded at any size. Renders the real Track + Thumb (exact app template geometry) across
/// a matrix of track heights, values and DPIs, then pixel-inspects the topmost/bottommost
/// fill rows: a rounded end tapers to at most 3 fill pixels.
/// </summary>
public class ScrollBarThumbRenderTests
{
    [Fact]
    public void ThumbBottomCorners_AreRounded_AtAllHeightsAndPositions()
        => RunOnSta(RunProbe);

    private static void RunProbe()
    {
        foreach (var dpi in new[] { 96.0, 120.0 })
        {
            foreach (var trackHeight in new[] { 100.0, 100.5, 101.3 })
            {
                foreach (var value in new[] { 0.0, 13.7, 500.0 })
                {
                    var (topFill, bottomFill) = MeasureTrackThumb(trackHeight, value, dpi);
                    Assert.True(
                        topFill <= 3,
                        $"top end must be rounded (dpi={dpi:F0} trackH={trackHeight:F1} value={value:F1}, topFill={topFill})");
                    Assert.True(
                        bottomFill <= 3,
                        $"bottom end must be rounded (dpi={dpi:F0} trackH={trackHeight:F1} value={value:F1}, bottomFill={bottomFill})");
                }
            }
        }
    }

    private static (int Top, int Bottom) MeasureTrackThumb(double trackHeight, double value, double dpi)
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Black);
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        borderFactory.SetValue(Border.MarginProperty, new Thickness(1));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var thumb = new Thumb
        {
            Width = 6,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = borderFactory }
        };

        var track = new Track
        {
            Minimum = 0,
            Maximum = 1000,
            Value = value,
            ViewportSize = 100,
            Orientation = Orientation.Vertical,
            IsDirectionReversed = true,
            Thumb = thumb,
            DecreaseRepeatButton = new RepeatButton { Background = Brushes.Transparent, Style = null },
            IncreaseRepeatButton = new RepeatButton { Background = Brushes.Transparent, Style = null }
        };

        var root = new Grid
        {
            Width = 12,
            Height = trackHeight,
            Background = Brushes.White,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        root.Children.Add(track);

        root.Measure(new Size(12, trackHeight));
        root.Arrange(new Rect(0, 0, 12, trackHeight));
        root.UpdateLayout();

        var scale = dpi / 96.0;
        var pixelWidth = (int)Math.Ceiling(12 * scale);
        var pixelHeight = (int)Math.Ceiling(trackHeight * scale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);

        // A fill pixel is an opaque dark pixel (alpha-aware so the RTB's transparent
        // background beyond a fractional-height root is never counted as fill).
        var fillCounts = new int[pixelHeight];
        for (var row = 0; row < pixelHeight; row++)
        {
            var count = 0;
            for (var col = 0; col < pixelWidth; col++)
            {
                var o = (row * pixelWidth + col) * 4;
                if (pixels[o + 3] > 128 && pixels[o] < 64 && pixels[o + 1] < 64 && pixels[o + 2] < 64)
                    count++;
            }

            fillCounts[row] = count;
        }

        var topFill = -1;
        var bottomFill = -1;
        for (var row = 0; row < pixelHeight; row++)
        {
            if (fillCounts[row] <= 0)
                continue;

            if (topFill < 0)
                topFill = fillCounts[row];
            bottomFill = fillCounts[row];
        }

        return (topFill, bottomFill);
    }

    private static void RunOnSta(Action scenario)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                scenario();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA scenario timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
