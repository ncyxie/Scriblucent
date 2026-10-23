using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionFifthPassTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            var system = typeof(MotionPolicy).GetField("_systemOff", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = (bool)system.GetValue(null)!;
            try { MotionPolicy.Configure(MotionPreference.Full, false); action(); }
            catch (Exception error) { failure = error; }
            finally { MotionPolicy.Configure(preference, previous); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Host : IDisposable
    {
        internal readonly Window Window;
        internal Host(FrameworkElement content, string theme = "Light")
        {
            Window = new Window { Content = content, Width = 720, Height = 580, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons", "Controls/Dashboard" })
                Window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml") });
            Window.Show(); Window.UpdateLayout(); Pump(40);
        }
        public void Dispose() { Window.Close(); Pump(35); }
    }
    private static void Seek(UIElement element, MotionChannel channel, int ms)
    {
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [element, null]; Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
        var state = ((IDictionary)args[1]!)[channel]!;
        var tracks = (IReadOnlyList<(NativeMotionTrack, AnimationClock)>)state.GetType().GetField("Tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        foreach (var (_, clock) in tracks) { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime); }
    }
    private static void Pointer(Button button, bool entered) => button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = entered ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
    private static void KeyInput(System.Windows.Controls.Primitives.ButtonBase button, Key key, RoutedEvent routed) => button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(button)!, Environment.TickCount, key) { RoutedEvent = routed });
    private static byte[] Raster(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }

    [Theory]
    [InlineData("Light", "DialogSecondaryButtonStyle", -1)]
    [InlineData("Dark", "DialogSecondaryButtonStyle", -1)]
    [InlineData("Light", "DialogPrimaryButtonStyle", -2)]
    [InlineData("Dark", "DialogPrimaryButtonStyle", -2)]
    [InlineData("Light", "ToolbarIconButtonStyle", 0)]
    [InlineData("Dark", "ToolbarIconButtonStyle", 0)]
    [InlineData("Light", "EditorPrimarySaveButtonStyle", -2)]
    [InlineData("Dark", "EditorPrimarySaveButtonStyle", -2)]
    public void Routed_pointer_feedback_is_visible_and_keeps_the_button_hit_root_still(string theme, string style, double lift) => Sta(() =>
    {
        var button = new Button { Content = "Action", Width = 140, Height = 42 };
        using var host = new Host(new Grid { Children = { button } }, theme);
        button.Style = (Style)button.FindResource(style); button.ApplyTemplate(); Pump(30);
        var face = (InteractionSurface)(button.Template.FindName("ButtonBorder", button) ?? button.Template.FindName("SaveButtonBorder", button));
        var rect = button.TransformToAncestor(host.Window).TransformBounds(new Rect(button.RenderSize)); var rest = Raster(face);
        Pointer(button, true); Seek(face, MotionChannel.Interaction, 180);
        Assert.True(UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y <= 0);
        Assert.False(rest.SequenceEqual(Raster(face)));
        Assert.Equal(lift, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y, 5);
        Assert.Equal(rect, button.TransformToAncestor(host.Window).TransformBounds(new Rect(button.RenderSize)));
        KeyInput(button, Key.Enter, Keyboard.PreviewKeyDownEvent); Seek(face, MotionChannel.Interaction, 80);
        Assert.Equal(1, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y, 5);
        Assert.Equal(.985, UiMotion.EnsureScale(face).ScaleX, 5);
        KeyInput(button, Key.Enter, Keyboard.PreviewKeyUpEvent); Pointer(button, false);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.Equal(0, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y);
        Assert.Equal(1, UiMotion.EnsureScale(face).ScaleX);
        button.IsEnabled = false; Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
    });

    [Fact]
    public void Native_space_enter_and_access_key_still_click_and_template_replacement_releases_motion() => Sta(() =>
    {
        var button = new Button { Content = "_Save", Width = 140, Height = 42 }; var clicks = 0; button.Click += (_, _) => clicks++;
        var root = new Grid { Children = { button } }; using var host = new Host(root);
        button.Focus();
        KeyInput(button, Key.Space, Keyboard.KeyDownEvent); Assert.True(button.IsPressed);
        KeyInput(button, Key.Space, Keyboard.KeyUpEvent); Assert.Equal(1, clicks);
        KeyInput(button, Key.Enter, Keyboard.KeyDownEvent); Assert.Equal(2, clicks);
        AccessKeyManager.ProcessKey(PresentationSource.FromVisual(button), "s", false); Assert.Equal(3, clicks);
        var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button); Pointer(button, true);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        button.ApplyTemplate(); Pump(40);
        Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
        Assert.Equal(0, UiMotion.ChannelTranslate(face, MotionChannel.Interaction).Y);
        Assert.Equal(1, UiMotion.EnsureScale(face).ScaleX);
    });

    private static (Grid Root, Border Sidebar, ColumnDefinition Column, Grid Content, TextBlock Label) SidebarFixture()
    {
        var root = new Grid(); var column = new ColumnDefinition { Width = new(64) }; root.ColumnDefinitions.Add(column); root.ColumnDefinitions.Add(new());
        var label = new TextBlock { Text = "Dashboard", Margin = new(64, 0, 0, 0) }; SidebarResizeMotion.SetIsLabel(label, true);
        var sidebar = new Border { Background = Brushes.SlateBlue, Child = label }; SidebarResizeMotion.SetIsEnabled(sidebar, true);
        var content = new Grid { Name = "SidebarContentStage", Background = Brushes.Azure }; Grid.SetColumn(content, 1);
        root.Children.Add(content); root.Children.Add(sidebar); return (root, sidebar, column, content, label);
    }
    private static void SeekSidebar(Border sidebar, int ms)
    {
        SidebarTestClock.Pause(sidebar)(ms);
    }
    [Fact]
    public void Sidebar_edge_and_viewport_reverse_together_with_live_policy_cleanup() => Sta(() =>
    {
        var f = SidebarFixture(); using var host = new Host(f.Root);
        Assert.Equal(0, f.Label.Opacity); Assert.Equal(64, ((RectangleGeometry)f.Sidebar.Clip).Rect.Width);
        var commits = 0; var descriptor = DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition));
        EventHandler changed = (_, _) => commits++; descriptor.AddValueChanged(f.Column, changed);
        var completed = 0;
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, 64, 232, onComplete: () => completed++);
        SeekSidebar(f.Sidebar, 130); var visible = ((RectangleGeometry)f.Sidebar.Clip).Rect.Width;
        Assert.InRange(visible, 110, 190); Assert.Equal(visible, f.Column.Width.Value, 5); Assert.True(commits > 0);
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, visible, 64, onComplete: () => completed++);
        SeekSidebar(f.Sidebar, 0); Assert.Equal(visible, ((RectangleGeometry)f.Sidebar.Clip).Rect.Width, 5);
        Assert.Equal(visible, f.Column.Width.Value, 5); Assert.Equal(0, completed);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.Equal(64, ((RectangleGeometry)f.Sidebar.Clip).Rect.Width); Assert.Equal(0, f.Label.Opacity);
        Assert.Equal(0, UiMotion.ChannelTranslate(f.Content, MotionChannel.Layout).X); Assert.Equal(1, completed);
        Assert.False(NativeSurfaceMotion.IsRunning(f.Content, MotionChannel.Layout));
        descriptor.RemoveValueChanged(f.Column, changed);
    });
    [Fact]
    public void Sidebar_resize_and_unload_finish_the_current_transition_once() => Sta(() =>
    {
        var f = SidebarFixture(); using var host = new Host(f.Root); int completed = 0;
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, 64, 232, onComplete: () => completed++);
        host.Window.Width += 100; host.Window.UpdateLayout(); Pump(35);
        Assert.Equal(232, ((RectangleGeometry)f.Sidebar.Clip).Rect.Width); Assert.Equal(1, completed);
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, 232, 64, onComplete: () => completed++);
        f.Root.Children.Remove(f.Sidebar); Pump(35);
        Assert.Equal(2, completed); Assert.False(NativeSurfaceMotion.IsRunning(f.Content, MotionChannel.Layout));
    });

    [Fact]
    public void Staging_limits_content_and_cancels_pending_groups_on_dismissal() => Sta(() =>
    {
        var root = new StackPanel(); var heading = new TextBlock { Text = "Heading" }; ArrivalGroups.SetRole(heading, ArrivalRole.Heading); root.Children.Add(heading);
        var groups = Enumerable.Range(0, 5).Select(i => new Border { Height = 45, Child = new TextBlock { Text = $"Group {i}" } }).ToArray();
        foreach (var group in groups) { ArrivalGroups.SetRole(group, ArrivalRole.Content); root.Children.Add(group); }
        var action = new Button { Content = "Save" }; ArrivalGroups.SetRole(action, ArrivalRole.Actions); root.Children.Add(action);
        using var host = new Host(root); ArrivalGroups.Start(root, true);
        Assert.Equal(.35, heading.Opacity); Assert.Equal(.35, action.Opacity);
        Assert.Equal(3, groups.Count(x => NativeSurfaceMotion.IsRunning(x, MotionChannel.Presence)));
        Seek(heading, MotionChannel.Presence, 20); Assert.Equal(.35, heading.Opacity, 6);
        Seek(action, MotionChannel.Presence, 90); Assert.Equal(.35, action.Opacity, 6);
        ArrivalGroups.Stop(root, true); Pump(320);
        Assert.Equal(1, action.Opacity); Assert.Equal(1, heading.Opacity);
        Assert.All(groups, x => Assert.False(NativeSurfaceMotion.IsRunning(x, MotionChannel.Presence)));
        MotionPolicy.Configure(MotionPreference.Off, false); ArrivalGroups.Start(root, true);
        Assert.Equal(1, action.Opacity); Assert.Equal(1, heading.Opacity);
    });

    [Fact]
    public void Brief_loading_stays_unpainted_and_completion_is_immediate() => Sta(() =>
    {
        var panel = new ContextualStatePanel { IsLoading = true, Title = "Loading" }; using var host = new Host(new Grid { Children = { panel } });
        Assert.All(panel.Children.Cast<UIElement>(), x => Assert.Equal(0, x.Opacity));
        panel.IsLoading = false; Assert.True(NativeSurfaceMotion.IsRunning(panel.Children[0], MotionChannel.Presence));
        MotionPolicy.Configure(MotionPreference.Off, false); Pump(180);
        Assert.All(panel.Children.Cast<UIElement>(), x => Assert.Equal(1, x.Opacity));
        Assert.Equal(0, UiMotion.ChannelTranslate((FrameworkElement)panel.Children[0], MotionChannel.Presence).Y);
    });
    [Fact]
    public void Slow_loading_reveals_once_and_unload_cancels_the_timer() => Sta(() =>
    {
        var panel = new ContextualStatePanel { IsLoading = true, Title = "Loading" }; var root = new Grid { Children = { panel } };
        using var host = new Host(root); Pump(180);
        Assert.True(panel.Children[0].Opacity > 0);
        panel.IsLoading = false; panel.IsLoading = true; root.Children.Remove(panel); Pump(200);
        Assert.False(NativeSurfaceMotion.IsRunning(panel.Children[0], MotionChannel.Presence));
    });

    [Fact]
    public void Disclosure_reversal_preserves_native_expander_and_reduced_policy_settles_layout() => Sta(() =>
    {
        var expander = new Expander { Header = "Advanced", Content = new Border { Height = 130, Background = Brushes.CornflowerBlue } };
        using var host = new Host(new StackPanel { Children = { expander } });
        var content = (FrameworkElement)expander.Template.FindName("DisclosureContent", expander);
        Assert.Equal(Visibility.Collapsed, content.Visibility);
        Assert.IsType<System.Windows.Automation.Peers.ExpanderAutomationPeer>(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(expander));
        var header = (System.Windows.Controls.Primitives.ToggleButton)expander.Template.FindName("HeaderSite", expander);
        header.Focus(); KeyInput(header, Key.Space, Keyboard.KeyDownEvent); KeyInput(header, Key.Space, Keyboard.KeyUpEvent);
        Assert.True(expander.IsExpanded); expander.IsExpanded = false;
        expander.IsExpanded = true; Pump(60); expander.IsExpanded = false; Pump(40); expander.IsExpanded = true;
        MotionPolicy.Configure(MotionPreference.Reduced, false); Pump(100);
        Assert.Equal(Visibility.Visible, content.Visibility); Assert.True(content.IsEnabled);
        expander.IsExpanded = false; Pump(300); Assert.Equal(Visibility.Collapsed, content.Visibility);
    });
    [Fact]
    public void Recycled_history_rows_snap_selection_and_release_hover_and_adorner_motion() => Sta(() =>
    {
        var row = new Border { Height = 60, Child = new TextBlock { Text = "Version" }, CornerRadius = new(10) };
        StateSurfaceMotionBehavior.SetIsEnabled(row, true); using var host = new Host(new System.Windows.Documents.AdornerDecorator { Child = row });
        StateSurfaceMotionBehavior.SetIsSelected(row, true);
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        StateSurfaceMotionBehavior.SetIsSelected(row, false); row.DataContext = new object();
        Assert.Equal(0, SurfaceFill.Get(row, SurfaceFill.Role.Selection, false)!.Opacity);
        Assert.False(NativeSurfaceMotion.IsRunning(row, MotionChannel.Selection));
        Assert.False(NativeSurfaceMotion.IsRunning(row, MotionChannel.Interaction));
        host.Window.Content = null; Pump(35); Assert.Equal(0, UiMotion.ChannelTranslate(row, MotionChannel.Interaction).Y);
    });

    [Fact]
    public void Directional_selection_entries_keep_counts_immediate_and_do_not_replay_realization() => Sta(() =>
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<string>();
        var list = new ListBox { ItemsSource = items, Height = 200 };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ItemPresenceMotionBehavior.SetIsEnabled(list, true); ItemPresenceMotionBehavior.SetEntryOffsetX(list, -8);
        ItemPresenceMotionBehavior.SetRise(list, 0); ItemPresenceMotionBehavior.SetEnterDuration(list, 220);
        using var host = new Host(new Grid { Children = { list } });
        items.Add("Selected quiz"); Assert.Single(list.Items);
        list.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.True(NativeSurfaceMotion.IsRunning(container, MotionChannel.Presence));
        Seek(container, MotionChannel.Presence, 0);
        Assert.Equal(-8, UiMotion.ChannelTranslate(container, MotionChannel.Presence).X);
        Assert.Equal(0, UiMotion.ChannelTranslate(container, MotionChannel.Presence).Y);
        items.Clear(); Assert.Empty(list.Items); Pump(400);
        Assert.False(NativeSurfaceMotion.IsRunning(container, MotionChannel.Presence));
        MotionPolicy.Configure(MotionPreference.Off, false);
        list.ItemsSource = Enumerable.Range(0, 20_000).Select(i => $"Quiz {i}").ToArray(); list.UpdateLayout(); Pump(30);
        MotionPolicy.Configure(MotionPreference.Full, false); list.ScrollIntoView(list.Items[19000]); list.UpdateLayout(); Pump(40);
        Assert.InRange(list.ItemContainerGenerator.Items.Count, 20_000, 20_000);
        for (int i = 18990; i < 19020; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is UIElement realized)
                Assert.False(NativeSurfaceMotion.IsRunning(realized, MotionChannel.Presence));
    });
    [Fact]
    public void Selection_plate_navigation_and_edge_resize_use_independent_channels() => Sta(() =>
    {
        var f = SidebarFixture(); var group = new Grid(); NameScope.SetNameScope(group, new NameScope());
        SidebarNavMotionBehavior.SetIsGroupHost(group, true);
        var plate = new SidebarSelectionSurface { Visibility = Visibility.Collapsed, Fill = Brushes.CornflowerBlue };
        group.RegisterName("SidebarSelectionPlate", plate);
        var canvas = new Canvas { IsHitTestVisible = false, Children = { plate } }; group.Children.Add(canvas);
        var buttons = new StackPanel(); group.Children.Add(buttons);
        var first = new Button { Content = "Notes", Height = 40 }; var second = new Button { Content = "Quizzes", Height = 40 };
        buttons.Children.Add(first); buttons.Children.Add(second); f.Sidebar.Child = null; f.Sidebar.Child = new StackPanel { Children = { f.Label, group } };
        using var host = new Host(f.Root);
        SidebarNavMotionBehavior.SetIsActive(first, true);
        Assert.Equal(48, plate.PaintedWidth);
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, 64, 232);
        SidebarNavMotionBehavior.SetIsActive(first, false); SidebarNavMotionBehavior.SetIsActive(second, true);
        SeekSidebar(f.Sidebar, 130);
        Assert.Equal(((RectangleGeometry)f.Sidebar.Clip).Rect.Width - 16, plate.PaintedWidth, 5);
        Assert.True(NativeSurfaceMotion.IsRunning(plate, MotionChannel.Navigation));
        MotionPolicy.Configure(MotionPreference.Off, false);
        Assert.Equal(216, plate.PaintedWidth); Assert.Equal(40, UiMotion.EnsureTranslate(plate).Y);
        Assert.False(NativeSurfaceMotion.IsRunning(plate, MotionChannel.Layout)); Assert.False(NativeSurfaceMotion.IsRunning(plate, MotionChannel.Navigation));
    });
    [Fact]
    public void Loading_completion_does_not_delay_items_and_a_new_generation_cancels_the_old_fade() => Sta(() =>
    {
        var list = new ListBox(); using var host = new Host(list);
        LoadContinuityBehavior.SetIsLoading(list, true);
        list.Items.Add("Ready immediately"); LoadContinuityBehavior.SetIsLoading(list, false);
        Assert.Single(list.Items); Assert.True(list.IsEnabled); Assert.True(list.IsHitTestVisible);
        Assert.True(NativeSurfaceMotion.IsRunning(list, MotionChannel.Presence));
        LoadContinuityBehavior.SetIsLoading(list, true);
        Assert.False(NativeSurfaceMotion.IsRunning(list, MotionChannel.Presence)); Assert.Equal(1, list.Opacity);
        MotionPolicy.Configure(MotionPreference.Reduced, false); LoadContinuityBehavior.SetIsLoading(list, false); Pump(200);
        Assert.Equal(1, list.Opacity); Assert.False(NativeSurfaceMotion.IsRunning(list, MotionChannel.Presence));
    });
    private static DpiChangedEventArgs DpiArgs(Window window, double oldScale, double newScale)
    {
        var constructor = typeof(DpiChangedEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();
        int dpi = 0;
        var arguments = constructor.GetParameters().Select(p => p.ParameterType == typeof(DpiScale)
            ? (object)(dpi++ == 0 ? new DpiScale(oldScale, oldScale) : new DpiScale(newScale, newScale))
            : p.ParameterType == typeof(RoutedEvent) ? Window.DpiChangedEvent : (object)window).ToArray();
        var args = (DpiChangedEventArgs)constructor.Invoke(arguments); args.RoutedEvent = Window.DpiChangedEvent; args.Source = window; return args;
    }
    [Fact]
    public void Sidebar_dpi_notification_settles_the_edge_and_unsubscribes_on_unload() => Sta(() =>
    {
        var f = SidebarFixture(); using var host = new Host(f.Root); int completed = 0;
        UiMotion.AnimateSidebarChrome(f.Sidebar, f.Column, 64, 232, onComplete: () => completed++);
        host.Window.RaiseEvent(DpiArgs(host.Window, 1, 1.5));
        Assert.Equal(232, ((RectangleGeometry)f.Sidebar.Clip).Rect.Width); Assert.Equal(1, completed);
        Assert.Equal(0, UiMotion.ChannelTranslate(f.Content, MotionChannel.Layout).X);
        f.Root.Children.Remove(f.Sidebar); Pump(25);
        host.Window.RaiseEvent(DpiArgs(host.Window, 1.5, 2));
        Assert.Equal(1, completed);
    });
}
