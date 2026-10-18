using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Windows.Media;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DashboardViewportTests
{
    [Fact]
    public void Parked_group_paint_does_not_rebuild_during_resize_and_returns_when_scrolled_back() => Sta(() =>
    {
        var viewer = new ScrollViewer(); var stack = new StackPanel(); viewer.Content = stack;
        var grid = new CountingGrid { ItemCellSize = new Size(244, 164), ItemsSource = Enumerable.Range(0, 9).ToArray() };
        var host = new DashboardVirtualizationHost { Child = grid, HostMode = DashboardVirtualizationHostMode.Static, ScrollViewerElement = viewer };
        var panel = new DashboardGroupLayoutPanel(); panel.Children.Add(new Border { Height = 36 });
        panel.Children.Add(new DashboardGroupRevealHost { Child = host });
        var card = new DashboardGroupCard { Child = panel };
        var paint = new CountingPaint(); Assert.True(paint.Attach(card, 0));
        stack.Children.Add(card); stack.Children.Add(new Border { Height = 2000 });
        var window = new Window { Content = viewer, Width = 900, Height = 500, ShowInTaskbar = false };
        void Settle() { for (int i = 0; i < 4; i++) { Pump(); window.UpdateLayout(); } }
        try
        {
            window.Show(); Settle();
            Assert.True(card.IsVisible); Assert.True(paint.Draws > 0);
            viewer.ScrollToBottom(); Settle(); Assert.False(card.IsVisible);
            var before = paint.Draws;
            for (int i = 0; i < 8; i++) { window.Width -= 2; window.UpdateLayout(); Pump(); }
            Assert.Equal(before, paint.Draws);
            viewer.ScrollToTop(); Settle();
            Assert.True(card.IsVisible); Assert.True(paint.Draws > before);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Note_size_estimates_preserve_whitespace_semantics_without_allocating_preview_copies() => Sta(() =>
    {
        var card = new NoteCardViewModel(new NoteDocument
            { SearchablePreview = "\r\n\u2003" + new string('x', 72) + "\r\n" + new string('y', 72) + "\u2002\n" }, _ => { });
        var grid = new NoteCardItemSizeProvider(false); var list = new NoteCardItemSizeProvider(true);
        Assert.Equal(209, grid.GetSizeForItem(card).Height);
        Assert.Equal(106, list.GetSizeForItem(card).Height);
        for (int i = 0; i < 10; i++) _ = grid.GetSizeForItem(card);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) _ = grid.GetSizeForItem(card);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
        var blank = new NoteCardViewModel(new NoteDocument { SearchablePreview = "\r\n \u2003\u2002" }, _ => { });
        Assert.Equal(164, grid.GetSizeForItem(blank).Height);
        Assert.Equal(88, list.GetSizeForItem(blank).Height);
    });

    [Fact]
    public void Distant_groups_never_realize_cards_at_startup_or_reflow_their_headers_during_resize() => Sta(() =>
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(FrameworkElement.WidthProperty, 220d); face.SetValue(FrameworkElement.HeightProperty, 140d);
        var template = new DataTemplate { VisualTree = face };
        var viewer = new ScrollViewer(); var stack = new StackPanel(); viewer.Content = stack;
        var groups = Enumerable.Range(0, 100).Select(_ =>
        {
            var header = new CountingHeader { Height = 36 };
            var grid = new CountingGrid { ItemCellSize = new Size(244, 164), ItemTemplate = template,
                ItemsSource = Enumerable.Range(0, 200).ToArray() };
            var host = new DashboardVirtualizationHost { Child = grid, HostMode = DashboardVirtualizationHostMode.Static, ScrollViewerElement = viewer };
            var body = new DashboardGroupRevealHost { Child = host, IsRevealed = true };
            var panel = new DashboardGroupLayoutPanel(); panel.Children.Add(header); panel.Children.Add(body);
            // Production material/light layers insert a Grid between the frame and its layout.
            var card = new DashboardGroupCard { Child = new Grid { Children = { panel } }, Margin = new Thickness(0, 0, 0, 14) };
            stack.Children.Add(card);
            return (header, grid, host, body, card);
        }).ToArray();
        var window = new Window { Width = 900, Height = 500, Content = viewer, ShowInTaskbar = false };
        void Settle() { for (int i = 0; i < 4; i++) { Pump(); window.UpdateLayout(); } }
        try
        {
            window.Show(); Settle();
            Assert.InRange(groups.Sum(g => g.grid.Prepared), 1, 80);
            Assert.InRange(groups.Count(g => g.card.IsVisible), 1, 3);
            Assert.All(groups.Skip(3), g => Assert.Equal(0, g.grid.Prepared));
            var before = groups.Skip(3).Sum(g => g.header.Measures);
            for (int i = 0; i < 12; i++) { window.Width = 900 - i * 3; window.UpdateLayout(); Pump(); }
            Assert.InRange(groups.Skip(3).Sum(g => g.header.Measures) - before, 0, 5);
            Assert.All(groups.Skip(3), g => Assert.Equal(0, g.grid.Prepared));
            for (int i = 1; i <= 8; i++)
            {
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * i / 8); Settle();
                Assert.InRange(groups.Sum(g => g.grid.ResolveItemsPanel()?.Children.Count ?? 0), 1, 100);
            }
            viewer.ScrollToBottom(); Settle();
            Assert.True(groups[^1].card.IsVisible);
            Assert.True(groups[^1].grid.HasRealizedContainers());
            Assert.False(groups[0].card.IsVisible);
            viewer.ScrollToTop(); Settle();
            Assert.True(groups[0].card.IsVisible);
            Assert.True(groups[0].grid.HasRealizedContainers());
            var expanded = viewer.ExtentHeight;
            groups[0].body.IsRevealed = false; Settle();
            Assert.True(viewer.ExtentHeight < expanded - 1000);
            groups[0].body.IsRevealed = true; Settle();
            Assert.InRange(viewer.ExtentHeight, expanded - 1, expanded + 1);
        }
        finally { window.Close(); MotionPolicy.Configure(MotionPreference.Automatic, false); }
    });

    [Theory]
    [InlineData(DashboardVirtualizationHostMode.PageVirtualized)]
    [InlineData(DashboardVirtualizationHostMode.Static)]
    public void Resizing_an_offscreen_section_does_not_recreate_its_cards_every_pass(DashboardVirtualizationHostMode mode) => Sta(() =>
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(FrameworkElement.WidthProperty, 220d);
        face.SetValue(FrameworkElement.HeightProperty, 140d);
        var grid = new CountingGrid
        {
            ItemCellSize = new Size(244, 164),
            ItemTemplate = new DataTemplate { VisualTree = face },
            ItemsSource = Enumerable.Range(0, 9).ToArray()
        };
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = new DashboardVirtualizationHost { Child = grid, HostMode = mode, ScrollViewerElement = viewer };
        var stack = new StackPanel();
        stack.Children.Add(new Border { Height = 1200 }); stack.Children.Add(host);
        viewer.Content = stack;
        var window = new Window { Width = 900, Height = 500, Content = viewer, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            host.RequestViewportRefresh(); window.UpdateLayout();
            Assert.False(grid.IsVisible, "Off-screen cards must also leave render/cache/motion sweeps.");
            var before = grid.Prepared;
            for (int i = 0; i < 12; i++)
            {
                window.Width = 900 - i * 3;
                window.UpdateLayout();
                host.RequestViewportRefresh(); window.UpdateLayout();
            }
            Assert.True(grid.Prepared - before <= 9, $"Off-screen resize prepared {grid.Prepared - before} cards; it repeatedly rebuilt an invisible section.");
            Assert.True(viewer.ExtentHeight >= 1500, "The invisible section must retain its scroll extent.");
            viewer.ScrollToBottom(); Pump(); window.UpdateLayout(); Pump();
            Assert.True(grid.HasRealizedContainers(), "Scrolling to the section must still realize its cards.");
            Assert.True(grid.IsVisible);
            Assert.True(grid.ActualHeight > 100);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_large_grouped_page_only_activates_visible_sections_and_can_jump_to_the_last_group() => Sta(() =>
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(FrameworkElement.WidthProperty, 220d); face.SetValue(FrameworkElement.HeightProperty, 140d);
        var template = new DataTemplate { VisualTree = face };
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel(); viewer.Content = stack;
        var grids = Enumerable.Range(0, 12).Select(_ => new CountingGrid
        {
            ItemCellSize = new Size(244, 164), ItemTemplate = template,
            ItemsSource = Enumerable.Range(0, 1700).ToArray()
        }).ToArray();
        foreach (var grid in grids) stack.Children.Add(new DashboardVirtualizationHost
            { Child = grid, HostMode = DashboardVirtualizationHostMode.Static, ScrollViewerElement = viewer });
        var window = new Window { Width = 900, Height = 500, Content = viewer, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            Assert.InRange(grids.Count(g => g.IsVisible), 1, 2);
            Assert.InRange(grids.Sum(g => g.Prepared), 1, 80);
            viewer.ScrollToBottom(); Pump(); window.UpdateLayout(); Pump();
            Assert.True(grids[^1].IsVisible);
            Assert.True(grids[^1].HasRealizedContainers());
            Assert.False(grids[0].IsVisible);
            Assert.InRange(grids.Count(g => g.IsVisible), 1, 2);
            viewer.ScrollToTop(); Pump(); window.UpdateLayout(); Pump();
            Assert.True(grids[0].IsVisible);
            Assert.True(grids[0].HasRealizedContainers());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Parking_a_grid_without_an_extent_estimate_retains_its_measured_scroll_extent() => Sta(() =>
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(FrameworkElement.WidthProperty, 220d); face.SetValue(FrameworkElement.HeightProperty, 300d);
        var grid = new DashboardVirtualizedCardGrid { ItemTemplate = new DataTemplate { VisualTree = face },
            ItemsSource = Enumerable.Range(0, 9).ToArray() };
        var viewer = new ScrollViewer(); var stack = new StackPanel(); viewer.Content = stack;
        var host = new DashboardVirtualizationHost { Child = grid, HostMode = DashboardVirtualizationHostMode.Static, ScrollViewerElement = viewer };
        stack.Children.Add(host); stack.Children.Add(new Border { Height = 2000 });
        var window = new Window { Width = 900, Height = 500, Content = viewer, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout(); Pump();
            var measured = host.DesiredSize.Height;
            Assert.True(measured > 600);
            viewer.ScrollToBottom(); Pump(); host.RequestViewportRefresh(); window.UpdateLayout(); Pump();
            Assert.False(grid.IsVisible);
            Assert.InRange(host.DesiredSize.Height, measured - .5, measured + .5);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_nearby_section_warms_before_it_crosses_the_visible_viewport_edge() => Sta(() =>
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(FrameworkElement.WidthProperty, 220d); face.SetValue(FrameworkElement.HeightProperty, 140d);
        var grid = new CountingGrid { ItemCellSize = new Size(244, 164),
            ItemTemplate = new DataTemplate { VisualTree = face }, ItemsSource = Enumerable.Range(0, 300).ToArray() };
        var viewer = new ScrollViewer(); var stack = new StackPanel(); viewer.Content = stack;
        stack.Children.Add(new Border { Height = 600 });
        var host = new DashboardVirtualizationHost { Child = grid, HostMode = DashboardVirtualizationHostMode.Static, ScrollViewerElement = viewer };
        stack.Children.Add(host);
        var window = new Window { Width = 900, Height = 500, Content = viewer, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(); host.RequestViewportRefresh(); window.UpdateLayout(); Pump();
            Assert.True(host.TranslatePoint(new Point(), viewer).Y > viewer.ViewportHeight);
            Assert.True(grid.IsVisible);
            Assert.True(grid.HasRealizedContainers());
            Assert.InRange(grid.Prepared, 1, 40);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Parking_a_viewport_preserves_the_external_visibility_binding() => Sta(() =>
    {
        var source = new Border { Visibility = Visibility.Visible };
        var grid = new DashboardVirtualizedCardGrid();
        grid.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = source });
        grid.SetPageViewportActive(false); Assert.Equal(Visibility.Hidden, grid.Visibility);
        source.Visibility = Visibility.Collapsed; Assert.Equal(Visibility.Collapsed, grid.Visibility);
        grid.SetPageViewportActive(true); Assert.Equal(Visibility.Collapsed, grid.Visibility);
        source.Visibility = Visibility.Visible; Assert.Equal(Visibility.Visible, grid.Visibility);
        Assert.True(System.Windows.Data.BindingOperations.IsDataBound(grid, UIElement.VisibilityProperty));
    });

    private sealed class CountingGrid : DashboardVirtualizedCardGrid
    {
        internal int Prepared;
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        { Prepared++; base.PrepareContainerForItemOverride(element, item); }
    }
    private sealed class CountingHeader : Border
    {
        internal int Measures;
        protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
    }
    private sealed class CountingPaint : SurfacePaint
    {
        internal int Draws;
        protected override void DrawSurface(DrawingContext drawing)
        { Draws++; drawing.DrawRectangle(Brushes.Blue, null, new Rect(RenderSize)); }
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
