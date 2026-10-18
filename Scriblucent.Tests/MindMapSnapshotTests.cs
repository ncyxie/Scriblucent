using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Rendering;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MindMapSnapshotTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SkiaMindMapSnapshotCache.Clear(); action(); }
            catch (Exception error) { failure = error; }
            finally { SkiaMindMapSnapshotCache.Clear(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Until(Func<bool> condition)
    {
        var wait = Stopwatch.StartNew();
        while (!condition() && wait.ElapsedMilliseconds < 5000)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(condition(), "Snapshot subscribers did not complete.");
    }
    private static MindMapTileSnapshot Snapshot(Guid id, uint color) => new()
    {
        MapId = id, Revision = 1, CanvasWidthDip = 60, CanvasHeightDip = 30, Edges = [],
        Nodes = [new MindMapTileNode { X = 0, Y = 0, Width = 60, Height = 30, Background = color,
            Border = 0, OuterContour = "M0,0L60,0 60,30 0,30Z", BorderContour = "", Glyphs = [], Metadata = [] }]
    };
    private static byte[] Pixel(BitmapSource image)
    {
        var bytes = new byte[4]; image.CopyPixels(new Int32Rect(15, 15, 1, 1), bytes, 4, 0); return bytes;
    }
    [Fact]
    public void Maps_with_the_same_revision_cannot_share_each_others_picture() => Sta(() =>
    {
        SkiaMindMapSnapshotCache.SnapshotResult? red = null, blue = null;
        SkiaMindMapSnapshotCache.Request(Snapshot(Guid.NewGuid(), 0xFFFF0000), 1, 1, result => red = result);
        SkiaMindMapSnapshotCache.Request(Snapshot(Guid.NewGuid(), 0xFF0000FF), 1, 1, result => blue = result);
        Until(() => red is not null && blue is not null);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(red!.Image));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(blue!.Image));
    });
    [Fact]
    public void Duplicate_requests_complete_every_subscriber_and_reuse_the_frozen_picture() => Sta(() =>
    {
        var snapshot = Snapshot(Guid.NewGuid(), 0xFF406080);
        var results = new List<SkiaMindMapSnapshotCache.SnapshotResult?>();
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, results.Add);
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, results.Add);
        Until(() => results.Count == 2);
        Assert.All(results, result => Assert.NotNull(result)); Assert.Same(results[0], results[1]);
        Assert.True(results[0]!.Image.IsFrozen);
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, results.Add);
        Assert.Equal(3, results.Count); Assert.Same(results[0], results[2]);
    });
    [Fact]
    public void Dpi_is_part_of_the_cache_identity() => Sta(() =>
    {
        var snapshot = Snapshot(Guid.NewGuid(), 0xFF406080);
        SkiaMindMapSnapshotCache.SnapshotResult? first = null, second = null;
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1, result => first = result); Until(() => first is not null);
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, result => second = result); Until(() => second is not null);
        Assert.Equal(60, first!.Image.PixelWidth); Assert.Equal(75, second!.Image.PixelWidth);
        Assert.Equal(96, first.Image.DpiX); Assert.Equal(120, second.Image.DpiX);
    });
    [Fact]
    public void Disposing_a_map_completes_waiters_and_rejects_its_late_render() => Sta(() =>
    {
        var id = Guid.NewGuid(); var old = Snapshot(id, 0xFFFF0000);
        var canceled = new List<SkiaMindMapSnapshotCache.SnapshotResult?>();
        SkiaMindMapSnapshotCache.Request(old, 1, 1, canceled.Add);
        SkiaMindMapSnapshotCache.Request(old, 1, 1, canceled.Add);
        SkiaMindMapSnapshotCache.Release(id);
        Assert.Equal(2, canceled.Count); Assert.All(canceled, result => Assert.Null(result));
        SkiaMindMapSnapshotCache.SnapshotResult? fresh = null;
        SkiaMindMapSnapshotCache.Request(Snapshot(id, 0xFF0000FF), 1, 1, result => fresh = result);
        Until(() => fresh is not null);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(fresh!.Image)); Assert.Equal(2, canceled.Count);
    });
    [Fact]
    public void A_worker_failure_notifies_every_waiter_so_the_live_canvas_can_return() => Sta(() =>
    {
        var broken = Snapshot(Guid.NewGuid(), 0xFFFF0000); broken.Nodes = null!;
        var results = new List<SkiaMindMapSnapshotCache.SnapshotResult?>();
        SkiaMindMapSnapshotCache.Request(broken, 1, 1, results.Add);
        SkiaMindMapSnapshotCache.Request(broken, 1, 1, results.Add);
        Until(() => results.Count == 2); Assert.All(results, result => Assert.Null(result));
    });
}
