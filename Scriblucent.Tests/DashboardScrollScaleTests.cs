using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Services;
using WpfToolkit.Controls;
using Xunit;

namespace Scriblucent.Tests;

/// <summary>
/// Virtualization-scale regression tests. With tens of thousands of items, a large scroll
/// jump (scrollbar thumb drag, track click) must realize the target viewport band promptly.
///
/// Covers two large-collection scrolling failure modes:
/// 1. A smooth-scroll session leaves the panel's realization budget throttled; the next
///    non-session scroll (thumb drag - which never starts a session) keeps the throttle,
///    and the panel walks realization in budget-sized chunks from the stale band - the
///    viewport stays blank/skipped and cards flicker until the walk catches up.
/// 2. The panel's budgeted realization path walks from the previously realized band even
///    when the target band is disjoint (a deep jump) - thousands of deferred re-measures.
/// </summary>
public class DashboardScrollScaleTests
{
    private const int ItemCount = 37000;
    private const double CellWidth = 340;
    private const double CellHeight = 214;

    private const string PlainTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border Tag="{Binding}" Width="330" Height="204" Background="Transparent"/>
        </DataTemplate>
        """;

    /// <summary>Counts measure passes so a test can distinguish the budgeted walk (thousands
    /// of deferred re-measures) from the disjoint-jump path (a handful).</summary>
    public sealed class ProbePanel : VirtualizingWrapPanel
    {
        public int MeasureCount;

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            return base.MeasureOverride(availableSize);
        }
    }

    [Fact]
    public void LargeJump_AfterBudgetedSession_RealizesViewportWithoutSkip()
        => RunOnSta(RunLargeJumpAfterBudgetedSession);

    [Fact]
    public void LargeJump_WithActiveBudget_RealizesTargetBand()
        => RunOnSta(RunLargeJumpWithActiveBudget);

    private static void RunLargeJumpAfterBudgetedSession()
    {
        var items = new ObservableCollection<object>();
        for (var i = 0; i < ItemCount; i++)
            items.Add(new object());

        var template = (DataTemplate)XamlReader.Parse(PlainTemplateXaml);

        var grid = new DashboardVirtualizedCardGrid
        {
            ItemCellSize = new Size(CellWidth, CellHeight),
            ItemTemplate = template,
            ItemsSource = items,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var host = new DashboardVirtualizationHost
        {
            HostMode = DashboardVirtualizationHostMode.PageVirtualized,
            MotionScope = DashboardMotionScope.Flashcards,
            ScrollViewerElement = scrollViewer
        };
        host.Child = grid;
        scrollViewer.Content = host;

        var window = new Window
        {
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            Content = scrollViewer
        };

        window.Show();
        Pump();
        Pump(TimeSpan.FromMilliseconds(100));
        window.UpdateLayout();

        var panel = FindVisualChild<VirtualizingWrapPanel>(grid)
            ?? throw new InvalidOperationException("wrap panel not realized");

        // Simulate the leftover of a smooth-scroll session that ended while throttled.
        panel.RealizeMaxContainersPerPass = 4;
        window.UpdateLayout();

        // The user drags the scrollbar thumb to ~90% of the content.
        var targetOffset = scrollViewer.ScrollableHeight * 0.9;
        Assert.True(targetOffset > 1000, "the 37k-item extent must be large enough to scroll deep");
        scrollViewer.ScrollToVerticalOffset(targetOffset);

        // Let the layout pass raise ScrollChanged, process the queued viewport sync, and
        // measure: a non-session scroll must never keep a stale throttle.
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        Assert.Equal(int.MaxValue, panel.RealizeMaxContainersPerPass);

        // A bounded number of layout passes must fill the viewport band at the target.
        for (var pass = 0; pass < 12; pass++)
        {
            window.UpdateLayout();
            Pump();
        }

        var realized = RealizedIndices(grid, panel);
        Assert.NotEmpty(realized);

        var perRow = Math.Max(1, (int)Math.Floor(panel.ViewportWidth / CellWidth));
        var startRow = (int)Math.Floor(targetOffset / CellHeight);
        var startIndex = startRow * perRow;

        Assert.True(
            realized.Min() >= startIndex - perRow * 4,
            $"realized band must start near the target (min={realized.Min()}, target band={startIndex}) - a stale-band walk is skipping the viewport");
        Assert.True(
            realized.Max() >= startIndex,
            $"realized band must cover the target viewport (max={realized.Max()}, target={startIndex})");

        // The band must be stable across further passes - no realize/derealize blink.
        window.UpdateLayout();
        Pump();
        window.UpdateLayout();
        Pump();
        var settled = RealizedIndices(grid, panel);
        Assert.Equal(realized.OrderBy(i => i).ToList(), settled.OrderBy(i => i).ToList());
    }

    private static void RunLargeJumpWithActiveBudget()
    {
        var items = new ObservableCollection<object>();
        for (var i = 0; i < ItemCount; i++)
            items.Add(new object());

        var panelFactory = new FrameworkElementFactory(typeof(ProbePanel));
        panelFactory.SetValue(VirtualizingWrapPanel.OrientationProperty, Orientation.Horizontal);
        panelFactory.SetValue(VirtualizingWrapPanel.ItemSizeProperty, new Size(CellWidth, CellHeight));
        panelFactory.SetValue(VirtualizingWrapPanel.SpacingModeProperty, SpacingMode.None);
        panelFactory.SetValue(VirtualizingWrapPanel.StretchItemsProperty, false);
        panelFactory.SetValue(VirtualizingWrapPanel.IsGridLayoutEnabledProperty, true);

        var itemsControl = new ItemsControl
        {
            ItemsSource = items,
            ItemsPanel = new ItemsPanelTemplate(panelFactory)
        };
        ScrollViewer.SetCanContentScroll(itemsControl, true);
        VirtualizingPanel.SetIsVirtualizing(itemsControl, true);
        VirtualizingPanel.SetVirtualizationMode(itemsControl, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(itemsControl, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(itemsControl, VirtualizationCacheLengthUnit.Pixel);
        VirtualizingPanel.SetCacheLength(itemsControl, new VirtualizationCacheLength(0, 0));

        var window = new Window
        {
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            Content = itemsControl
        };

        window.Show();
        Pump();
        Pump(TimeSpan.FromMilliseconds(100));

        var panel = FindVisualChild<ProbePanel>(itemsControl)
            ?? throw new InvalidOperationException("wrap panel not realized");

        // Realize the top band, then jump deep while the budget is active (the in-session
        // track-click case). The panel must realize the TARGET band promptly - the walk from
        // the stale base would need thousands of deferred re-measures to get there.
        panel.RealizeMaxContainersPerPass = 4;
        window.UpdateLayout();

        var targetOffset = panel.ExtentHeight * 0.9;
        panel.SetVerticalOffset(targetOffset);

        // A bounded number of single dispatcher turns - NOT a full drain. In the real app the
        // deferred realization runs one step per dispatcher-idle gap between frames, so the
        // walk-based path would still be thousands of passes short after this many turns.
        for (var turn = 0; turn < 10; turn++)
        {
            itemsControl.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }

        var realized = RealizedIndices(itemsControl, panel);
        Assert.NotEmpty(realized);

        var perRow = Math.Max(1, (int)Math.Floor(panel.ViewportWidth / CellWidth));
        var startRow = (int)Math.Floor(targetOffset / CellHeight);
        var startIndex = startRow * perRow;

        Assert.True(
            panel.MeasureCount < 100,
            $"a deep jump under an active budget must not walk realization across thousands of re-measures (measures={panel.MeasureCount})");
        Assert.True(
            realized.Min() >= startIndex - perRow * 2,
            $"realized band must start near the target under an active budget (min={realized.Min()}, target={startIndex})");
        Assert.True(
            realized.Max() >= startIndex,
            $"realized band must cover the target viewport under an active budget (max={realized.Max()}, target={startIndex})");
    }

    private static List<int> RealizedIndices(ItemsControl itemsControl, VirtualizingWrapPanel panel)
    {
        var result = new List<int>();
        foreach (UIElement child in panel.Children)
        {
            var index = itemsControl.ItemContainerGenerator.IndexFromContainer(child);
            if (index >= 0)
                result.Add(index);
        }

        return result;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }

    private static void RunOnSta(Action scenario)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                scenario();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA scenario timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
