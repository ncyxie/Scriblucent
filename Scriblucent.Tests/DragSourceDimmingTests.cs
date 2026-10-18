using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DragSourceDimmingTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception caught) { error = caught; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static CollectionSelectionFrame Card(object item) => new()
    {
        DataContext = item, Width = 120, Height = 80,
        Template = new ControlTemplate(typeof(CollectionSelectionFrame)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
        Content = new Border { Background = Brushes.Blue }
    };
    private static Window Host(UIElement content) => new()
        { Content = content, Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };

    [Fact]
    public void Rebinding_back_to_the_dragged_item_restores_dimming_and_end_clears_it() => Sta(() =>
    {
        var item = new object(); var other = new object(); var card = Card(item);
        var window = Host(card);
        try
        {
            window.Show(); Pump();
            using (var drag = new DragSourceDimming(window, item, .18))
            {
                Assert.Equal(.18, card.Opacity);
                card.DataContext = other; Assert.Equal(1, card.Opacity);
                card.DataContext = item; Assert.Equal(.18, card.Opacity);
                Assert.True(DashboardDragGhost.IsActiveDragSource(card));
            }
            Assert.Equal(1, card.Opacity);
            Assert.False(DashboardDragGhost.IsActiveDragSource(card));
        }
        finally { window.Close(); Pump(); }
    });

    [Fact]
    public void Replacement_visual_is_dimmed_after_unload_without_graying_unrelated_cards() => Sta(() =>
    {
        var item = new object(); var original = Card(item); var other = Card(new object());
        var panel = new StackPanel(); panel.Children.Add(original); panel.Children.Add(other);
        var window = Host(panel);
        try
        {
            window.Show(); Pump();
            using (var drag = new DragSourceDimming(window, item, .22))
            {
                panel.Children.Remove(original); Pump();
                Assert.False(original.IsLoaded); Assert.Equal(1, original.Opacity);
                var replacement = Card(item); panel.Children.Insert(0, replacement); Pump();
                Assert.Equal(.22, replacement.Opacity); Assert.Equal(1, other.Opacity);
                // Reusing the original container for another item must stay neutral.
                original.DataContext = new object(); panel.Children.Add(original); Pump();
                Assert.Equal(1, original.Opacity);
                drag.Dispose();
                Assert.Equal(1, replacement.Opacity); Assert.Equal(1, other.Opacity);
            }
        }
        finally { window.Close(); Pump(); }
    });

    [Fact]
    public void Dimming_is_scoped_to_the_source_window_and_does_not_touch_cached_face_paint() => Sta(() =>
    {
        var item = new object(); var card = Card(item); var other = Card(item);
        var window = Host(card); var otherWindow = Host(other);
        try
        {
            window.Show(); otherWindow.Show(); Pump();
            using var drag = new DragSourceDimming(window, item, .18);
            Assert.Equal(.18, card.Opacity); Assert.Equal(1, other.Opacity);
            var face = (Border)card.Content;
            var snapshot = Scriblucent.Rendering.FrozenCardDrawing.Capture(face)!;
            using var bitmap = snapshot.Render(1, out _);
            int pixel = (int)Scriblucent.Rendering.FrozenCardDrawing.Fringe + 20;
            Assert.Equal((byte)255, bitmap.GetPixel(pixel, pixel).Alpha);
        }
        finally { window.Close(); otherWindow.Close(); Pump(); }
    });
}
