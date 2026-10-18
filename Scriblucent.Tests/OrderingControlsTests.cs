using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class OrderingControlsTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { MotionPolicy.Configure(MotionPreference.Off, false); action(); }
            catch (Exception caught) { error = caught; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke(); Pump();
    }

    [Fact]
    public void Section_arrows_are_direct_and_follow_live_boundaries() => Sta(() =>
    {
        var control = new PositionButtons { UpLabel = "Earlier", DownLabel = "Later", CanMoveDown = true };
        int up = 0, down = 0;
        control.MoveUp += (sender, _) => { Assert.Same(control, sender); up++; };
        control.MoveDown += (sender, _) => { Assert.Same(control, sender); down++; };
        var window = new Window { Content = control, Width = 300, Height = 200, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            Assert.False(control.UpButton.IsEnabled); Assert.True(control.DownButton.IsEnabled);
            Click(control.DownButton); Assert.Equal(1, down); Assert.Equal(0, up);
            control.CanMoveUp = true; control.CanMoveDown = false; Pump();
            Assert.True(control.UpButton.IsEnabled); Assert.False(control.DownButton.IsEnabled);
            Click(control.UpButton); Assert.Equal(1, up);
            control.UpLabel = "Translated earlier"; Pump(); Assert.Equal("Translated earlier", control.UpButton.ToolTip);
            Assert.Null(control.UpButton.ContextMenu); Assert.Null(control.DownButton.ContextMenu);
            control.CanMoveUp = false; Pump(); Assert.False(control.UpButton.IsEnabled);
        }
        finally { window.Close(); Pump(); }
    });

    [Fact]
    public void Grip_click_step_commands_and_menu_actions_use_the_same_order_and_boundaries() => Sta(() =>
    {
        object a = new(), b = new(), c = new(); var rows = new ObservableCollection<object> { a, b, c };
        var list = new ItemsControl { ItemsSource = rows, ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ItemReorderControls)) } };
        ItemReorderBehavior.SetKind(list, ItemReorderKind.QuizQuestions);
        var window = new Window { Content = list, Width = 300, Height = 200, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var property = (DependencyProperty)typeof(ItemReorderBehavior).GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var state = list.GetValue(property);
            var adapter = new ItemReorderAdapter(rows, "test",
                item => [item], (moving, anchor) => !moving.Contains(anchor), (moving, anchor, after) =>
                {
                    var ordered = ItemOrdering.Move(rows, moving.ToHashSet(), anchor, after);
                    int from = rows.IndexOf(moving[0]), to = ordered.IndexOf(moving[0]);
                    if (from == to) return false; rows.Move(from, to); return true;
                });
            state.GetType().GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, adapter);
            ItemReorderGrip Grip(object item) => Children<ItemReorderGrip>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(item)).Single();
            var grip = Grip(c); Click(grip);
            Assert.True(grip.ContextMenu.IsOpen); Assert.Equal(2, grip.ContextMenu.Items.Count);
            Assert.True(((MenuItem)grip.ContextMenu.Items[0]).IsEnabled); Assert.False(((MenuItem)grip.ContextMenu.Items[1]).IsEnabled);
            ((MenuItem)grip.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); grip.ContextMenu.IsOpen = false; Pump();
            Assert.Equal(new[] { a, c, b }, rows);
            Assert.True(ItemReorderBehavior.MoveFromHandle(Grip(c), true)); Pump();
            Assert.Equal(new[] { a, b, c }, rows);
            var direct = Children<ItemReorderControls>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(c)).Single();
            Assert.True(direct.EarlierButton.IsEnabled); Assert.False(direct.LaterButton.IsEnabled);
            Click(direct.EarlierButton); Assert.Equal(new[] { a, c, b }, rows);
            direct = Children<ItemReorderControls>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(c)).Single();
            Click(direct.LaterButton); Assert.Equal(new[] { a, b, c }, rows);
            Assert.False(ItemReorderBehavior.MoveFromHandle(Grip(a), false));
            var first = Grip(a); Click(first);
            Assert.False(((MenuItem)first.ContextMenu.Items[0]).IsEnabled); Assert.True(((MenuItem)first.ContextMenu.Items[1]).IsEnabled);
            first.ContextMenu.IsOpen = false;
        }
        finally
        {
            foreach (var grip in Children<ItemReorderGrip>(list)) grip.ContextMenu.IsOpen = false;
            window.Close(); Pump();
        }
    });
}
