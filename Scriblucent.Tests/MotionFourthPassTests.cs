using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionFourthPassTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 6000) Pump(16);
        Assert.True(condition(), "The bounded asynchronous operation did not settle.");
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            var systemField = typeof(MotionPolicy).GetField("_systemOff", BindingFlags.Static | BindingFlags.NonPublic)!;
            var materialField = typeof(MotionPolicy).GetField("_transparencyEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
            var system = (bool)systemField.GetValue(null)!; var transparency = materialField.GetValue(null);
            try { materialField.SetValue(null, true); MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception error) { failure = error; }
            finally { MotionPolicy.Configure(preference, system); materialField.SetValue(null, transparency); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Motion scenario timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static Window Window(FrameworkElement content, string theme = "Light")
    {
        var window = new Window { Content = content, Width = 650, Height = 450, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = false };
        foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons", "Controls/Dashboard" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml") });
        window.Show(); window.UpdateLayout(); Pump(35); return window;
    }
    private static IReadOnlyList<(NativeMotionTrack Track, AnimationClock Clock)> Tracks(UIElement element, MotionChannel channel)
    {
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [element, null];
        Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
        var state = ((IDictionary)args[1]!)[channel]!;
        return (IReadOnlyList<(NativeMotionTrack, AnimationClock)>)state.GetType().GetField("Tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
    }
    private static void Seek(UIElement element, MotionChannel channel, int ms)
    {
        foreach (var (_, clock) in Tracks(element, channel))
        { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime); }
    }

    [Fact]
    public void Arrival_has_a_gentle_launch_and_completion_waits_for_the_longest_track() => Sta(() =>
    {
        var face = new Border { Background = Brushes.SlateBlue, Opacity = 0 };
        using var cleanup = new Cleanup(Window(face));
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(face); move.Y = 18; scale.ScaleX = .96;
        var calls = 0;
        SurfaceMotion.AnimateTracks(face, MotionChannel.Presence,
            [new(face, UIElement.OpacityProperty, 1, Profile: new(UiMotion.OutSine, 120)),
             new(move, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.006, 1, MotionProfiles.Arrival)],
            320, UiMotion.OutCubic, () => calls++);
        Seek(face, MotionChannel.Presence, 64);
        Assert.InRange(move.Y, 16, 17); Assert.InRange(scale.ScaleX, .965, .975);
        Seek(face, MotionChannel.Presence, 200); Pump(20);
        Assert.Equal(1, face.Opacity); Assert.Equal(0, calls);
        Assert.True(NativeSurfaceMotion.IsRunning(face, MotionChannel.Presence));
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(1, calls); Assert.Equal(0, move.Y); Assert.Equal(1, scale.ScaleX);
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Retarget_uses_the_visible_pose_and_drops_the_superseded_completion() => Sta(() =>
    {
        var face = new Border(); using var cleanup = new Cleanup(Window(face));
        var move = UiMotion.EnsureTranslate(face); move.Y = 18;
        int stale = 0, current = 0;
        SurfaceMotion.AnimateTracks(face, MotionChannel.Presence,
            [new(move, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival)], 320, UiMotion.InOutSine, () => stale++);
        Seek(face, MotionChannel.Presence, 110); var visible = move.Y;
        SurfaceMotion.AnimateTracks(face, MotionChannel.Presence,
            [new(move, TranslateTransform.YProperty, 6, true)], 140, UiMotion.InQuad, () => current++);
        Seek(face, MotionChannel.Presence, 0); Assert.Equal(visible, move.Y, 6);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(0, stale); Assert.Equal(1, current); Assert.Equal(6, move.Y);
    });

    [Fact]
    public void Backdrop_preparation_starts_foreground_after_capture_and_before_publication() => Sta(() =>
    {
        var face = new Border { Background = Brushes.CornflowerBlue };
        using var cleanup = new Cleanup(Window(face));
        var prepared = 0; var published = 0; BitmapSource? image = null;
        using var request = OverlayBackdropCapture.RequestPrepared(cleanup.Window, value =>
        { Assert.Equal(1, prepared); image = value; published++; }, () =>
        {
            Assert.Equal(0, published); prepared++;
            SurfaceMotion.Animate(face, 1, 320, UiMotion.OutSine, false);
        });
        Until(() => published == 1);
        Assert.Equal(1, prepared);
        if (!SystemParameters.HighContrast) Assert.NotNull(image);
    });

    [Fact]
    public void Backdrop_restart_before_preparation_does_not_wait_for_the_entrance_it_must_start() => Sta(() =>
    {
        using var cleanup = new Cleanup(Window(new Border { Background = Brushes.SlateBlue }));
        var entrance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = 0; var published = 0;
        using var request = OverlayBackdropCapture.RequestPrepared(cleanup.Window, _ => published++, () =>
        { prepared++; entrance.SetResult(); }, () => entrance.Task);
        // Resize/DPI/theme notifications all use this same restart while the first capture is queued.
        request.GetType().GetMethod("Restart", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(request, null);
        Until(() => prepared == 1 && published == 1);
        Assert.True(entrance.Task.IsCompleted); Assert.Equal(1, prepared);
    });

    [Fact]
    public void Closing_before_capture_preparation_cannot_start_or_publish_the_abandoned_entrance() => Sta(() =>
    {
        var root = new Grid { Background = Brushes.LightSlateGray };
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        var dim = new Border { Background = Brushes.Black };
        var image = new Image(); var face = new Border { Width = 200, Height = 100, Background = Brushes.White };
        overlay.Children.Add(image); overlay.Children.Add(dim); overlay.Children.Add(face);
        Panel.SetZIndex(overlay, 300); root.Children.Add(overlay);
        using var cleanup = new Cleanup(Window(root));
        var animator = OverlayPanelAnimator.Bind(overlay, dim, face, image, overlay);
        int started = 0, shown = 0, closed = 0;
        animator.Show(() => shown++, backdropOwner: cleanup.Window, onEntranceStarted: () => started++);
        animator.Hide(() => closed++); Pump(120);
        Assert.Equal(0, started); Assert.Equal(0, shown); Assert.Equal(1, closed);
        Assert.Equal(Visibility.Collapsed, overlay.Visibility); Assert.Null(image.Source);
        Assert.False(RenderTransitionClock.IsAnyRunning); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Live_off_finishes_a_preparing_overlay_once_without_waiting_for_material() => Sta(() =>
    {
        var root = new Grid(); var overlay = new Grid { Visibility = Visibility.Collapsed };
        var dim = new Border(); var face = new Border(); var image = new Image();
        overlay.Children.Add(image); overlay.Children.Add(dim); overlay.Children.Add(face);
        Panel.SetZIndex(overlay, 300); root.Children.Add(overlay);
        using var cleanup = new Cleanup(Window(root));
        var animator = OverlayPanelAnimator.Bind(overlay, dim, face, image, overlay);
        var shown = 0; animator.Show(() => shown++, backdropOwner: cleanup.Window);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(1, shown); Assert.Equal(1, face.Opacity);
        Pump(100); Assert.Equal(1, shown);
        animator.Hide(); Until(() => !OverlayBackdropCapture.IsProcessing);
        Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Reversing_a_dialog_exit_reuses_the_snapshot_and_starts_from_its_visible_pose() => Sta(() =>
    {
        var root = new Grid { Background = Brushes.SlateGray };
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        var dim = new Border(); var face = new Border { Width = 240, Height = 120 }; var image = new Image();
        overlay.Children.Add(image); overlay.Children.Add(dim); overlay.Children.Add(face);
        Panel.SetZIndex(overlay, 300); root.Children.Add(overlay);
        using var cleanup = new Cleanup(Window(root));
        var animator = OverlayPanelAnimator.Bind(overlay, dim, face, image, overlay);
        var starts = 0; var shown = 0;
        animator.Show(() => shown++, backdropOwner: cleanup.Window, onEntranceStarted: () => starts++);
        Until(() => shown == 1 && !OverlayBackdropCapture.IsProcessing);
        var leaseField = typeof(OverlayPanelAnimator).GetField("_backdropRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var lease = leaseField.GetValue(animator); var snapshot = image.Source;
        Assert.NotNull(lease);
        animator.Hide(); Seek(face, MotionChannel.Presence, 40);
        var visible = face.Opacity;
        animator.Show(() => shown++, backdropOwner: cleanup.Window, onEntranceStarted: () => starts++);
        overlay.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        Assert.Equal(2, starts); Assert.Same(lease, leaseField.GetValue(animator)); Assert.Same(snapshot, image.Source);
        Seek(face, MotionChannel.Presence, 0); Assert.Equal(visible, face.Opacity, 6);
        MotionPolicy.Configure(MotionPreference.Off, false); Assert.Equal(2, shown);
        animator.Hide(); Assert.Null(leaseField.GetValue(animator)); Assert.Null(image.Source);
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Shared_button_face_handles_press_and_live_reduction_without_duplicate_tracks(string theme) => Sta(() =>
    {
        var button = new Button { Content = "Save", Width = 130, Height = 40 };
        using var cleanup = new Cleanup(Window(button, theme));
        button.Style = (Style)button.FindResource("DialogPrimaryButtonStyle"); button.ApplyTemplate(); Pump(35);
        var face = Assert.IsType<InteractionSurface>(button.Template.FindName("ButtonBorder", button));
        face.AnimateState(true, false); Seek(face, MotionChannel.Interaction, 90);
        CommandLightBehavior.PreviewPointer(button, new Point(65, 4), true); Pump(230);
        var light = Assert.IsType<CommandSurfaceLight>(CommandLightBehavior.LightFor(face));
        Assert.Same(light, CommandLightBehavior.LightFor(button));
        Assert.InRange(light.Strength, .65, 1);
        var source = PresentationSource.FromVisual(button)!;
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        var tracks = Tracks(face, MotionChannel.Interaction);
        Assert.Equal(tracks.Count, tracks.Select(track => (track.Track.Target, track.Track.Property)).Distinct().Count());
        Seek(face, MotionChannel.Interaction, 80);
        Assert.Equal(1, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.Equal(0, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y);
        Assert.Equal(.22, light.Strength);
        Assert.False(RenderTransitionClock.IsAnyRunning);
        button.IsEnabled = false; Pump(230);
        Assert.Equal(0, light.Strength);
        Assert.Equal(1, UiMotion.EnsureScale(face).ScaleX);
        if (SurfaceFill.Get(face, SurfaceFill.Role.Pressed, false) is { } pressed) Assert.Equal(0, pressed.Opacity);
    });

    [Fact]
    public void Radio_group_keyboard_selection_and_reload_keep_feedback_without_replaying_model_changes() => Sta(() =>
    {
        var panel = new StackPanel(); var first = new RadioButton { Content = "First", IsChecked = true };
        var second = new RadioButton { Content = "Second" }; panel.Children.Add(first); panel.Children.Add(second);
        using var cleanup = new Cleanup(Window(panel));
        foreach (var radio in new[] { first, second })
        { radio.Style = (Style)radio.FindResource("SortOptionRadioButtonStyle"); radio.ApplyTemplate(); }
        Pump(40);
        var source = PresentationSource.FromVisual(first)!;
        first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        second.IsChecked = true;
        var glyph = (Ellipse)second.Template.FindName("RadioGlyph", second);
        Assert.True(NativeSurfaceMotion.IsRunning(glyph, MotionChannel.Presence));
        panel.Children.Remove(second); Pump(20);
        Assert.False(NativeSurfaceMotion.IsRunning(glyph, MotionChannel.Presence));
        panel.Children.Add(second); Pump(35); second.IsChecked = false; second.IsChecked = true;
        Assert.Equal(1, glyph.Opacity); Assert.False(NativeSurfaceMotion.IsRunning(glyph, MotionChannel.Presence));
        second.IsChecked = false;
        second.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        second.IsChecked = true; Assert.True(NativeSurfaceMotion.IsRunning(glyph, MotionChannel.Presence));
    });

    [Fact]
    public void Contextual_tools_are_interactive_during_arrival_and_disabled_when_dismissing() => Sta(() =>
    {
        var content = new Border { Width = 130, Height = 35 }; var slot = new Border { Child = content };
        using var cleanup = new Cleanup(Window(new StackPanel { Children = { slot } }));
        EditorToolbarReveal.SetVisible(slot, false, false); EditorToolbarReveal.SetVisible(slot, true);
        Assert.True(slot.IsHitTestVisible); Assert.True(slot.IsEnabled);
        EditorToolbarReveal.SetVisible(slot, false);
        Assert.False(slot.IsHitTestVisible); Assert.False(slot.IsEnabled);
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(Visibility.Collapsed, slot.Visibility);
    });

    [Fact]
    public void Save_label_and_glyph_share_presence_motion_and_retry_cancels_old_feedback() => Sta(() =>
    {
        var status = new EditorTitleStatus { Visibility = Visibility.Collapsed };
        using var cleanup = new Cleanup(Window(status));
        status.ShowSaved(() => status.SetEdited(false), holdMs: 80);
        Pump(25);
        Assert.Equal(EditorSaveFeedback.Saved, status.Feedback);
        Assert.True(NativeSurfaceMotion.IsRunning(status, MotionChannel.Presence));
        Assert.False(NativeSurfaceMotion.IsRunning(status.Glyph, MotionChannel.Interaction));
        Assert.Equal(1, status.Glyph.Opacity);
        Assert.Equal(1, status.Label.Opacity);
        status.ShowFailure();
        Pump(400);
        Assert.Equal(EditorSaveFeedback.Failed, status.Feedback);
        Assert.Equal(Scriblucent.Localization.LocalizationService.GetString("NotSaved"), status.Text);
        Assert.Equal(Visibility.Visible, status.Visibility);
        status.SetEdited(true);
        Assert.Equal(EditorSaveFeedback.Failed, status.Feedback);
        status.ShowSaved(() => status.SetEdited(false), holdMs: 80);
        Pump(400);
        // This harness has an invisible HWND; commit its native exit through the policy transition.
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(EditorSaveFeedback.None, status.Feedback);
        Assert.Equal(Visibility.Collapsed, status.Visibility);
    });

    [Fact]
    public void Selection_fill_is_beneath_the_content_and_recycling_clears_it() => Sta(() =>
    {
        var label = new TextBlock { Text = "A selected card" }; var card = new Border { Padding = new Thickness(12), Child = label };
        using var cleanup = new Cleanup(Window(card)); var bounds = card.RenderSize;
        SurfaceFill.Select(card, true, false); cleanup.Window.UpdateLayout();
        var layer = SurfaceFill.Get(card, SurfaceFill.Role.Selection, false)!;
        Assert.Equal(1, layer.Opacity); Assert.Equal(bounds, card.RenderSize);
        Assert.True(Panel.GetZIndex(layer) < Panel.GetZIndex(label));
        SurfaceFill.ClearSelection(card); Assert.Null(SurfaceFill.Get(card, SurfaceFill.Role.Selection, false));
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void First_selection_fades_on_the_loaded_card_and_cancels_without_affecting_hover() => Sta(() =>
    {
        var card = new Border { Child = new TextBlock { Text = "Select me" } };
        using var cleanup = new Cleanup(Window(card));
        SurfaceFill.Select(card, true, true);
        Assert.True(NativeSurfaceMotion.IsRunning(card, MotionChannel.Selection));
        var layer = SurfaceFill.Get(card, SurfaceFill.Role.Selection, false)!;
        Seek(card, MotionChannel.Selection, 70);
        Assert.InRange(layer.Opacity, .6, .8);
        var hover = new TranslateTransform();
        SurfaceMotion.AnimateTracks(card, MotionChannel.Interaction,
            [new(hover, TranslateTransform.YProperty, -5, true)], 160, UiMotion.OutCubic);
        SurfaceFill.ClearSelection(card);
        Assert.False(NativeSurfaceMotion.IsRunning(card, MotionChannel.Selection));
        Assert.True(NativeSurfaceMotion.IsRunning(card, MotionChannel.Interaction));
    });

    [Fact]
    public void Diagnostics_do_not_count_a_gap_across_a_native_motion_idle_interval() => Sta(() =>
    {
        var face = new Border(); using var cleanup = new Cleanup(Window(face));
        MotionDiagnostics.Start();
        try
        {
            SurfaceMotion.Animate(face, .5, 180, UiMotion.OutSine, false);
            MotionDiagnostics.EndFrame(MotionDiagnostics.BeginFrame());
            SurfaceMotion.Stop(face);
            SurfaceMotion.Animate(face, 1, 180, UiMotion.OutSine, false);
            MotionDiagnostics.EndFrame(MotionDiagnostics.BeginFrame());
            var samples = MotionDiagnostics.Stop();
            Assert.Equal(2, samples.Count);
            Assert.All(samples, sample => Assert.Equal(0, sample.CallbackGapMs));
        }
        finally { MotionDiagnostics.Stop(); }
    });

    private sealed class Cleanup(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose() { Window.Close(); Pump(35); }
    }
}
