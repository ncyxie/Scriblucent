using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Tests;

[CollectionDefinition("Layout reveal", DisableParallelization = true)]
public class LayoutRevealCollection;

[Collection("Layout reveal")]
public class LayoutRevealTests
{
    [Theory]
    [InlineData(Orientation.Vertical)]
    [InlineData(Orientation.Horizontal)]
    public void Snap_preserves_content_size_margin_bindings_and_disabled_state(Orientation axis) => Sta(() =>
    {
        using var h = new Harness();
        var binding = new Binding { Source = 120.0 };
        h.Content.SetBinding(FrameworkElement.WidthProperty, binding);
        h.Content.Margin = new Thickness(7, 9, 11, 13);
        h.Content.IsEnabled = false;
        UiMotion.AnimateLayoutReveal(h.Content, true, axis: axis, animate: false); h.Layout();
        Assert.Same(binding, BindingOperations.GetBindingBase(h.Content, FrameworkElement.WidthProperty));
        Assert.Equal(120, h.Content.Width);
        Assert.Equal(60, h.Content.Height);
        Assert.Equal(new Thickness(7, 9, 11, 13), h.Content.Margin);
        Assert.False(h.Content.IsEnabled);
        Assert.True(double.IsNaN(h.Host.Extent));
        Assert.False(h.Host.ClipToBounds);
        UiMotion.AnimateLayoutReveal(h.Content, false, axis: axis, animate: false); h.Layout();
        Assert.Equal(Visibility.Collapsed, h.Host.Visibility);
        Assert.Equal(0, h.Host.DesiredSize.Width);
        Assert.Equal(0, h.Host.DesiredSize.Height);
    });

    [Theory]
    [InlineData(Orientation.Vertical)]
    [InlineData(Orientation.Horizontal)]
    public void Content_stays_full_size_and_opaque_during_reveal(Orientation axis) => Sta(() =>
    {
        using var h = new Harness();
        var transform = new TranslateTransform(2, 3);
        h.Content.RenderTransform = transform;
        UiMotion.AnimateLayoutReveal(h.Content, true, axis: axis); h.Pump(70);
        Assert.Equal(1, h.Content.Opacity);
        Assert.Same(transform, h.Content.RenderTransform);
        Assert.Equal(2, transform.X); Assert.Equal(3, transform.Y);
        Assert.Equal(120, h.Content.ActualWidth); Assert.Equal(60, h.Content.ActualHeight);
        if (!UiMotion.IsMotionDisabled)
        {
            Assert.InRange(h.Host.Extent, .01, axis == Orientation.Vertical ? 59.99 : 119.99);
            Assert.False(h.Host.IsHitTestVisible);
        }
        h.Pump(300);
        Assert.Equal(Visibility.Visible, h.Host.Visibility);
        Assert.True(double.IsNaN(h.Host.Extent));
        Assert.True(h.Host.IsHitTestVisible);
    });

