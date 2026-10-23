using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class BlockedWindowBackdropTests
{
    [Fact]
    public void Both_backdrops_are_ready_before_any_window_starts_fading() => Sta(() =>
    {
        if (!MotionPolicy.BackdropBlur) return;
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        bool capturedDuringFade = false, observedEntrance = false;
        void CaptureStarted() => capturedDuringFade |= dialog.IsEntering
            || Elements<UIElement>(main.Root).Any(element => NativeSurfaceMotion.IsRunning(element, MotionChannel.Presence));
        main.Root.CaptureStarted += CaptureStarted;
        editor.Root.CaptureStarted += CaptureStarted;
        EventHandler rendering = (_, _) =>
        {
            if (!dialog.IsEntering) return;
            observedEntrance = true;
            Assert.NotNull(dialog.BackdropImage.Source);
            Assert.Contains(Images(main.Root), image => image.Source is not null
                && (image.Opacity == 1 || NativeSurfaceMotion.IsRunning(image, MotionChannel.Presence)));
        };
        CompositionTarget.Rendering += rendering;
        try { dialog.ShowModal(() => WaitUntil(() => dialog.IsShown)); }
        finally { CompositionTarget.Rendering -= rendering; }
        Assert.True(observedEntrance);
        Assert.False(capturedDuringFade, "Snapshot rasterization interrupted a window's backdrop fade.");
    });

    [Fact]
    public void Switching_motion_off_during_preparation_completes_the_entrance_once() => Sta(() =>
    {
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        main.Root.CaptureStarted += () => MotionPolicy.Configure(MotionPreference.Off, false);
        dialog.ShowModal(() =>
        {
            WaitUntil(() => dialog.IsShown);
            Pump(100);
            Assert.Equal(1, dialog.ShownCount);
        });
    });

    [Fact]
    public void Resize_between_snapshot_preparation_and_publication_does_not_stall_the_entrance() => Sta(() =>
    {
        if (!MotionPolicy.BackdropBlur) return;
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        var property = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        EventHandler? resized = null;
        Image? mainImage = null;
        main.Root.CaptureStarted += () =>
        {
            if (mainImage is not null) return;
            mainImage = Images(main.Root).First();
            resized = (_, _) =>
            {
                if (mainImage.Source is null) return;
                property.RemoveValueChanged(mainImage, resized!);
                main.Window.Width += 12;
                editor.Window.Width += 12;
            };
            property.AddValueChanged(mainImage, resized);
        };
        try { dialog.ShowModal(() => WaitUntil(() => dialog.IsShown)); }
        finally { if (mainImage is not null && resized is not null) property.RemoveValueChanged(mainImage, resized); }
    });

    [Fact]
    public void Main_window_capture_finishes_before_the_dialog_starts_moving() => Sta(() =>
    {
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        int captures = 0;
        bool capturedDuringEntrance = false;
        main.Root.CaptureStarted += () =>
        {
            captures++;
            capturedDuringEntrance |= dialog.IsEntering;
        };
        dialog.ShowModal(() => WaitForBackdrop(main));
        Assert.True(captures > 0);
        Assert.False(capturedDuringEntrance, "The dashboard was rasterized during the dialog entrance.");
    });

    [Fact]
    public void Main_window_blur_is_available_when_the_editor_blur_arrives() => Sta(() =>
    {
        if (!MotionPolicy.BackdropBlur) return;
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        bool published = false, mainReady = false;
        var property = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        EventHandler changed = (_, _) =>
        {
            if (dialog.BackdropImage.Source is null) return;
            published = true;
            mainReady = Images(main.Root).Any(image => image.Source is not null);
        };
        property.AddValueChanged(dialog.BackdropImage, changed);
        try
        {
            dialog.ShowModal(() => WaitUntil(() => published));
            Assert.True(mainReady, "The editor blur arrived before the dashboard blur.");
        }
        finally { property.RemoveValueChanged(dialog.BackdropImage, changed); }
    });

    [Theory]
    [InlineData(MotionPreference.Automatic)]
    [InlineData(MotionPreference.Reduced)]
    [InlineData(MotionPreference.Off)]
    public void Secondary_modal_blurs_the_disabled_main_window_and_releases_it_on_close(MotionPreference preference) => Sta(() =>
    {
        MotionPolicy.Configure(preference, false);
        var budget = MotionCacheBudget.ReservedPixels;
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        dialog.ShowModal(() =>
        {
            Assert.False(IsEnabled(main.Window));
            Assert.False(IsEnabled(editor.Window));
            WaitForBackdrop(main);
            Assert.Single(main.Root.Children.OfType<Grid>());
            Assert.Single(editor.Root.Children); // Its existing dialog scrim covers it.
        });
        WaitUntil(() => main.Root.Children.Count == 1 && MotionCacheBudget.ReservedPixels == budget);
        Assert.True(IsEnabled(main.Window));
        Assert.True(IsEnabled(editor.Window));
        Assert.Equal(Visibility.Visible, main.Body.Visibility);
    });

    [Fact]
    public void Nested_dialog_keeps_main_blurred_until_the_last_modal_closes() => Sta(() =>
    {
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var outer = new Dialog(editor.Window);
        outer.ShowModal(() =>
        {
            WaitForBackdrop(main);
            var backdrop = main.Root.Children.OfType<Grid>().Single();
            using var inner = new Dialog(outer.Window);
            inner.ShowModal(() =>
            {
                WaitForBackdrop(main);
                Assert.Same(backdrop, main.Root.Children.OfType<Grid>().Single());
                Assert.Single(editor.Root.Children);
            });
            Assert.False(IsEnabled(main.Window));
            Assert.Same(backdrop, main.Root.Children.OfType<Grid>().Single());
        });
        WaitUntil(() => main.Root.Children.Count == 1);
        Assert.True(IsEnabled(main.Window));
    });

    [Fact]
    public void A_modeless_window_does_not_blur_an_interactive_main_window() => Sta(() =>
    {
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        dialog.Window.Show(); Pump(100);
        Assert.True(IsEnabled(main.Window));
        Assert.Single(main.Root.Children);
        dialog.Window.Close(); Pump(50);
    });

    [Fact]
    public void Immediate_modal_close_cancels_pending_backdrops_and_restores_content() => Sta(() =>
    {
        var budget = MotionCacheBudget.ReservedPixels;
        using var main = new Host(); main.Window.Show();
        using var editor = new Host(main.Window); editor.Window.Show();
        using var dialog = new Dialog(editor.Window);
        dialog.Window.Loaded += (_, _) => dialog.Window.Close();
        dialog.Window.ShowDialog();
        WaitUntil(() => main.Root.Children.Count == 1 && MotionCacheBudget.ReservedPixels == budget);
        Assert.Equal(Visibility.Visible, main.Body.Visibility);
        Assert.True(IsEnabled(main.Window));
    });

    private static void WaitForBackdrop(Host host)
    {
        WaitUntil(() => host.Root.Children.Count == 2 && (!MotionPolicy.BackdropBlur
            || Images(host.Root).Any(image => image.Source is not null)));
        if (MotionPolicy.BackdropBlur)
            Assert.Contains(Images(host.Root), image => image.Source is not null && image.Effect is null);
    }

    private static IEnumerable<Image> Images(DependencyObject root)
        => Elements<Image>(root);

    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T element) yield return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Elements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class Host : IDisposable
    {
        internal CaptureAwareGrid Root { get; } = new() { Background = Brushes.White };
        internal Border Body { get; } = new() { Background = Brushes.CornflowerBlue, Margin = new Thickness(30) };
        internal Window Window { get; }
        internal Host(Window? owner = null)
        {
            Root.Children.Add(Body);
            Root.Resources["OverlayScrim"] = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
            Window = new Window { Content = Root, Owner = owner, Width = 420, Height = 320,
                Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        }
        public void Dispose() { Window.Close(); Pump(30); }
    }

    private sealed class CaptureAwareGrid : Grid
    {
        internal event Action? CaptureStarted;
        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            if (visualAdded is FrameworkElement overlay && Panel.GetZIndex(overlay) >= 200)
                overlay.IsVisibleChanged += (_, _) =>
                {
                    // Capturing excludes the scrim itself, temporarily hiding it.
                    if (overlay.Visibility == Visibility.Hidden) CaptureStarted?.Invoke();
                };
        }
    }

    private sealed class Dialog : IDisposable
    {
        internal Window Window { get; }
        private readonly OverlayDialogAnimator _animator;
        private readonly Border _card;
        internal bool IsEntering => NativeSurfaceMotion.IsRunning(_card, MotionChannel.Presence);
        internal bool IsShown => _card.Opacity > .99 && !IsEntering;
        internal int ShownCount { get; private set; }
        internal Image BackdropImage { get; }
        internal Dialog(Window owner)
        {
            var root = new Grid(); var scrim = new Grid();
            var image = new Image(); var dim = new Border { Background = Brushes.Black };
            BackdropImage = image;
            var card = new Border { Width = 150, Height = 100, Background = Brushes.White };
            _card = card;
            scrim.Children.Add(image); scrim.Children.Add(dim);
            root.Children.Add(scrim); root.Children.Add(card);
            Window = new Window { Owner = owner, Content = root, Width = 320, Height = 240,
                Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            _animator = OverlayDialogAnimator.Bind(Window, root, dim, card, image, scrim);
            Window.Loaded += (_, _) => _animator.BeginOpen(() => ShownCount++);
        }

        internal void ShowModal(Action inspect)
        {
            Exception? failure = null;
            Window.Loaded += (_, _) => Window.Dispatcher.BeginInvoke(() =>
            {
                try { inspect(); }
                catch (Exception ex) { failure = ex; }
                finally { _animator.BeginClose(Window.Close); }
            }, DispatcherPriority.ApplicationIdle);
            Window.ShowDialog();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        public void Dispose() { Window.Close(); Pump(30); }
    }

    private static void WaitUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!ready() && DateTime.UtcNow < deadline) Pump(25);
        Assert.True(ready(), "Modal backdrop did not reach the expected state.");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception ex) { failure = ex; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Modal scenario timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static bool IsEnabled(Window window) => IsWindowEnabled(new WindowInteropHelper(window).Handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
