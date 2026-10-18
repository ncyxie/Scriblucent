using System.Windows;
using Scriblucent.Behaviors;

namespace Scriblucent.Tests;

public class DragScrollTests
{
    [Fact]
    public void Edge_scroll_is_directional_gradual_and_bounded()
    {
        var viewport = new Rect(20, 40, 500, 600);
        double gentle = ItemDragScroll.TargetVelocity(new Point(200, viewport.Bottom - 70), viewport);
        double fast = ItemDragScroll.TargetVelocity(new Point(200, viewport.Bottom - 4), viewport);
        Assert.True(gentle > 0 && gentle < fast);
        Assert.InRange(fast, 0, 1200);
        Assert.Equal(-fast, ItemDragScroll.TargetVelocity(new Point(200, viewport.Top + 4), viewport), 8);
    }

    [Fact]
    public void Centre_and_other_columns_do_not_scroll()
    {
        var viewport = new Rect(20, 40, 500, 600);
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(200, 300), viewport));
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(-29, 639), viewport));
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(569, 639), viewport));
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(200, 300), viewport, 10));
        Assert.True(ItemDragScroll.TargetVelocity(new Point(19, 652), viewport) > 0);
        Assert.True(ItemDragScroll.TargetVelocity(new Point(521, 652), viewport) > 0);
        Assert.True(ItemDragScroll.TargetVelocity(new Point(viewport.Right + 100, viewport.Bottom + 100), viewport) > 0);
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(viewport.Right + 100, viewport.Bottom + 20), viewport));
    }

    [Fact]
    public void Pushing_past_viewport_and_window_keeps_accelerating_without_a_cutoff()
    {
        var viewport = new Rect(20, 40, 500, 600);
        double previous = 0;
        for (double distance = -70; distance <= 180; distance += 5)
        {
            double speed = ItemDragScroll.TargetVelocity(new Point(200, viewport.Bottom + distance), viewport);
            Assert.True(speed > previous, $"Speed stopped increasing at {distance} DIP outside: {previous} -> {speed}.");
            Assert.Equal(-speed, ItemDragScroll.TargetVelocity(new Point(200, viewport.Top - distance), viewport), 8);
            previous = speed;
        }
        Assert.True(ItemDragScroll.TargetVelocity(new Point(200, viewport.Bottom + 100), viewport)
            > ItemDragScroll.TargetVelocity(new Point(200, viewport.Bottom - 4), viewport) * 2);
        Assert.InRange(ItemDragScroll.TargetVelocity(new Point(200, 100000), viewport, 100), 1, 4200);
    }

    [Fact]
    public void Screen_edge_hold_accelerates_but_inward_placement_stays_precise()
    {
        var viewport = new Rect(20, 40, 500, 600);
        var edge = new Point(200, viewport.Bottom - 1);
        double initial = ItemDragScroll.TargetVelocity(edge, viewport);
        Assert.Equal(initial, ItemDragScroll.TargetVelocity(edge, viewport, .3));
        double held = ItemDragScroll.TargetVelocity(edge, viewport, 1.5);
        Assert.True(held > initial * 2);
        Assert.True(ItemDragScroll.TargetVelocity(edge, viewport, 3) > held);
        var precise = new Point(200, viewport.Bottom - 40);
        Assert.Equal(ItemDragScroll.TargetVelocity(precise, viewport), ItemDragScroll.TargetVelocity(precise, viewport, 3));
        Assert.Equal(-held, ItemDragScroll.TargetVelocity(new Point(200, viewport.Top + 1), viewport, 1.5), 8);
    }

    [Fact]
    public void Tiny_or_empty_viewports_have_no_overlapping_edge_bands()
    {
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(), Rect.Empty));
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(), new Rect(0, 0, 100, 0)));
        Assert.Equal(0, ItemDragScroll.TargetVelocity(new Point(50, 10), new Rect(0, 0, 100, 20)));
    }
}