    [Fact]
    public void Reversal_cancels_old_callbacks_and_finishes_the_latest_request() => Sta(() =>
    {
        using var h = new Harness(); int stale = 0, latest = 0;
        UiMotion.AnimateLayoutReveal(h.Content, true);
        if (!UiMotion.IsMotionDisabled)
            typeof(RenderTransitionClock).GetMethod("Advance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(h.Clock(), [55d]);
        UiMotion.AnimateLayoutReveal(h.Content, false, () => stale++);
        if (!UiMotion.IsMotionDisabled)
            typeof(RenderTransitionClock).GetMethod("Advance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(h.Clock(), [35d]);
        UiMotion.AnimateLayoutReveal(h.Content, true, () => latest++); h.Pump(350);
        Assert.Equal(UiMotion.IsMotionDisabled ? 1 : 0, stale); Assert.Equal(1, latest);
        Assert.Equal(Visibility.Visible, h.Host.Visibility);
        Assert.Equal(Visibility.Visible, h.Content.Visibility);
        Assert.False(h.Host.ClipToBounds);
    });

    [Fact]
    public void Fixed_grid_slot_still_clips_to_the_animated_extent() => Sta(() =>
    {
        using var h = new Harness();
        h.Host.HorizontalAlignment = HorizontalAlignment.Stretch;
        UiMotion.AnimateLayoutReveal(h.Content, true, axis: Orientation.Horizontal); h.Pump(60);
        if (UiMotion.IsMotionDisabled) return;
        var clip = LayoutInformation.GetLayoutClip(h.Host);
        Assert.NotNull(clip);
        Assert.InRange(clip.Bounds.Width, .01, 119.99);
        Assert.Equal(h.Host.Extent, clip.Bounds.Width, 1);
        h.Pump(300);
        Assert.Null(LayoutInformation.GetLayoutClip(h.Host));
    });

    [Fact]
    public void Repeated_request_does_not_restart_the_clock() => Sta(() =>
    {
        using var h = new Harness();
        UiMotion.AnimateLayoutReveal(h.Content, true); h.Pump(60);
        if (UiMotion.IsMotionDisabled) return;
        var clock = h.Clock();
        var field = typeof(RenderTransitionClock).GetField("_stopwatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stopwatch = field.GetValue(clock);
        UiMotion.AnimateLayoutReveal(h.Content, true);
        Assert.Same(stopwatch, field.GetValue(clock));
        h.Pump(300);
    });

    [Fact]
    public void Changing_content_size_during_motion_retargets_and_restores_auto_sizing() => Sta(() =>
    {
        using var h = new Harness();
        UiMotion.AnimateLayoutReveal(h.Content, true); h.Pump(50);
        h.Content.Height = 130; h.Layout(); h.Pump(350);
        Assert.Equal(130, h.Host.ActualHeight);
        Assert.True(double.IsNaN(h.Host.Extent));
        h.Content.Height = 90; h.Layout();
        Assert.Equal(90, h.Host.ActualHeight);
    });

    [Fact]
    public void Resize_remeasures_wrapped_text_during_reveal() => Sta(() =>
    {
        using var h = new Harness();
        h.Content.ClearValue(FrameworkElement.WidthProperty);
        h.Content.ClearValue(FrameworkElement.HeightProperty);
        h.Content.Child = new TextBlock { Text = string.Join(" ", Enumerable.Repeat("Pradinis skaičius", 30)), TextWrapping = TextWrapping.Wrap };
        UiMotion.AnimateLayoutReveal(h.Content, true); h.Pump(40);
        h.Window.Width = 220; h.Layout(); h.Pump(400); h.Settle();
        Assert.True(Math.Abs(h.Content.DesiredSize.Height - h.Host.ActualHeight) < .5,
            $"Desired={h.Content.DesiredSize.Height} actual={h.Host.ActualHeight} extent={h.Host.Extent}");
        Assert.True(double.IsNaN(h.Host.Extent));
        Assert.True(h.Host.ActualHeight > 60);
    });

    [Fact]
    public void Existing_grid_row_restores_to_auto_and_collapses_without_a_gap() => Sta(() =>
    {
        using var h = new Harness();
        h.Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        var row = h.Root.RowDefinitions[0];
        UiMotion.AnimateLayoutReveal(h.Content, true, row: row); h.Pump(320);
        Assert.True(row.Height.IsAuto);
        Assert.Equal(60, row.ActualHeight);
        UiMotion.AnimateLayoutReveal(h.Content, false, row: row); h.Pump(320);
        Assert.Equal(0, row.ActualHeight);
    });

    [Fact]
    public void Unloading_settles_the_latest_state_and_stops_the_clock() => Sta(() =>
    {
        using var h = new Harness(); int complete = 0;
        UiMotion.AnimateLayoutReveal(h.Content, true, animate: false);
        UiMotion.AnimateLayoutReveal(h.Content, false, () => complete++); h.Pump(30);
        h.Root.Children.Remove(h.Host); h.Pump(20);
        Assert.Equal(Visibility.Collapsed, h.Host.Visibility);
        Assert.Equal(1, complete);
        Assert.True(double.IsNaN(h.Host.Extent));
        h.Root.Children.Add(h.Host); h.Layout();
        UiMotion.AnimateLayoutReveal(h.Content, true); h.Pump(320);
        Assert.Equal(Visibility.Visible, h.Host.Visibility);
    });

    [Fact]
    public void Off_applies_both_states_immediately() => Sta(() =>
    {
        var previous = MotionPolicy.Preference;
        try
        {
            MotionPolicy.Configure(MotionPreference.Off, false);
            using var h = new Harness(); int calls = 0;
            UiMotion.AnimateLayoutReveal(h.Content, true, () => calls++);
            Assert.Equal(Visibility.Visible, h.Host.Visibility);
            Assert.True(double.IsNaN(h.Host.Extent));
            UiMotion.AnimateLayoutReveal(h.Content, false, () => calls++);
            Assert.Equal(Visibility.Collapsed, h.Host.Visibility);
            Assert.Equal(2, calls);
        }
        finally { MotionPolicy.Configure(previous, false); }
    });

    [Fact]
    public void Inline_status_holds_then_fades_and_releases_its_layout_space() => Sta(() =>
    {
        using var h = new Harness(); int calls = 0;
        var label = new TextBlock { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        h.Root.Children.Add(label); h.Layout(); Assert.Equal(0, label.DesiredSize.Width);
        UiMotion.FlashInlineStatus(label, "Saving", () => calls++, holdMs: 80);
        h.Pump(30); Assert.Equal("Saving", label.Text); Assert.Equal(Visibility.Visible, label.Visibility);
        h.Pump(450); h.SettleNative(label, MotionChannel.Presence); Assert.Equal(Visibility.Collapsed, label.Visibility); Assert.Equal(1, calls);
        h.Layout(); Assert.Equal(0, label.DesiredSize.Width);
    });

    [Fact]
    public void Trailing_reveal_keeps_the_button_side_of_content_anchored() => Sta(() =>
    {
        using var h = new Harness();
        UiMotion.AnimateLayoutReveal(h.Content, true, axis: Orientation.Horizontal, animate: false);
        h.Host.RevealFromEnd = true; h.Host.Extent = 45; h.Layout();
        Assert.Equal(45, h.Host.ActualWidth);
        Assert.Equal(120, h.Content.ActualWidth);
        Assert.Equal(45, h.Content.TranslatePoint(new Point(120, 0), h.Host).X);
        Assert.Equal(-75, h.Content.TranslatePoint(new Point(), h.Host).X);
        h.Host.Extent = 95; h.Layout();
        Assert.Equal(95, h.Content.TranslatePoint(new Point(120, 0), h.Host).X);
        Assert.Equal(-25, h.Content.TranslatePoint(new Point(), h.Host).X);
    });

    [Fact]
    public void Repeated_typing_feedback_does_not_restart_status_reveal() => Sta(() =>
    {
        var previous = MotionPolicy.Preference;
        MotionPolicy.Configure(MotionPreference.Full, false);
        try
        {
            using var h = new Harness();
            var label = new TextBlock { Text = "Edited", Visibility = Visibility.Collapsed };
            h.Root.Children.Add(label); h.Layout();
            UiMotion.SetInlineStatusVisible(label, true); h.Pump(50);
            Assert.InRange(label.Opacity, .001, .999);
            for (int i = 0; i < 12; i++) { UiMotion.SetInlineStatusVisible(label, true); h.Pump(25); }
            Assert.InRange(label.Opacity, .98, 1);
            UiMotion.SetInlineStatusVisible(label, false); h.Pump(40);
            UiMotion.SetInlineStatusVisible(label, true); h.Pump(320);
            h.SettleNative(label, MotionChannel.Presence);
            Assert.Equal(Visibility.Visible, label.Visibility); Assert.Equal(1, label.Opacity);
        }
        finally { ReducedMotionPreferences.Configure(previous); }
    });

    [Theory]
    [InlineData(MotionPreference.Full)]
    [InlineData(MotionPreference.Reduced)]
    [InlineData(MotionPreference.Off)]
    public void Floating_command_reversal_preserves_full_content_and_cancels_stale_exit(MotionPreference preference) => Sta(() =>
    {
        var previous = MotionPolicy.Preference;
        MotionPolicy.Configure(preference, false);
        try
        {
            using var h = new Harness(); int stale = 0, finished = 0;
            FloatingCommandMotion.SetVisible(h.Content, false, animate: false);
            FloatingCommandMotion.SetVisible(h.Content, true); h.Pump(35);
            Assert.Equal(60, h.Host.ActualHeight); Assert.False(h.Host.ClipToBounds);
            FloatingCommandMotion.SetVisible(h.Content, false, onComplete: () => stale++);
            if (preference != MotionPreference.Off)
            {
                Assert.False(h.Content.IsHitTestVisible);
                FloatingCommandMotion.SetVisible(h.Content, true); h.Pump(340);
                h.SettleNative(h.Content, MotionChannel.Presence);
                Assert.Equal(0, stale);
            }
            else { Assert.Equal(1, stale); FloatingCommandMotion.SetVisible(h.Content, true); }
            Assert.Equal(Visibility.Visible, h.Content.Visibility); Assert.Equal(1, h.Content.Opacity);
            Assert.True(h.Content.IsHitTestVisible);
            var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(h.Content);
            Assert.Equal(1, scale.ScaleX); Assert.Equal(0, translate.Y);
            FloatingCommandMotion.SetVisible(h.Content, false, onComplete: () => finished++); h.Pump(260);
            h.SettleNative(h.Content, MotionChannel.Presence);
            Assert.Equal(Visibility.Collapsed, h.Host.Visibility); Assert.Equal(1, finished);
            Assert.False(NativeSurfaceMotion.IsRunning(h.Content, MotionChannel.Presence));
        }
        finally { ReducedMotionPreferences.Configure(previous); }
    });

    [Fact]
    public void Soft_search_reversal_holds_the_visible_pose_and_returns_auto_size() => Sta(() =>
    {
        var previous = MotionPolicy.Preference;
        MotionPolicy.Configure(MotionPreference.Full, false);
        try
        {
            using var h = new Harness(); h.Host.RevealFromEnd = true; int stale = 0;
            UiMotion.AnimateLayoutReveal(h.Content, true, axis: Orientation.Horizontal, soften: true); h.Pump(65);
            Assert.InRange(h.Host.Extent, .001, 119.999); Assert.InRange(h.Content.Opacity, .001, .999);
            var extent = h.Host.Extent; var opacity = h.Content.Opacity;
            UiMotion.AnimateLayoutReveal(h.Content, false, () => stale++, axis: Orientation.Horizontal, soften: true);
            Assert.InRange(Math.Abs(h.Host.Extent - extent), 0, .01);
            Assert.InRange(Math.Abs(h.Content.Opacity - opacity), 0, .01);
            UiMotion.AnimateLayoutReveal(h.Content, true, axis: Orientation.Horizontal, soften: true); h.Pump(360);
            h.SettleNative(h.Host, MotionChannel.Layout);
            Assert.Equal(0, stale); Assert.True(double.IsNaN(h.Host.Extent)); Assert.Equal(1, h.Content.Opacity);
            Assert.True(h.Host.IsHitTestVisible); Assert.False(h.Host.ClipToBounds);
        }
        finally { ReducedMotionPreferences.Configure(previous); }
    });

    private sealed class Harness : IDisposable
    {
        internal Border Content { get; } = new() { Width = 120, Height = 60, Visibility = Visibility.Collapsed };
        internal LayoutRevealHost Host { get; }
        internal Grid Root { get; } = new();
        internal Window Window { get; }
        private readonly DispatcherTimer _ticker;
        private static readonly ConditionalWeakTable<object, RenderTransitionClock> Clocks =
            (ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion).GetField("Clocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        private static readonly MethodInfo Tick = typeof(RenderTransitionClock).GetMethod("OnRendering", BindingFlags.NonPublic | BindingFlags.Instance)!;
        internal Harness()
        {
            Host = new LayoutRevealHost { Child = Content, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            Root.Children.Add(Host);
            Window = new Window { Content = Root, Width = 420, Height = 450, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            Window.Show(); Layout();
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _ticker.Tick += (_, _) => { if (Clocks.TryGetValue(Host, out var clock)) Tick.Invoke(clock, [null, EventArgs.Empty]); };
            _ticker.Start();
        }
        internal RenderTransitionClock Clock() => Clocks.TryGetValue(Host, out var clock) ? clock : throw new InvalidOperationException();
        internal void Layout() => Window.UpdateLayout();
        internal void Settle()
        {
            for (int attempt = 0; attempt < 50 && !double.IsNaN(Host.Extent); attempt++) Pump(20);
        }
        internal void SettleNative(UIElement element, MotionChannel channel)
        {
            for (int attempt = 0; attempt < 80 && NativeSurfaceMotion.IsRunning(element, channel); attempt++) Pump(20);
            Assert.False(NativeSurfaceMotion.IsRunning(element, channel));
        }
        internal void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); Layout();
        }
        public void Dispose() { _ticker.Stop(); Window.Close(); }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Reveal scenario timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
