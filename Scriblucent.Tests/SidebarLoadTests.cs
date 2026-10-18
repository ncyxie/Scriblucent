using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class SidebarLoadTests
{
    [Fact]
    public void Sidebar_width_advances_without_a_render_callback_and_releases_its_wakeup_on_settle() => Sta(() =>
    {
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        var root = new Grid(); var column = new ColumnDefinition { Width = new GridLength(64) };
        root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Border(); SidebarResizeMotion.SetIsEnabled(sidebar, true); root.Children.Add(sidebar);
        var window = new Window { Content = root, Width = 900, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            UiMotion.AnimateSidebarChrome(sidebar, column, 64, 232);
            var table = typeof(SidebarResizeMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object?[] args = [sidebar, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
            var state = args[1]!;
            var timer = (DispatcherTimer)state.GetType().GetField("_frameWake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            Assert.True(timer.IsEnabled);
            // Do not pump the dispatcher: neither a WPF animation tick nor Rendering
            // can deliver width values during this gap. The finite wake must still advance.
            Thread.Sleep(70);
            state.GetType().GetMethod("WakeFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [null, EventArgs.Empty]);
            Assert.InRange(column.Width.Value, 75, 231);
            MotionPolicy.Configure(MotionPreference.Off, false);
            Assert.Equal(232, column.Width.Value);
            Assert.False(timer.IsEnabled);
            Pump(); Assert.Equal(232, column.Width.Value);
            Assert.Null(state.GetType().GetField("_layoutObserver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
            Assert.False(RenderTransitionClock.IsAnyRunning);
        }
        finally { window.Close(); MotionPolicy.Configure(MotionPreference.Automatic, false); }
    });

    [Fact]
    public void Shared_contours_reuse_only_frozen_exact_matches_and_remain_bounded() => Sta(() =>
    {
        var size = new Size(180, 40); var radius = new CornerRadius(8); var frame = new Thickness(.8);
        var original = SurfaceContour.Create(size, radius, frame);
        Assert.True(original.IsFrozen);
        Assert.Same(original, SurfaceContour.Create(size, radius, frame));
        Assert.NotSame(original, SurfaceContour.Create(size, radius, frame, .8));
        var ring = SurfaceContour.Ring(size, radius, frame, 0, .8);
        Assert.True(ring.IsFrozen);
        Assert.Same(ring, SurfaceContour.Ring(size, radius, frame, 0, .8));
        for (int i = 0; i < 2000; i++) SurfaceContour.Create(new Size(200 + i, 40), radius, frame);
        var cache = (IDictionary)typeof(SurfaceContour).GetField("Contours", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.InRange(cache.Count, 1, 256);
        Assert.All(cache.Values.Cast<Geometry>(), geometry => Assert.True(geometry.IsFrozen));
    });

    [Fact]
    public void Stream_surface_rims_preserve_the_existing_fill_at_fractional_and_asymmetric_corners() => Sta(() =>
    {
        foreach (var radius in new[] { new CornerRadius(14), new CornerRadius(0, 5, 20, 11) })
        foreach (var frame in new[] { new Thickness(), new Thickness(.8, 1.6, 2.4, .8) })
        {
            var size = new Size(73.6, 36.8);
            var previous = SurfaceContour.Ring(SurfaceContour.Create(size, radius, frame, .8), SurfaceContour.Create(size, radius, frame, 1.6));
            var stream = SurfaceContour.Ring(size, radius, frame, .8, .8);
            Assert.Equal(previous.Bounds, stream.Bounds);
            for (double x = 0; x <= size.Width; x += .4)
            for (double y = 0; y <= size.Height; y += .4)
                Assert.Equal(previous.FillContains(new Point(x, y)), stream.FillContains(new Point(x, y)));
            _ = stream.Bounds;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4096; i++) _ = stream.Bounds;
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128 * 1024);
        }
    });

    [Fact]
    public void Short_group_titles_reuse_their_measurement_but_long_and_changed_titles_still_fit() => Sta(() =>
    {
        var title = new CountingTitle { NaturalWidth = 160 };
        var panel = new DashboardGroupTitlePanel();
        panel.Children.Add(new Border()); panel.Children.Add(title);
        for (int i = 0; i < 32; i++)
        {
            panel.Measure(new Size(700 - i * 3, 36));
            panel.Arrange(new Rect(0, 0, 700 - i * 3, 36));
        }
        Assert.InRange(title.Measures, 1, 2);
        Assert.Equal(160, title.ActualWidth);
        title.NaturalWidth = 900; title.InvalidateMeasure();
        panel.Measure(new Size(500, 36)); panel.Arrange(new Rect(0, 0, 500, 36));
        Assert.Equal(500, title.ActualWidth);
        panel.Measure(new Size(400, 36)); panel.Arrange(new Rect(0, 0, 400, 36));
        Assert.Equal(400, title.ActualWidth);
        title.NaturalWidth = 80; title.InvalidateMeasure();
        panel.Measure(new Size(500, 36)); panel.Arrange(new Rect(0, 0, 500, 36));
        Assert.Equal(80, title.ActualWidth);
    });

    [Theory]
    [InlineData(64, 232)]
    [InlineData(232, 64)]
    public void Sidebar_does_not_request_fractional_column_layouts_inside_the_same_endpoint_pixel(double from, double to) => Sta(() =>
    {
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        var root = new Grid { UseLayoutRounding = true };
        var column = new ColumnDefinition { Width = new GridLength(from) };
        root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Border(); SidebarResizeMotion.SetIsEnabled(sidebar, true);
        root.Children.Add(sidebar);
        var window = new Window { Content = root, Width = 900, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            UiMotion.AnimateSidebarChrome(sidebar, column, from, to);
            var seek = SidebarTestClock.Pause(sidebar);
            foreach (var ms in new[] { 0, 1, 2, 3, 4 })
            {
                seek(ms);
                window.UpdateLayout();
                Assert.Equal(from, column.Width.Value);
                Assert.Equal(from, SidebarResizeMotion.VisibleWidth(sidebar, 0));
            }
            MotionPolicy.Configure(MotionPreference.Off, false); window.UpdateLayout();
            Assert.Equal(to, column.Width.Value);
        }
        finally { window.Close(); MotionPolicy.Configure(MotionPreference.Automatic, false); }
    });

    [Fact]
    public void Auto_hide_only_owns_template_bars_and_reuses_an_in_progress_fade() => Sta(() =>
    {
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        var inner = new ScrollViewer { Content = new Border { Height = 2000 }, Height = 160 };
        var content = new CountingBorder { Height = 2000, Child = inner };
        var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        ScrollBarAutoHideBehavior.SetEnable(viewer, true);
        var window = new Window { Content = viewer, Width = 700, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            var property = (DependencyProperty)typeof(ScrollBarAutoHideBehavior).GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var state = viewer.GetValue(property);
            var bars = (List<ScrollBar>)state.GetType().GetProperty("ScrollBars")!.GetValue(state)!;
            Assert.NotEmpty(bars);
            Assert.All(bars, bar => Assert.Same(viewer, bar.TemplatedParent));
            content.Visits = 0;
            var refresh = state.GetType().GetMethod("RefreshScrollBars")!;
            for (int i = 0; i < 40; i++) refresh.Invoke(state, null);
            Assert.Equal(0, content.Visits);
            var set = state.GetType().GetMethod("SetShown")!;
            set.Invoke(state, [false]);
            var bar = bars.Single(b => b.Orientation == Orientation.Vertical);
            var initial = NativeState(bar);
            Assert.NotNull(initial);
            for (int i = 0; i < 40; i++) set.Invoke(state, [false]);
            Assert.Same(initial, NativeState(bar));
            set.Invoke(state, [true]);
            Assert.NotSame(initial, NativeState(bar));
        }
        finally { window.Close(); MotionPolicy.Configure(MotionPreference.Off, false); }
    });

    private static object? NativeState(UIElement element)
    {
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [element, null];
        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)! ? ((IDictionary)args[1]!)[MotionChannel.Presence] : null;
    }
    private sealed class CountingBorder : Border
    {
        internal int Visits;
        protected override int VisualChildrenCount { get { Visits++; return base.VisualChildrenCount; } }
    }
    private sealed class CountingTitle : FrameworkElement
    {
        internal int Measures;
        internal double NaturalWidth;
        protected override Size MeasureOverride(Size available)
        { Measures++; return new Size(Math.Min(NaturalWidth, available.Width), 24); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
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
