using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Behaviors;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionSystemTests
{
    private static readonly MethodInfo Advance = typeof(RenderTransitionClock).GetMethod("Advance", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Tick(RenderTransitionClock clock, double ms) => Advance.Invoke(clock, [ms]);

    [Fact]
    public void Delayed_segments_hold_the_previous_segment_and_completed_segments_stop_writing() => Sta(() =>
    {
        var clock = new RenderTransitionClock();
        var value = 0d; int firstWrites = 0, calls = 0;
        clock.Run([
            UiMotion.Track(100, UiMotion.OutCubic, 0, 1, v => { value = v; firstWrites++; }),
            UiMotion.Track(100, UiMotion.OutCubic, 1, 0, v => value = v, startDelayMs: 100)
        ], () => calls++);
        Tick(clock, 50); Assert.True(value > .5);
        Tick(clock, 100); var writes = firstWrites;
        Tick(clock, 150); Assert.InRange(value, .01, .49);
        Tick(clock, 175); Assert.Equal(writes, firstWrites);
        Tick(clock, 200); Tick(clock, 300);
        Assert.Equal(0, value); Assert.Equal(1, calls);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Reentrant_retarget_does_not_complete_or_overwrite_the_successor() => Sta(() =>
    {
        var clock = new RenderTransitionClock(); int stale = 0, latest = 0;
        bool retarget = true; double value = 0;
        clock.Run([UiMotion.Track(100, UiMotion.OutCubic, 0, 1, v =>
        {
            if (!retarget) return;
            retarget = false;
            clock.Run([UiMotion.Track(100, UiMotion.OutCubic, 3, 4, n => value = n)], () => latest++);
        })], () => stale++);
        Tick(clock, 50); Tick(clock, 100);
        Assert.Equal(4, value); Assert.Equal(0, stale); Assert.Equal(1, latest);
    });

    [Fact]
    public void Interaction_channels_preserve_structural_completion_and_transforms() => Sta(() =>
    {
        var element = new Border(); int calls = 0;
        var (_, structural) = UiMotion.EnsureScaleTranslateGroup(element);
        var hover = UiMotion.ChannelTranslate(element, MotionChannel.Interaction);
        Assert.NotSame(structural, hover);
        UiMotion.Run(element, [UiMotion.Track(100, UiMotion.OutCubic, 8, 0, v => structural.Y = v)], () => calls++);
        UiMotion.Run(element, MotionChannel.Interaction, [UiMotion.Track(100, UiMotion.OutCubic, 0, -3, v => hover.Y = v)]);
        var clocks = (System.Runtime.CompilerServices.ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion)
            .GetField("Clocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(clocks.TryGetValue(element, out var presence));
        Assert.True(clocks.TryGetValue(UiMotion.Channel(element, MotionChannel.Interaction), out var interaction));
        Tick(presence!, 100); Tick(interaction!, 100);
        Assert.Equal(1, calls); Assert.Equal(0, structural.Y); Assert.Equal(-3, hover.Y);
        UiMotion.Cleanup(element); Assert.Equal(0, hover.Y);
    });

    [Fact]
    public void Live_preference_changes_settle_active_operations_once() => Sta(() =>
    {
        var clock = new RenderTransitionClock(); int completed = 0; double value = 0;
        clock.Run([UiMotion.Track(220, UiMotion.OutCubic, 0, 1, v => value = v)], () => completed++);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Tick(clock, 500);
        Assert.Equal(1, value); Assert.Equal(1, completed);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Theory]
    [InlineData(MotionPreference.Full, 300)]
    [InlineData(MotionPreference.Reduced, 300)]
    [InlineData(MotionPreference.Off, 0)]
    public void Motion_sets_share_timing_and_only_off_disables_core_motion(MotionPreference preference, int duration) => Sta(() =>
    {
        var materialBefore = MotionPolicy.BackdropBlur;
        MotionPolicy.Configure(preference, false);
        Assert.Equal(duration, MotionPolicy.FadeDuration(300));
        Assert.Equal(preference != MotionPreference.Off, MotionPolicy.Spatial);
        Assert.Equal(preference == MotionPreference.Full, MotionPolicy.Celebration);
        Assert.Equal(preference == MotionPreference.Full ? 8 : 4, MotionPolicy.PresenceItemBudget);
        Assert.Equal(materialBefore, MotionPolicy.BackdropBlur);
    });

    [Fact]
    public void Live_off_clears_a_held_hover_after_settling_clocks() => Sta(() =>
    {
        var element = new Border();
        var hover = UiMotion.ChannelTranslate(element, MotionChannel.Interaction);
        UiMotion.Run(element, MotionChannel.Interaction,
            [UiMotion.Track(140, UiMotion.OutCubic, 0, -3, v => hover.Y = v)]);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(0, hover.Y);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Theory]
    [InlineData(MotionPreference.Full)]
    [InlineData(MotionPreference.Reduced)]
    public void System_accessibility_overrides_both_motion_sets(MotionPreference preference) => Sta(() =>
    {
        MotionPolicy.Configure(preference, true);
        Assert.Equal(MotionQuality.Off, MotionPolicy.Quality);
        Assert.False(MotionPolicy.Enabled);
        Assert.False(MotionPolicy.ExpressiveExtras);
    });

    [Fact]
    public void Reduced_procedural_motion_keeps_full_timing_and_every_intermediate_step() => Sta(() =>
    {
        var samples = new List<double[]>();
        foreach (var preference in new[] { MotionPreference.Full, MotionPreference.Reduced })
        {
            MotionPolicy.Configure(preference, false);
            var clock = new RenderTransitionClock(); var values = new List<double>();
            clock.Run([UiMotion.Track(300, UiMotion.InOutSine, 0, 1, values.Add)]);
            for (var time = 0; time <= 300; time += 10) Tick(clock, time);
            Assert.Equal(31, values.Count);
            Assert.InRange(values[15], .49, .51);
            Assert.Equal(1, values[^1]);
            samples.Add(values.ToArray());
        }
        Assert.Equal(samples[0], samples[1]);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Reduced_limits_arrivals_without_stagger_or_overshoot_and_settles_every_item() => Sta(() =>
    {
        using var h = new Harness();
        var items = Enumerable.Range(0, 8).Select(_ => new Border { Width = 40, Height = 40 }).ToArray();
        h.Window.Content = new StackPanel();
        var root = (StackPanel)h.Window.Content;
        foreach (var item in items) root.Children.Add(item);
        h.Pump(30); MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.All(items, item => Assert.True(item.IsLoaded));
        var completed = 0;
        UiMotion.AnimateStaggeredContentEnter(items, 280, onComplete: () => completed++);
        Assert.All(items.Take(4), item => Assert.True(NativeSurfaceMotion.IsRunning(item, MotionChannel.Presence)));
        Assert.All(items.Skip(4), item =>
        { Assert.Equal(1, item.Opacity); Assert.False(NativeSurfaceMotion.IsRunning(item, MotionChannel.Presence)); });
        var states = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var args = new object?[] { items[3], null };
        Assert.True((bool)states.GetType().GetMethod("TryGetValue")!.Invoke(states, args)!);
        var state = ((System.Collections.IDictionary)args[1]!)[MotionChannel.Presence]!;
        var tracks = (System.Collections.IEnumerable)state.GetType().GetField("Tracks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        foreach (var track in tracks)
        {
            var clock = (System.Windows.Media.Animation.AnimationClock)track!.GetType().GetField("Item2")!.GetValue(track)!;
            var frames = Assert.IsType<System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames>(clock.Timeline).KeyFrames;
            Assert.Equal(2, frames.Count);
            Assert.Equal(TimeSpan.Zero, frames[0].KeyTime.TimeSpan);
            Assert.InRange(frames[1].KeyTime.TimeSpan.TotalMilliseconds, 180, 280);
        }
        h.Pump(400);
        Assert.Equal(1, completed);
        Assert.All(items, item => { Assert.Equal(1, item.Opacity); Assert.Equal(1, UiMotion.EnsureScale(item).ScaleX); });
        Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Theory]
    [InlineData(0, MotionPreference.Full)]
    [InlineData(1, MotionPreference.Reduced)]
    [InlineData(2, MotionPreference.Off)]
    public void Renamed_motion_sets_preserve_saved_numeric_preferences(int saved, MotionPreference expected)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Scriblucent.Models.AppSettings>("{\"AnimationPreference\":" + saved + "}")!;
        Assert.Equal(expected, settings.AnimationPreference);
        Assert.Equal(saved, (int)expected);
    }

    [Fact]
    public void Temporary_cache_budget_rejects_large_surfaces_and_releases_on_cancel() => Sta(() =>
    {
        var baseline = MotionCacheBudget.ReservedPixels;
        var large = new Border { Width = 2500, Height = 1600 };
        large.Measure(new Size(2500, 1600)); large.Arrange(new Rect(0, 0, 2500, 1600));
        UiMotion.BeginMotionPrep(large);
        Assert.Null(large.CacheMode);
        UiMotion.EndMotionPrep(large);
        var element = new Border { Width = 100, Height = 100 };
        element.Measure(new Size(100, 100)); element.Arrange(new Rect(0, 0, 100, 100));
        UiMotion.RunComposited(element, [UiMotion.Track(220, UiMotion.OutCubic, 0, 1, _ => { })]);
        Assert.NotNull(element.CacheMode);
        UiMotion.Stop(element);
        Assert.Null(element.CacheMode);
        Assert.Equal(baseline, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Aggregate_cache_budget_is_bounded() => Sta(() =>
    {
        var baseline = MotionCacheBudget.ReservedPixels;
        var reservations = Enumerable.Range(0, 10).Select(_ => MotionCacheBudget.TryReservePixels(1_000_000)).ToArray();
        Assert.InRange(MotionCacheBudget.ReservedPixels, baseline, MotionCacheBudget.TotalPixels);
        foreach (var reservation in reservations) MotionCacheBudget.Release(reservation);
        Assert.Equal(baseline, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Native_delayed_fade_does_not_flash_its_final_base_value() => Sta(() =>
    {
        using var h = new Harness();
        h.Content.Opacity = 0;
        SurfaceMotion.Animate(h.Content, 1, 220, UiMotion.OutCubic, false, startDelayMs: 1000);
        h.Pump(25);
        Assert.InRange(h.Content.Opacity, 0, .01);
        SurfaceMotion.Stop(h.Content);
    });

    [Fact]
    public void Native_reversal_cancels_obsolete_completion_and_finishes_at_rest() => Sta(() =>
    {
        using var h = new Harness(); int stale = 0, latest = 0;
        h.Content.Opacity = 0;
        var translate = UiMotion.ChannelTranslate(h.Content, MotionChannel.Presence);
        translate.Y = 8;
        SurfaceMotion.Animate(h.Content, 1, 1000, UiMotion.OutCubic, false, () => stale++, translate);
        h.Pump(25);
        SurfaceMotion.Animate(h.Content, 0, 30, UiMotion.InQuad, false, () => latest++, translate, 8);
        h.Pump(250);
        Assert.Equal(0, stale); Assert.Equal(1, latest);
        Assert.Equal(0, h.Content.Opacity); Assert.Equal(8, translate.Y);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Large_reveal_uses_full_layout_with_clip_and_reversal_restores_it() => Sta(() =>
    {
        using var h = new Harness();
        var content = new Border { Width = 120, Height = 900, Visibility = Visibility.Collapsed };
        var host = new LayoutRevealHost { Child = content, VerticalAlignment = VerticalAlignment.Top };
        h.Window.Content = new Grid { Children = { host } }; h.Window.UpdateLayout();
        h.Pump(25);
        UiMotion.AnimateLayoutReveal(content, true);
        Assert.True(double.IsNaN(host.Extent));
        Assert.NotNull(host.Clip);
        var clocks = (System.Runtime.CompilerServices.ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion)
            .GetField("Clocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(clocks.TryGetValue(host, out var opening));
        Tick(opening!, 100);
        int stale = 0, latest = 0;
        UiMotion.AnimateLayoutReveal(content, false, () => stale++);
        UiMotion.AnimateLayoutReveal(content, true, () => latest++);
        h.Pump(500);
        Assert.Equal(0, stale); Assert.Equal(1, latest);
        Assert.Null(host.Clip); Assert.True(host.IsHitTestVisible);
        Assert.Equal(Visibility.Visible, host.Visibility);
    });

    [Fact]
    public void Confetti_is_bounded_and_independent_between_overlays() => Sta(() =>
    {
        using var h = new Harness();
        var a = new Canvas { Width = 300, Height = 300 };
        var b = new Canvas { Width = 300, Height = 300 };
        h.Window.Content = new Grid { Children = { a, b } }; h.Window.UpdateLayout(); h.Pump(25);
        for (int i = 0; i < 80; i++) { a.Children.Add(new Border()); b.Children.Add(new Border()); }
        if (!MotionPolicy.Celebration) return;
        StudyCelebration.PlayConfetti(a); StudyCelebration.PlayConfetti(b);
        Assert.Equal(81, a.Children.Count); Assert.Equal(81, b.Children.Count);
        StudyCelebration.Stop(a);
        Assert.Equal(80, a.Children.Count); Assert.Equal(Visibility.Visible, b.Visibility);
        StudyCelebration.Stop(b); Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Reduced_score_reveal_ends_visible_and_at_full_scale() => Sta(() =>
    {
        using var h = new Harness();
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        StudyCelebration.RevealScore(h.Content);
        h.Pump(250);
        Assert.Equal(1, h.Content.Opacity);
        var scale = UiMotion.EnsureScale(h.Content); Assert.Equal(1, scale.ScaleX); Assert.Equal(1, scale.ScaleY);
    });

    [Fact]
    public void Nested_overlay_suspension_is_scoped_to_each_window() => Sta(() =>
    {
        using var first = new Harness();
        using var second = new Harness();
        var overlayA = new Border(); var overlayB = new Border(); var overlayC = new Border();
        Panel.SetZIndex(overlayA, 250); Panel.SetZIndex(overlayB, 250); Panel.SetZIndex(overlayC, 250);
        ((Grid)first.Window.Content).Children.Add(overlayA);
        ((Grid)first.Window.Content).Children.Add(overlayB);
        ((Grid)second.Window.Content).Children.Add(overlayC);
        OverlayBackgroundSuspender.TrySuspend(overlayA);
        OverlayBackgroundSuspender.TrySuspend(overlayB);
        OverlayBackgroundSuspender.TrySuspend(overlayC);
        Assert.Equal(Visibility.Hidden, first.Content.Visibility);
        OverlayBackgroundSuspender.TryResume(overlayA);
        Assert.Equal(Visibility.Hidden, first.Content.Visibility);
        OverlayBackgroundSuspender.TryResume(overlayC);
        Assert.Equal(Visibility.Visible, second.Content.Visibility);
        Assert.Equal(Visibility.Hidden, first.Content.Visibility);
        OverlayBackgroundSuspender.TryResume(overlayB);
        Assert.Equal(Visibility.Visible, first.Content.Visibility);
    });

    [Fact]
    public void Large_reveal_retargets_resized_content_without_nan_geometry() => Sta(() =>
    {
        using var h = new Harness();
        var content = new Border { Height = 520, Visibility = Visibility.Collapsed };
        var host = new LayoutRevealHost { Child = content, VerticalAlignment = VerticalAlignment.Top };
        h.Window.Content = new Grid { Children = { host } }; h.Pump(25);
        UiMotion.AnimateLayoutReveal(content, true);
        content.Height = 720; host.InvalidateMeasure(); h.Window.UpdateLayout();
        h.Pump(500);
        Assert.Null(host.Clip);
        Assert.True(double.IsNaN(host.Extent));
        Assert.True(host.IsHitTestVisible);
        Assert.Equal(Visibility.Visible, content.Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keyboard_press_releases_on_lost_focus_for_buttons_and_chrome(bool chrome) => Sta(() =>
    {
        using var h = new Harness();
        var button = new Button { Content = "Action" };
        h.Window.Content = button; h.Pump(25);
        if (chrome) ChromeHoverMotionBehavior.SetIsEnabled(button, true);
        else ButtonPressMotionBehavior.SetIsEnabled(button, true);
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        RenderTransitionClock? clock = null;
        if (chrome) h.Pump(450);
        else
        {
            var clocks = (System.Runtime.CompilerServices.ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion)
                .GetField("Clocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.True(clocks.TryGetValue(UiMotion.Channel(button, MotionChannel.Interaction), out clock));
            Tick(clock!, 80);
        }
        Assert.Equal(1, UiMotion.ChannelTranslate(button, MotionChannel.Interaction).Y);
        button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, button, null)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        if (chrome) h.Pump(450); else Tick(clock!, 120);
        Assert.Equal(0, UiMotion.ChannelTranslate(button, MotionChannel.Interaction).Y);
        Assert.False(RenderTransitionClock.IsAnyRunning);
        UiMotion.Cleanup(button);
    });

    [Fact]
    public void Painted_drop_rim_preserves_border_layout_and_content_position() => Sta(() =>
    {
        using var h = new Harness();
        var label = new TextBlock { Text = "Stable content" };
        var border = new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Blue,
            Padding = new Thickness(12), Child = label };
        h.Window.Content = new System.Windows.Documents.AdornerDecorator { Child = border }; h.Pump(25);
        var position = label.TranslatePoint(new Point(), border);
        UiMotion.AnimateThickness(border, border.BorderThickness, new Thickness(2), 120);
        h.Window.UpdateLayout();
        Assert.Equal(new Thickness(1), border.BorderThickness);
        Assert.Equal(position, label.TranslatePoint(new Point(), border));
        h.Pump(250);
        UiMotion.AnimateThickness(border, border.BorderThickness, new Thickness(1), 120);
        h.Pump(250);
        Assert.Equal(new Thickness(1), border.BorderThickness);
        Assert.Equal(position, label.TranslatePoint(new Point(), border));
        AccentMotion.Clear(border);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    private sealed class Harness : IDisposable
    {
        internal Border Content = new() { Width = 120, Height = 60, Background = Brushes.CornflowerBlue };
        internal Window Window;
        internal Harness()
        {
            Window = new Window { Content = new Grid { Children = { Content } }, Width = 450, Height = 450,
                ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            Window.Show(); Window.UpdateLayout();
        }
        internal void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Min(16, milliseconds)) };
            timer.Tick += (_, _) =>
            {
                // Transparent fixtures can coast without rendering; exercise real clock
                // completion rather than allowing the first tick only at the deadline.
                if (Window.IsLoaded) Window.InvalidateVisual();
                if (elapsed.ElapsedMilliseconds < milliseconds) return;
                timer.Stop(); frame.Continue = false;
            };
            timer.Start(); Dispatcher.PushFrame(frame); Window.UpdateLayout();
        }
        public void Dispose() { UiMotion.Cleanup(Content); Window.Close(); Pump(25); }
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previous = MotionPolicy.Preference;
            var system = (bool)typeof(MotionPolicy).GetField("_systemOff", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            try { MotionPolicy.Configure(MotionPreference.Full, false); action(); }
            catch (Exception ex) { failure = ex; }
            finally { MotionPolicy.Configure(previous, system); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Motion scenario timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Transform_helpers_preserve_existing_tracks_and_popup_anchor() => Sta(() =>
    {
        var element = new Border { RenderTransformOrigin = new Point(.5, 0) };
        var translation = UiMotion.EnsureTranslate(element); translation.Y = 14;
        var scale = UiMotion.EnsureScale(element); scale.ScaleX = .96;
        var hover = UiMotion.ChannelTranslate(element, MotionChannel.Interaction);
        var group = UiMotion.EnsureScaleTranslateGroup(element);
        Assert.Same(translation, group.Translate); Assert.Same(scale, group.Scale);
        Assert.Same(hover, UiMotion.ChannelTranslate(element, MotionChannel.Interaction));
        Assert.Equal(14, translation.Y); Assert.Equal(.96, scale.ScaleX);
        Assert.Equal(new Point(.5, 0), element.RenderTransformOrigin);
    });

    [Fact]
    public void Coordinated_native_tracks_hold_all_start_values_and_settle_once_with_bindings() => Sta(() =>
    {
        using var h = new Harness();
        var source = new Border { Opacity = .3 };
        System.Windows.Data.BindingOperations.SetBinding(h.Content, UIElement.OpacityProperty,
            new System.Windows.Data.Binding(nameof(UIElement.Opacity)) { Source = source });
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(h.Content);
        scale.ScaleX = scale.ScaleY = .90; move.Y = 22;
        var calls = 0;
        SurfaceMotion.AnimateTracks(h.Content, MotionChannel.Showcase,
            [new(h.Content, UIElement.OpacityProperty, 1), new(move, TranslateTransform.YProperty, 0, true),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.025, .997)],
            420, UiMotion.OutCubic, () => calls++, cache: true, startDelayMs: 5000);
        h.Pump(30);
        Assert.InRange(h.Content.Opacity, .299, .301); Assert.Equal(.90, scale.ScaleX); Assert.Equal(22, move.Y);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(1, calls); Assert.Equal(1, h.Content.Opacity); Assert.Equal(1, scale.ScaleX); Assert.Equal(0, move.Y);
        Assert.True(System.Windows.Data.BindingOperations.IsDataBound(h.Content, UIElement.OpacityProperty));
        Assert.Null(h.Content.CacheMode); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
        Assert.False(NativeSurfaceMotion.IsRunning(h.Content, MotionChannel.Showcase));
    });

    [Fact]
    public void Canceling_native_interaction_does_not_cancel_native_navigation() => Sta(() =>
    {
        using var h = new Harness(); var calls = 0;
        var navigation = UiMotion.ChannelTranslate(h.Content, MotionChannel.Navigation);
        var hover = UiMotion.ChannelTranslate(h.Content, MotionChannel.Interaction);
        navigation.Y = 14;
        SurfaceMotion.AnimateTracks(h.Content, MotionChannel.Navigation,
            [new(navigation, TranslateTransform.YProperty, 0, true)], 1000, UiMotion.OutCubic, () => calls++);
        SurfaceMotion.AnimateTracks(h.Content, MotionChannel.Interaction,
            [new(hover, TranslateTransform.YProperty, -5, true)], 1000, UiMotion.OutCubic);
        UiMotion.Stop(UiMotion.Channel(h.Content, MotionChannel.Interaction));
        Assert.True(NativeSurfaceMotion.IsRunning(h.Content, MotionChannel.Navigation)); Assert.Equal(0, calls);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(1, calls); Assert.Equal(0, navigation.Y);
    });

    [Fact]
    public void Decoration_budget_changes_preserve_active_operations_and_core_durations() => Sta(() =>
    {
        using var h = new Harness(); var calls = 0;
        SurfaceMotion.Animate(h.Content, .5, 1000, UiMotion.OutCubic, false, () => calls++);
        typeof(MotionPolicy).GetField("_notifiedQuality", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, MotionPolicy.Quality == MotionQuality.Expressive ? MotionQuality.Lightweight : MotionQuality.Expressive);
        typeof(MotionPolicy).GetMethod("NotifyQualityChanged", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal(0, calls); Assert.True(NativeSurfaceMotion.IsRunning(h.Content, MotionChannel.Presence));
        Assert.Equal(420, MotionPolicy.FadeDuration(420)); Assert.True(MotionPolicy.Celebration);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(1, calls);
    });

    [Fact]
    public void Native_flip_commits_the_target_face_on_live_off_and_cancels_on_reuse() => Sta(() =>
    {
        using var h = new Harness();
        var flip = new Scriblucent.Controls.FlashcardFlipHost { Width = 200, Height = 140,
            FrontContent = new Border { Child = new TextBlock { Text = "Question" } },
            BackContent = new Border { Child = new TextBlock { Text = "Answer" } } };
        h.Window.Content = flip; h.Window.UpdateLayout(); h.Pump(25);
        var calls = 0; flip.AnimateToggle(1000, () => calls++);
        Assert.True(flip.IsAnimating);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.True(flip.IsFlipped); Assert.False(flip.IsAnimating); Assert.Equal(1, calls);
        var shade = (Border)flip.FindName("FlipShade"); Assert.Equal(0, shade.Opacity);
        var front = (FrameworkElement)flip.FindName("FrontHost"); var back = (FrameworkElement)flip.FindName("BackFace");
        Assert.Equal(Visibility.Hidden, front.Visibility); Assert.Equal(Visibility.Visible, back.Visibility);
        MotionPolicy.Configure(MotionPreference.Full, false);
        flip.AnimateToggle(1000, () => calls++); flip.PrepareForReuse();
        Assert.True(flip.IsFlipped); Assert.False(flip.IsAnimating); Assert.Equal(1, calls);
        Assert.Equal(Visibility.Hidden, front.Visibility); Assert.Equal(Visibility.Visible, back.Visibility);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Closing_a_window_during_wheel_glide_releases_the_shared_render_observer() => Sta(() =>
    {
        using var h = new Harness();
        var viewer = new ScrollViewer { Height = 100, Content = new Border { Height = 1600 } };
        h.Window.Content = viewer; h.Window.UpdateLayout(); h.Pump(25);
        Scriblucent.Behaviors.SmoothScrollBehavior.SetIsEnabled(viewer, true);
        try
        {
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -240)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
            Assert.True(RenderTransitionClock.IsAnyRunning);
            h.Window.Close();
            Assert.False(RenderTransitionClock.IsAnyRunning);
        }
        finally { Scriblucent.Behaviors.SmoothScrollBehavior.SetIsEnabled(viewer, false); }
    });

    [Fact]
    public void Sparse_render_callbacks_never_strip_expression() => Sta(() =>
    {
        var quality = MotionPolicy.Quality;
        for (var i = 0; i < 128; i++) MotionPolicy.Sample(TimeSpan.FromMilliseconds(200 * i));
        Assert.Equal(quality, MotionPolicy.Quality); Assert.Equal(420, MotionPolicy.FadeDuration(420));
    });

    [Fact]
    public void Live_off_clears_an_active_native_hover_after_its_clock_settles() => Sta(() =>
    {
        using var h = new Harness();
        var hover = UiMotion.ChannelTranslate(h.Content, MotionChannel.Interaction);
        SurfaceMotion.AnimateTracks(h.Content, MotionChannel.Interaction,
            [new(hover, TranslateTransform.YProperty, -5, true)], 1000, UiMotion.OutCubic);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(0, hover.Y); Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Shared_sidebar_plate_animates_the_first_visit_to_another_destination() => Sta(() =>
    {
        using var h = new Harness();
        var group = new Grid();
        NameScope.SetNameScope(group, new NameScope());
        var plate = new Border { Visibility = Visibility.Collapsed };
        group.RegisterName("SidebarSelectionPlate", plate);
        group.Children.Add(new Canvas { Children = { plate } });
        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var first = new Button { Height = 40 };
        var second = new Button { Height = 40 };
        buttons.Children.Add(first); buttons.Children.Add(second); group.Children.Add(buttons);
        SidebarNavMotionBehavior.SetIsGroupHost(group, true);
        SidebarNavMotionBehavior.SetIsActive(first, true);
        h.Window.Content = group; h.Pump(50);
        var move = UiMotion.EnsureTranslate(plate);
        Assert.Equal(0, move.Y);
        SidebarNavMotionBehavior.SetIsActive(first, false);
        SidebarNavMotionBehavior.SetIsActive(second, true);
        Assert.True(NativeSurfaceMotion.IsRunning(plate, MotionChannel.Navigation));
        h.Pump(350);
        Assert.Equal(40, move.Y, 3);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Result_value_updates_immediately_without_replaying_the_graphic_arrival() => Sta(() =>
    {
        using var h = new Harness();
        var emblem = new Scriblucent.Controls.StudyResultEmblem { Ratio = .9 };
        h.Window.Content = emblem; h.Pump(50);
        emblem.Play();
        emblem.Ratio = .5;
        var number = (TextBlock)emblem.Children[2];
        Assert.Equal(.5.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), number.Text);
        Assert.True(NativeSurfaceMotion.IsRunning(emblem, MotionChannel.Showcase));
        emblem.IsCompletion = true;
        Assert.Equal(Visibility.Collapsed, number.Visibility);
        Assert.Equal(Visibility.Visible, emblem.Children[3].Visibility);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });
}
