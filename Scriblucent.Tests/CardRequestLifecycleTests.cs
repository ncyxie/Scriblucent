using System.Diagnostics;
using System.Windows.Threading;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.ViewModels;
using SkiaSharp;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class CardRequestLifecycleTests
{
    private sealed class Renderer(int starters) : SkiaTileRenderer, IDisposable
    {
        internal readonly CountdownEvent Started = new(starters);
        internal readonly ManualResetEventSlim Gate = new(false);
        internal int Calls, Finished;
        public override string BuildSignature(object vm) => "same";
        public override SKBitmap Render(object vm, double dpi, float width, out double height)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call <= starters) Started.Signal();
            Assert.True(Gate.Wait(TimeSpan.FromSeconds(5)));
            height = 20;
            var bitmap = new SKBitmap(20, 20); bitmap.Erase(SKColors.Blue);
            Interlocked.Increment(ref Finished); return bitmap;
        }
        public override SKBitmap RenderPlaceholder(double dpi) => new(20, 20);
        public void Dispose() { Gate.Set(); }
    }

    [Fact]
    public void Releasing_one_consumer_preserves_a_shared_render_for_the_other() => Sta(() =>
    {
        using var renderer = new Renderer(1); var vm = new object();
        using var first = new CardTileRequestLease(); using var second = new CardTileRequestLease();
        int obsolete = 0, current = 0;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => obsolete++, 20, () => vm, first);
        Assert.True(renderer.Started.Wait(TimeSpan.FromSeconds(5)));
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => current++, 20,
            () => throw new Exception("Shared work was captured twice"), second);
        first.Dispose(); renderer.Gate.Set(); Until(() => current == 1);
        Assert.Equal(0, obsolete); Assert.Equal(1, renderer.Calls);
        Assert.NotNull(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
    });

    [Theory]
    [InlineData("Flashcard")] [InlineData("MindMap")] [InlineData("Quiz")]
    public void Listing_cards_replay_their_frozen_live_drawing_on_the_worker(string kind) => Sta(() =>
    {
        object model = kind switch { "Quiz" => new QuizViewModel(new QuizDocument()), "MindMap" => new MindMapViewModel(new MindMapDocument()), _ => new FlashcardSetViewModel(new FlashcardSetDocument()) };
        var renderer = kind switch { "Quiz" => SkiaTileRenderers.QuizUngrouped, "MindMap" => SkiaTileRenderers.MindMapUngrouped, _ => SkiaTileRenderers.FlashcardSetUngrouped };
        var drawing = new System.Windows.Media.DrawingGroup();
        drawing.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Blue, null,
            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 32, 20))));
        drawing.Freeze();
        var snapshot = new FrozenCardDrawing(drawing, 32, 20);
        System.Windows.Media.Imaging.BitmapSource? result = null;
        using var lease = new CardTileRequestLease();
        SkiaCardTileCache.GetReadyOrRequest(model, 1, renderer, source => result = source, 32, () => snapshot, lease);
        Until(() => result is not null);
        Assert.Equal(66, result!.PixelWidth); Assert.Equal(54, result.PixelHeight);
        var pixel = new byte[4]; result.CopyPixels(new System.Windows.Int32Rect(25, 25, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixel);
    });

    [Fact]
    public void Last_consumer_removes_a_queued_job_and_a_returning_card_can_request_it_again() => Sta(() =>
    {
        using var renderer = new Renderer(2);
        using var first = new CardTileRequestLease(); using var second = new CardTileRequestLease();
        SkiaCardTileCache.GetReadyOrRequest(new object(), 1, renderer, null, 20, () => 1, first);
        SkiaCardTileCache.GetReadyOrRequest(new object(), 1, renderer, null, 20, () => 2, second);
        Assert.True(renderer.Started.Wait(TimeSpan.FromSeconds(5)));
        var vm = new object(); using var abandoned = new CardTileRequestLease();
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => Assert.Fail("Cancelled callback"), 20, () => vm, abandoned);
        Assert.Equal(1, SkiaCardTileCache.PendingCount);
        abandoned.Dispose(); Assert.Equal(0, SkiaCardTileCache.PendingCount);
        renderer.Gate.Set(); Until(() => renderer.Finished == 2); Pump();
        Assert.Equal(2, renderer.Calls); Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
        using var returning = new CardTileRequestLease(); var ready = false;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => ready = true, 20, () => vm, returning);
        Until(() => ready); Assert.Equal(3, renderer.Calls);
    });

    [Fact]
    public void Cancelled_running_work_does_not_publish_or_repopulate_the_cache() => Sta(() =>
    {
        using var renderer = new Renderer(1); var vm = new object(); using var lease = new CardTileRequestLease();
        int callbacks = 0;
        SkiaCardTileCache.GetReadyOrRequest(vm, 1, renderer, _ => callbacks++, 20, () => vm, lease);
        Assert.True(renderer.Started.Wait(TimeSpan.FromSeconds(5)));
        lease.Dispose(); renderer.Gate.Set(); Until(() => renderer.Finished == 1); Pump();
        Assert.Equal(0, callbacks); Assert.Null(SkiaCardTileCache.GetReady(vm, 1, renderer, 20));
    });

    [Fact]
    public void Abandoned_note_preview_is_removed_before_it_decodes() => Sta(() =>
    {
        var note = new NoteCardViewModel(new NoteDocument { Content = "Not needed" }, _ => { });
        using var lease = new CardTileRequestLease();
        var before = NotePreviewWarmQueue.PendingCount;
        NotePreviewWarmQueue.Enqueue(note, () => Assert.Fail("Cancelled preview"), lease: lease);
        Assert.Equal(before + 1, NotePreviewWarmQueue.PendingCount);
        lease.Dispose(); Assert.Equal(before, NotePreviewWarmQueue.PendingCount);
        Pump(); Assert.False(note.IsContentPreviewCached);
    });

    [Fact]
    public void Replacing_a_search_before_idle_skips_obsolete_decoding_and_completes_the_current_search() => Sta(() =>
    {
        var old = new NoteCardViewModel(new NoteDocument { Content = "Old search" }, _ => { });
        var current = new NoteCardViewModel(new NoteDocument { Content = "Current search" }, _ => { });
        var generation = NotePreviewWarmQueue.BumpGeneration(); int ready = 0;
        NotePreviewWarmQueue.Enqueue(old, () => Assert.Fail("Obsolete search completed"), generation);
        generation = NotePreviewWarmQueue.BumpGeneration();
        NotePreviewWarmQueue.Enqueue(current, () => ready++, generation);
        Until(() => ready == 1);
        Assert.False(old.IsContentPreviewCached); Assert.True(current.IsContentPreviewCached);
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Tile_and_search_preview_consumers_cancel_independently(bool cancelTile) => Sta(() =>
    {
        var note = new NoteCardViewModel(new NoteDocument { Content = "Needed preview" }, _ => { });
        using var lease = new CardTileRequestLease(); int search = 0, tile = 0;
        var generation = NotePreviewWarmQueue.BumpGeneration();
        NotePreviewWarmQueue.Enqueue(note, () => search++, generation);
        NotePreviewWarmQueue.Enqueue(note, () => tile++, lease: lease);
        if (cancelTile) lease.Dispose(); else NotePreviewWarmQueue.BumpGeneration();
        Until(() => note.IsContentPreviewCached);
        Assert.Equal(cancelTile ? 1 : 0, search); Assert.Equal(cancelTile ? 0 : 1, tile);
    });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.SystemIdle);
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(1); }
        Assert.True(condition());
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SkiaCardTileCache.Clear(); action(); }
            catch (Exception error) { failure = error; }
            finally { SkiaCardTileCache.Clear(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
