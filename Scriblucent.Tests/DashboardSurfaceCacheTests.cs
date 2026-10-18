using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class DashboardSurfaceCacheTests
{
    [Fact]
    public void Repeated_warmup_of_ready_cards_keeps_idle_release_armed() => WithCards(12, (viewer, cards) =>
    {
        DashboardScrollSurfaceCache.Prewarm(viewer, cards); Pump();
        Assert.Equal(12, DashboardScrollSurfaceCache.AppliedCount);
        for (int i = 0; i < 8; i++) DashboardScrollSurfaceCache.Prewarm(viewer, cards);
        Assert.False(Field<bool>("_prewarmActive"), "A warmup with nothing new queued must finish.");
        Assert.True(Field<DispatcherTimer>("_idleReleaseTimer").IsEnabled, "Already-ready caches must still expire.");
        Field<DispatcherTimer>("_idleReleaseTimer").Interval = TimeSpan.FromMilliseconds(20);
        Pump();
        Assert.Equal(0, DashboardScrollSurfaceCache.AppliedCount);
    });

    [Fact]
    public void The_cache_budget_includes_queued_surfaces_and_duplicate_warmup_requests() => WithCards(100, (viewer, cards) =>
    {
        for (int i = 0; i < 8; i++) DashboardScrollSurfaceCache.Prewarm(viewer, cards);
        Assert.InRange(DashboardScrollSurfaceCache.PendingCount, 1, 64);
        Pump();
        Assert.InRange(DashboardScrollSurfaceCache.AppliedCount, 1, 64);
        Assert.Equal(0, DashboardScrollSurfaceCache.PendingCount);
    });

    [Fact]
    public void Unloaded_cards_release_their_cache_slots_without_waiting_for_an_idle_timer() => WithCards(12, (viewer, cards) =>
    {
        DashboardScrollSurfaceCache.Prewarm(viewer, cards); Pump();
        Assert.Equal(12, DashboardScrollSurfaceCache.AppliedCount);
        ((Panel)viewer.Content).Children.Clear(); Pump();
        Assert.Equal(0, DashboardScrollSurfaceCache.AppliedCount);
        Assert.All(cards, card => Assert.Null(card.CacheMode));
    });

    [Fact]
    public void Scroll_reclaims_ready_caches_and_staggered_release_respects_other_cache_owners() => WithCards(12, (viewer, cards) =>
    {
        DashboardScrollSurfaceCache.Prewarm(viewer, cards); Pump();
        var ready = cards.Select(c => c.CacheMode).ToArray();
        DashboardScrollSurfaceCache.BeginSession(viewer); Pump();
        Assert.False(Field<DispatcherTimer>("_idleReleaseTimer").IsEnabled);
        Assert.Equal(ready, cards.Select(c => c.CacheMode).ToArray());
        var replacement = new System.Windows.Media.BitmapCache();
        cards[0].CacheMode = replacement;
        DashboardScrollSurfaceCache.EndSessionStaggered(); Pump();
        Assert.Equal(0, DashboardScrollSurfaceCache.AppliedCount);
        Assert.Same(replacement, cards[0].CacheMode);
        Assert.All(cards.Skip(1), card => Assert.Null(card.CacheMode));
    });

    private static T Field<T>(string name) => (T)typeof(DashboardScrollSurfaceCache).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void WithCards(int count, Action<ScrollViewer, FrameworkElement[]> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var canvas = new Canvas { Width = 1200, Height = 850 };
                var cards = Enumerable.Range(0, count).Select(i => new Border { Width = 30, Height = 30 }).ToArray();
                foreach (var card in cards) canvas.Children.Add(card);
                var viewer = new ScrollViewer { Content = canvas };
                window = new Window { Width = 1280, Height = 900, Content = viewer, ShowInTaskbar = false };
                window.Show(); Pump();
                Assert.True(viewer.ViewportHeight >= 760);
                action(viewer, cards);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                DashboardScrollSurfaceCache.EndSession();
                window?.Close();
                // Each test owns its STA dispatcher; don't carry that dispatcher into another test.
                typeof(DashboardScrollSurfaceCache).GetField("_idleReleaseTimer", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
