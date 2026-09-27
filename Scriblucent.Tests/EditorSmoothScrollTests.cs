using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

public class EditorSmoothScrollTests
{
    [Fact]
    public void Virtualized_list_rows_are_not_baked_as_static_scroll_shells()
    {
        RunSta(() =>
        {
            if (ReducedMotionPreferences.ShouldReduceMotion)
                return;

            var staticShell = new Border
            {
                Height = 40,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8)
            };
            var recycledRow = new Border
            {
                Height = 60,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8)
            };
            var virtualizedRows = new VirtualizingStackPanel();
            virtualizedRows.Children.Add(recycledRow);
            var content = new StackPanel();
            content.Children.Add(staticShell);
            content.Children.Add(virtualizedRows);
            content.Children.Add(new Border { Height = 1_000 });
            var viewer = new ScrollViewer { Content = content, Width = 300, Height = 200 };
            viewer.Measure(new Size(300, 200));
            viewer.Arrange(new Rect(0, 0, 300, 200));
            viewer.UpdateLayout();

            Assert.True(viewer.ScrollableHeight > 1);
            try
            {
                SmoothScrollCompositor.Begin(viewer, Array.Empty<DashboardVirtualizationHost>());
                Assert.True(SmoothScrollCompositor.IsActive(viewer));
                Assert.NotNull(staticShell.CacheMode);
                Assert.Null(recycledRow.CacheMode);
            }
            finally
            {
                SmoothScrollCompositor.End(viewer, 0, viewer.VerticalOffset);
            }
        });
    }

    [Fact]
    public void Editor_scroll_commits_each_eased_position_without_moving_the_document_view()
    {
        RunSta(() =>
        {
            if (ReducedMotionPreferences.ShouldReduceMotion)
                return;

            var document = new FlowDocument();
            for (var i = 0; i < 80; i++)
                document.Blocks.Add(new Paragraph(new Run($"Line {i}")));

            var editor = new RichTextBox { Document = document, Width = 400, Height = 180 };
            editor.Measure(new Size(400, 180));
            editor.Arrange(new Rect(0, 0, 400, 180));
            editor.UpdateLayout();

            var viewer = FindVisualChild<ScrollViewer>(editor)!;
            var content = Assert.IsAssignableFrom<FrameworkElement>(viewer.Content);
            var originalTransform = content.RenderTransform;
            var originalHitTest = content.IsHitTestVisible;
            var began = 0;
            var ended = 0;
            void OnBegan() => began++;
            void OnEnded() => ended++;

            Assert.True(viewer.ScrollableHeight > 100);
            Assert.InRange(Math.Abs(content.ActualHeight - viewer.ViewportHeight), 0, 2);

            SmoothScrollCompositor.SetDirectOffsetMode(viewer, true);
            SmoothScrollCompositor.RegisterSessionLifecycle(viewer, OnBegan, OnEnded);
            try
            {
                SmoothScrollCompositor.Begin(viewer, Array.Empty<DashboardVirtualizationHost>());
                Assert.True(SmoothScrollCompositor.IsActive(viewer));
                Assert.Equal(1, began);

                foreach (var target in new[] { 12.0, 35.0, 58.0 })
                {
                    Assert.True(SmoothScrollCompositor.TryApply(viewer, 0, target, false, out _, out var committed));
                    viewer.UpdateLayout();
                    Assert.InRange(Math.Abs(committed - target), 0, 1);
                    Assert.InRange(Math.Abs(viewer.VerticalOffset - target), 0, 1);
                    Assert.Same(originalTransform, content.RenderTransform);
                    Assert.Equal(originalHitTest, content.IsHitTestVisible);
                    Assert.False(SmoothScrollCompositor.TryGetVerticalDrift(viewer, out _));
                }

                SmoothScrollCompositor.End(viewer, 0, 72);
                viewer.UpdateLayout();
                Assert.InRange(Math.Abs(viewer.VerticalOffset - 72), 0, 1);
                Assert.False(SmoothScrollCompositor.IsActive(viewer));
                Assert.Equal(1, ended);
                Assert.Same(originalTransform, content.RenderTransform);
            }
            finally
            {
                SmoothScrollCompositor.End(viewer, 0, viewer.VerticalOffset);
                SmoothScrollCompositor.UnregisterSessionLifecycle(viewer, OnBegan, OnEnded);
                SmoothScrollCompositor.SetDirectOffsetMode(viewer, false);
            }
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "STA scenario timed out");
        if (failure is not null)
            throw failure;
    }
}
