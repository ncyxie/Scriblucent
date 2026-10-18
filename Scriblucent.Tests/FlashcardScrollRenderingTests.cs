using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Rendering;
using Scriblucent.Views;
using SkiaSharp;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class FlashcardScrollRenderingTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { SkiaCardTileCache.Clear(); MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception caught) { error = caught; }
            finally { SkiaCardTileCache.Clear(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew(); while (!condition() && watch.ElapsedMilliseconds < 5000) Pump(10);
        Assert.True(condition());
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    [Fact]
    public void Frozen_paint_is_independent_of_later_UI_changes_and_replays_on_a_worker() => Sta(() =>
    {
        var brush = new SolidColorBrush(Colors.Red);
        var border = new Border { Width = 316, Height = 224, Background = brush,
            Child = new TextBlock { Text = "Immutable card", Foreground = Brushes.White, Margin = new Thickness(16) } };
        var window = new Window { Content = border, Width = 400, Height = 300, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); Pump(25);
            var snapshot = FrozenCardDrawing.Capture(border)!; Assert.True(snapshot.Drawing.IsFrozen);
            brush.Color = Colors.Blue;
            var pixel = Task.Run(() => { using var bitmap = snapshot.Render(1.25, out var height); Assert.Equal(224, height);
                return bitmap.GetPixel((int)((FrozenCardDrawing.Fringe + 100) * 1.25), (int)((FrozenCardDrawing.Fringe + 100) * 1.25)); }).GetAwaiter().GetResult();
            Assert.Equal(SKColors.Red, pixel);
        }
        finally { window.Content = null; Pump(40); window.Close(); Pump(40); }
    });
    private sealed class Vm { internal int Revision; }
    private sealed class MeasuredFace : Border
    {
        internal int Measures;
        protected override Size MeasureOverride(Size availableSize) { Measures++; return base.MeasureOverride(availableSize); }
    }
    [Fact]
    public void Deferred_answer_face_is_not_measured_while_browsing_and_both_flip_directions_work() => Sta(() =>
    {
        var front = new MeasuredFace { Width = 316, Height = 224, Background = Brushes.Red };
        var back = new MeasuredFace { Width = 316, Height = 224, Background = Brushes.Blue };
        var flip = new FlashcardFlipHost { Width = 316, Height = 224, DeferHiddenFaceLayout = true, FrontContent = front, BackContent = back };
        var window = new Window { Content = flip, Width = 400, Height = 300, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(ControlTestResources.Load("Tokens"));
        window.Resources.MergedDictionaries.Add(ControlTestResources.Load("LightTheme"));
        try
        {
            window.Show(); window.UpdateLayout(); Pump(60);
            Assert.True(front.Measures > 0); Assert.Equal(0, back.Measures);
            flip.AnimateToggle(240); Assert.True(back.Measures > 0);
            Until(() => !flip.IsAnimating); Assert.True(flip.IsFlipped);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)flip.FindName("FrontHost")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)flip.FindName("BackFace")).Visibility);
            flip.AnimateToggle(240); Until(() => !flip.IsAnimating); Assert.False(flip.IsFlipped);
            flip.AnimateToggle(240); Pump(60); flip.PrepareForReuse();
            Assert.False(flip.IsAnimating); Assert.Equal(Visibility.Collapsed, ((FrameworkElement)flip.FindName("BackFace")).Visibility);
            MotionPolicy.Configure(MotionPreference.Reduced, false); flip.AnimateToggle(240);
            Assert.True(flip.IsFlipped); Assert.False(flip.IsAnimating);
        }
        finally { MotionPolicy.Configure(MotionPreference.Automatic, false); window.Content = null; Pump(40); window.Close(); Pump(40); }
    });
    [Fact]
    public void Incremental_capture_yields_and_preserves_native_painter_order() => Sta(() =>
    {
        var content = new Grid { Width = 100, Height = 100, Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(new Border { Width = 70, Height = 70, Background = Brushes.Blue });
        content.Children.Add(new Border { Width = 30, Height = 30, Background = Brushes.Lime });
        var window = new Window { Content = content, Width = 150, Height = 150, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); Pump(25);
            var builder = FrozenCardDrawing.Builder.Create(content)!;
            Assert.False(builder.Advance(0));
            var slices = 1; while (!builder.Advance(0)) slices++;
            Assert.True(slices > 3); Assert.True(builder.Result!.Drawing.IsFrozen);
            using var bitmap = builder.Result.Render(1, out _);
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(67,67));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(42,42));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(22,22));
        }
        finally { window.Content = null; Pump(40); window.Close(); Pump(40); }
    });
    [Fact]
    public void Paint_replay_respects_geometry_transforms() => Sta(() =>
    {
        var drawing = new DrawingGroup();
        var shape = new RectangleGeometry(new Rect(0,0,20,20)) { Transform = new TranslateTransform(40,50) };
        drawing.Children.Add(new GeometryDrawing(Brushes.Blue, null, shape)); drawing.Freeze();
        using var bitmap = new FrozenCardDrawing(drawing, 100, 100).Render(1, out _);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(62,72)); Assert.Equal(0, bitmap.GetPixel(22,22).Alpha);
    });
    private sealed class Renderer(ManualResetEventSlim gate) : SkiaTileRenderer
    {
        internal readonly ManualResetEventSlim Started = new(false);
        public override string BuildSignature(object vm) => ((Vm)vm).Revision.ToString();
        public override SKBitmap Render(object input, double dpi, float width, out double height)
        {
            Started.Set(); Assert.True(gate.Wait(TimeSpan.FromSeconds(5))); height = 20;
            var bitmap = new SKBitmap((int)(20*dpi), (int)(20*dpi));
            bitmap.Erase((int)input == 2 ? new SKColor(255, 0, 0, 128) : (int)input == 0 ? SKColors.Red : SKColors.Blue); return bitmap;
        }
        public override SKBitmap RenderPlaceholder(double dpi) => new(20, 20);
    }
    [Fact]
    public void New_revision_replaces_in_flight_work_and_notifies_all_current_subscribers() => Sta(() =>
    {
        using var gate = new ManualResetEventSlim(); var renderer = new Renderer(gate); var vm = new Vm();
        int stale = 0, first = 0, second = 0;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => stale++, 20, () => vm.Revision);
        Assert.True(renderer.Started.Wait(TimeSpan.FromSeconds(5))); vm.Revision = 1;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => first++, 20, () => vm.Revision);
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => second++, 20, () => throw new Exception("Duplicated capture"));
        gate.Set(); Until(() => first == 1 && second == 1); Assert.Equal(0, stale);
        var cached = SkiaCardTileCache.GetReady(vm, 1, renderer, 20);
        Assert.NotNull(cached); Assert.True(cached.IsFrozen);
        Assert.Null(SkiaCardTileCache.GetReady(vm, 1.5, renderer, 20));
        Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 21));
        vm.Revision++;
        Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
    });
    [Fact]
    public void Cache_clear_prevents_old_work_repopulating_the_new_generation() => Sta(() =>
    {
        using var gate = new ManualResetEventSlim(); var renderer = new Renderer(gate); var vm = new Vm(); var old = 0;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => old++, 20, () => 0);
        Assert.True(renderer.Started.Wait(TimeSpan.FromSeconds(5))); SkiaCardTileCache.Clear(); gate.Set(); Pump(120);
        Assert.Equal(0, old);
        Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
    });
    [Fact]
    public void Translucent_tiles_keep_premultiplied_color_and_forget_releases_the_cached_item() => Sta(() =>
    {
        using var gate = new ManualResetEventSlim(true); var renderer = new Renderer(gate); var vm = new Vm { Revision = 2 };
        System.Windows.Media.Imaging.BitmapSource? source = null;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, value => source = value, 20, () => vm.Revision);
        Until(() => source is not null);
        Assert.Equal(PixelFormats.Pbgra32, source!.Format);
        var bytes = new byte[4]; source.CopyPixels(new Int32Rect(0, 0, 1, 1), bytes, 4, 0);
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, bytes);
        SkiaCardTileCache.Forget(vm, renderer);
        Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
    });
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Ready_tiles_preserve_input_geometry_and_first_press_ends_the_glide(string theme) => Sta(() =>
    {
        var grid = new FlashcardGridItemsControl();
        ScrollViewer.SetCanContentScroll(grid, true);
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ScrollViewer"); scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); grid.Template = new ControlTemplate(typeof(FlashcardGridItemsControl)) { VisualTree = scroll };
        var panel = new FrameworkElementFactory(typeof(WpfToolkit.Controls.VirtualizingWrapPanel)); panel.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.ItemSizeProperty, new Size(332,248));
        grid.ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(grid, ScrollUnit.Pixel);
        var tile = new FrameworkElementFactory(typeof(DashboardTileHost)); tile.SetValue(DashboardTileHost.RendererKeyProperty, "FlashcardGridCard");
        tile.SetValue(DashboardTileHost.PreserveLiveInputInBitmapModeProperty, true);
        var button = new FrameworkElementFactory(typeof(Button)); button.SetValue(FrameworkElement.WidthProperty, 316d); button.SetValue(FrameworkElement.HeightProperty, 224d);
        button.SetValue(ContentControl.ContentProperty, "Native input"); tile.AppendChild(button); grid.ItemTemplate = new DataTemplate { VisualTree = tile };
        grid.ItemsSource = Enumerable.Range(0, 24).Select(i => new FlashcardsPreviewWindow.FlashcardPreviewItem(Guid.NewGuid(), "Question", "Answer", 0)).ToArray();
        var window = new Window { Content = grid, Width = 760, Height = 580, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        foreach (var resource in new[] { "Tokens", theme+"Theme", "Generic", "Controls/Shared", "Controls/Buttons" }) window.Resources.MergedDictionaries.Add(ControlTestResources.Load(resource));
        try
        {
            window.Show(); window.UpdateLayout(); Pump(100);
            var viewer = Tree(grid).OfType<ScrollViewer>().First(); SmoothScrollBehavior.SetIsEnabled(viewer, true);
            var host = Tree(grid).OfType<DashboardTileHost>().First(); host.QueueWarm(); Until(() => host.HasCachedScrollTile);
            host.IsBitmapMode = true; Assert.True(host.HasReadyScrollTile);
            host.IsBitmapMode = false; var size = host.DesiredSize;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = host });
            Assert.True(SmoothScrollCompositor.IsActive(viewer)); Assert.True(host.HasReadyScrollTile);
            window.UpdateLayout(); Assert.Equal(size, host.DesiredSize);
            var native = Tree(host).OfType<Button>().First();
            var face = Tree(native).OfType<InteractionSurface>().First();
            native.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent, Source = native });
            Assert.False(NativeSurfaceMotion.IsRunning(face, MotionChannel.Interaction));
            var cold = Tree(grid).OfType<DashboardTileHost>().Last();
            ((FlashcardsPreviewWindow.FlashcardPreviewItem)cold.DataContext).Question = "Changed during movement";
            var attempts = FrozenCardDrawing.CaptureAttempts;
            var repeatWheel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            repeatWheel.Tick += (_, _) => viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
            repeatWheel.Start();
            try { host.QueueWarm(); cold.QueueWarm(); Pump(180); }
            finally { repeatWheel.Stop(); }
            Assert.Equal(attempts, FrozenCardDrawing.CaptureAttempts);
            Assert.True(host.HasReadyScrollTile); Assert.False(cold.HasReadyScrollTile);
            Assert.Same(native, host.InputHitTest(new Point(158,112)) is DependencyObject hit ? FindButton(hit) : null);
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = native });
            Assert.False(SmoothScrollCompositor.IsActive(viewer)); Assert.False(host.HasReadyScrollTile); Assert.False(ScrollTileBehavior.GetIsScrolling(viewer));
            // Reload without toggling IsEnabled: the behavior must attach again.
            window.Content = null; Pump(10); window.Content = grid; window.UpdateLayout(); Pump(30);
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
            Assert.True(ScrollTileBehavior.GetIsScrolling(viewer));
            MotionPolicy.Configure(MotionPreference.Off, false);
            Assert.False(ScrollTileBehavior.GetIsScrolling(viewer)); Assert.False(SmoothScrollCompositor.IsActive(viewer));
        }
        finally { MotionPolicy.Configure(MotionPreference.Automatic, false); window.Content = null; Pump(40); window.Close(); Pump(40); }
    });
    private static Button? FindButton(DependencyObject? item)
    { for (; item is not null; item = VisualTreeHelper.GetParent(item)) if (item is Button button) return button; return null; }
}
