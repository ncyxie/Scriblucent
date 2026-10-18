using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DashboardLoadingTests
{
    [Fact]
    public void The_sweep_reuses_its_drawing_and_resize_rebuilds_it_once() => Sta(() =>
    {
        var overlay = CreateOverlay();
        var window = new Window { Content = overlay, Width = 850, Height = 680, ShowInTaskbar = false };
        var previous = MotionPolicy.Preference;
        try
        {
            MotionPolicy.Configure(MotionPreference.Automatic, false);
            window.Show(); window.Activate(); overlay.StartWave(); Pump(60);
            Assert.True(overlay.IsWaveRunning);
            var builds = overlay.DrawingBuildCount;
            Pump(200);
            Assert.Equal(builds, overlay.DrawingBuildCount);
            window.Width += 40; Pump(50);
            Assert.Equal(builds + 1, overlay.DrawingBuildCount);
        }
        finally { window.Close(); MotionPolicy.Configure(previous, false); }
        Assert.False(overlay.IsWaveRunning);
    });

    [Fact]
    public void Motion_policy_and_visibility_pause_and_resume_the_same_loading_request() => Sta(() =>
    {
        var overlay = CreateOverlay();
        var window = new Window { Content = overlay, Width = 850, Height = 680, ShowInTaskbar = false };
        var previous = MotionPolicy.Preference;
        try
        {
            window.Show(); window.Activate();
            MotionPolicy.Configure(MotionPreference.Reduced, false); overlay.StartWave(); Pump(30);
            Assert.False(overlay.IsWaveRunning);
            MotionPolicy.Configure(MotionPreference.Automatic, false); Pump(30);
            Assert.True(overlay.IsWaveRunning);
            overlay.Visibility = Visibility.Collapsed; Pump(20); Assert.False(overlay.IsWaveRunning);
            overlay.Visibility = Visibility.Visible; Pump(20); Assert.True(overlay.IsWaveRunning);
            MotionPolicy.Configure(MotionPreference.Off, false); Assert.False(overlay.IsWaveRunning);
            overlay.StopWave(); MotionPolicy.Configure(MotionPreference.Automatic, false); Assert.False(overlay.IsWaveRunning);
        }
        finally { window.Close(); MotionPolicy.Configure(previous, false); }
    });

    [Fact]
    public void Inset_surfaces_are_opaque_and_theme_changes_refresh_the_retained_drawing() => Sta(() =>
    {
        var overlay = CreateOverlay();
        overlay.SetLayout(new() { ShowCalendar = true, ExpandCalendar = true, CalendarFirst = true });
        overlay.Measure(new Size(850, 680)); overlay.Arrange(new Rect(0, 0, 850, 680));
        overlay.UpdateLayout(); Pump(20);
        var image = new RenderTargetBitmap(850, 680, 96, 96, PixelFormats.Pbgra32); image.Render(overlay);
        var pixel = new byte[4]; image.CopyPixels(new Int32Rect(40, 75, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[3]); Assert.InRange(pixel[2], 239, 241);
        var builds = overlay.DrawingBuildCount;
        overlay.InsetBrush = Brushes.DimGray;
        overlay.UpdateLayout(); image.Render(overlay);
        image.CopyPixels(new Int32Rect(40, 75, 1, 1), pixel, 4, 0);
        Assert.InRange(pixel[2], 104, 106);
        Assert.Equal(builds + 1, overlay.DrawingBuildCount);
    });

    private static DashboardLoadingOverlay CreateOverlay() => new()
    {
        Background = Brushes.Transparent, SurfaceBrush = Brushes.White,
        InsetBrush = new SolidColorBrush(Color.FromRgb(240, 241, 246)), DetailBrush = Brushes.Gray,
        AccentBrush = new SolidColorBrush(Color.FromRgb(67, 92, 236))
    };
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
