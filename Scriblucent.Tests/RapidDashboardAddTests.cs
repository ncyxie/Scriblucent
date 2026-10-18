using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.Models;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class RapidDashboardAddTests
{
    [Fact]
    public void The_first_note_reveals_dashboard_content_without_waiting_for_the_mutation_timer() => Sta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var vm = new MainViewModel(new AppSettings { EnableAutoSave = false, EnableAiFeatures = false }, false, false)
            { PersistenceDirectoryOverride = Path.Combine(Path.GetTempPath(), "Scriblucent-add-test", Guid.NewGuid().ToString("N")) };
        // This verifies immediate UI notifications without starting a disk write.
        typeof(MainViewModel).GetField("_saveNotesQueued", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.AddNoteFromDocument(new NoteDocument());
        Assert.Contains(nameof(MainViewModel.ShouldShowNotesDashboardContent), changed);
        Assert.Contains(nameof(MainViewModel.ShowNotesEmptyDashboard), changed);
        Assert.True(vm.ShouldShowNotesDashboardContent);
        if (typeof(MainViewModel).GetField("_notesMutationReactionTimer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(vm) is DispatcherTimer reactionTimer) reactionTimer.Stop();
    });

    [Fact]
    public void A_new_click_lands_the_previous_arrival_even_when_its_clock_was_cancelled() => Sta(() =>
    {
        var previous = new NoteDocument(); var next = Guid.NewGuid();
        var card = new Border { DataContext = new NoteCardViewModel(previous, _ => { }), Opacity = .6, IsHitTestVisible = false };
        using var host = new Host(card);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(card);
        scale.ScaleX = scale.ScaleY = .98; translate.Y = 6;
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        engine.PreparePendingEnter(previous.Id);
        ((Dictionary<Guid, FrameworkElement>)typeof(DashboardMotionEngine).GetField("_layoutRootByItemId", flags)!.GetValue(engine)!)[previous.Id] = card;
        ((HashSet<Guid>)typeof(DashboardMotionEngine).GetField("_playingEnterIds", flags)!.GetValue(engine)!).Add(previous.Id);
        engine.PreparePendingEnter(next);
        Assert.Equal(1, card.Opacity); Assert.True(card.IsHitTestVisible);
        Assert.True(card.RenderTransform.Value.IsIdentity);
        var entering = (HashSet<Guid>)typeof(DashboardMotionEngine).GetField("_enteringItemIds", flags)!.GetValue(engine)!;
        Assert.Equal(next, Assert.Single(entering));
    });

    [Fact]
    public void A_new_click_preserves_the_previous_arrival_when_its_presence_clock_is_live() => Sta(() =>
    {
        var document = new NoteDocument();
        var card = new Border { DataContext = new NoteCardViewModel(document, _ => { }), Opacity = .4 };
        using var host = new Host(card);
        SurfaceMotion.AnimateTracks(card, MotionChannel.Presence,
            [new(card, UIElement.OpacityProperty, 1)], 280, UiMotion.InOutSine);
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        engine.PreparePendingEnter(document.Id);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ((Dictionary<Guid, FrameworkElement>)typeof(DashboardMotionEngine).GetField("_layoutRootByItemId", flags)!.GetValue(engine)!)[document.Id] = card;
        engine.PreparePendingEnter(Guid.NewGuid());
        Assert.True(NativeSurfaceMotion.IsRunning(card, MotionChannel.Presence));
        Assert.InRange(card.Opacity, .39, .41);
        Assert.Contains(document.Id, (HashSet<Guid>)typeof(DashboardMotionEngine).GetField("_enteringItemIds", flags)!.GetValue(engine)!);
    });

    [Fact]
    public void Repeated_dashboard_insertions_retarget_live_glides_instead_of_disabling_them() => Sta(() =>
    {
        var items = new ObservableCollection<FlashcardSetViewModel>();
        var original = new FlashcardSetViewModel(new FlashcardSetDocument());
        items.Add(original);
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetBinding(FrameworkElement.TagProperty, new Binding());
        outer.SetValue(FrameworkElement.WidthProperty, 100d);
        outer.SetValue(FrameworkElement.HeightProperty, 80d);
        outer.AppendChild(new FrameworkElementFactory(typeof(Border)));
        var grid = new DashboardVirtualizedCardGrid { ItemsSource = items, Width = 560,
            ItemCellSize = new Size(120, 100), ItemTemplate = new DataTemplate { VisualTree = outer } };
        using var host = new Host(grid);
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Flashcards);
        engine.Bind(grid, grid, grid);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clocks = (ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion)
            .GetField("Clocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var key = typeof(DashboardMotionEngine).GetField("_reflowBatchKey", flags)!.GetValue(engine)!;
        var translations = (ConditionalWeakTable<FrameworkElement, TranslateTransform>)typeof(DashboardMotionEngine)
            .GetField("ReflowTranslations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        for (var click = 1; click <= 8; click++)
        {
            var card = DashboardMotionCardCatalog.EnumerateLayoutRoots(grid, DashboardMotionScope.Flashcards)
                .Single(root => ((FlashcardSetViewModel)root.Tag).Document.Id == original.Document.Id);
            var before = card.TranslatePoint(new Point(), grid);
            engine.CapturePositions(freezeViewport: false);
            var next = new FlashcardSetViewModel(new FlashcardSetDocument());
            engine.PreparePendingEnter(next.Document.Id);
            items.Insert(0, next); grid.UpdateLayout();
            typeof(DashboardMotionEngine).GetMethod("AnimateSiblingReflow", flags)!
                .Invoke(engine, [new HashSet<Guid> { next.Document.Id }, null]);
            grid.UpdateLayout();
            card = DashboardMotionCardCatalog.EnumerateLayoutRoots(grid, DashboardMotionScope.Flashcards)
                .Single(root => ((FlashcardSetViewModel)root.Tag).Document.Id == original.Document.Id);
            var arrivals = DashboardMotionCardCatalog.EnumerateLayoutRoots(grid, DashboardMotionScope.Flashcards).ToArray();
            var first = arrivals.Single(root => ((FlashcardSetViewModel)root.Tag).Document.Id == items[0].Document.Id);
            var second = arrivals.Single(root => ((FlashcardSetViewModel)root.Tag).Document.Id == items[1].Document.Id);
            Assert.True(second.TranslatePoint(new Point(), grid).X - first.TranslatePoint(new Point(), grid).X >= 119);
            Assert.True(translations.TryGetValue(card, out var offset));
            Assert.True(Math.Abs(offset.X) > .5 || Math.Abs(offset.Y) > .5);
            // A row handoff deliberately stays local; same-row retargets preserve position.
            if (click % 4 != 0)
                Assert.True((before - card.TranslatePoint(new Point(), grid)).Length <= 1,
                    $"click={click}; before={before}; after={card.TranslatePoint(new Point(), grid)}; offset={offset.X},{offset.Y}; snapshots=" +
                    string.Join(" | ", ((Dictionary<Guid, Point>)typeof(DashboardMotionEngine).GetField("_positionSnapshots", flags)!.GetValue(engine)!).Values));
            Assert.True(clocks.TryGetValue(key, out var clock));
            var start = new Vector(offset.X, offset.Y);
            typeof(RenderTransitionClock).GetMethod("Advance", flags)!.Invoke(clock, [55d]);
            grid.UpdateLayout();
            Assert.True(new Vector(offset.X, offset.Y).Length < start.Length);
        }
        Assert.True(clocks.TryGetValue(key, out var last));
        typeof(RenderTransitionClock).GetMethod("Advance", flags)!.Invoke(last, [10000d]);
        foreach (var card in DashboardMotionCardCatalog.EnumerateLayoutRoots(grid, DashboardMotionScope.Flashcards))
            if (translations.TryGetValue(card, out var offset))
                Assert.Equal(new Vector(), new Vector(offset.X, offset.Y));
    });

    [Fact]
    public void A_transparent_arrival_still_has_a_visible_slot_for_sibling_reflow() => Sta(() =>
    {
        var card = new Border { Width = 220, Height = 140, Opacity = 0 };
        var viewer = new ScrollViewer { Content = card };
        using var host = new Host(viewer);
        Assert.False(DashboardMotionViewport.IntersectsViewport(card, viewer));
        Assert.True(DashboardMotionViewport.IntersectsViewport(card, viewer, includeTransparent: true));
    });

    [Fact]
    public void Watchdog_settles_the_pass_before_retiring_callbacks_can_discard_it() => Sta(() =>
    {
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var force = typeof(DashboardMotionEngine).GetField("_forceFinishPass", flags)!;
        var called = false;
        force.SetValue(engine, (Action)(() => called = true));
        var pending = (Dictionary<Guid, Action>)typeof(DashboardMotionEngine).GetField("_pendingEnterCompletions", flags)!.GetValue(engine)!;
        pending[Guid.NewGuid()] = () => force.SetValue(engine, null);
        typeof(DashboardMotionEngine).GetMethod("ForceFinishStuckEnters", flags)!.Invoke(engine, null);
        Assert.True(called);
    });

    [Fact]
    public void An_opaque_orphan_returns_to_its_slot_instead_of_only_losing_its_entering_id() => Sta(() =>
    {
        var document = new NoteDocument();
        var card = new Border { DataContext = new NoteCardViewModel(document, _ => { }), Opacity = 1 };
        using var host = new Host(card);
        UiMotion.EnsureScaleTranslateGroup(card).Translate.X = -244;
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        engine.PreparePendingEnter(document.Id);
        ((Dictionary<Guid, FrameworkElement>)typeof(DashboardMotionEngine).GetField("_layoutRootByItemId", flags)!.GetValue(engine)!)[document.Id] = card;
        typeof(DashboardMotionEngine).GetMethod("FinalizeOrphanedEnteringCards", flags)!.Invoke(engine, null);
        Assert.True(card.RenderTransform.Value.IsIdentity);
    });

    [Fact]
    public void Compact_arrival_styling_stays_latched_until_input_pauses() => Sta(() =>
    {
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var offsets = (Dictionary<Guid, Vector>)typeof(DashboardMotionEngine).GetField("_interruptedReflowOffsets", flags)!.GetValue(engine)!;
        var burst = typeof(DashboardMotionEngine).GetMethod("UseCompactAddArrival", flags)!;
        engine.PreparePendingEnter(Guid.NewGuid());
        Assert.False((bool)burst.Invoke(engine, null)!);
        offsets[Guid.NewGuid()] = new Vector(-120, 0);
        engine.PreparePendingEnter(Guid.NewGuid());
        Assert.True((bool)burst.Invoke(engine, null)!);
        offsets.Clear();
        engine.PreparePendingEnter(Guid.NewGuid());
        Assert.True((bool)burst.Invoke(engine, null)!);
        typeof(DashboardMotionEngine).GetField("_lastAddTimestampMs", flags)!.SetValue(engine, Environment.TickCount64 - 501);
        engine.PreparePendingEnter(Guid.NewGuid());
        Assert.False((bool)burst.Invoke(engine, null)!);
    });

    [Fact]
    public void A_pending_add_invalidates_the_lookup_before_its_container_is_prepared() => Sta(() =>
    {
        var grid = new DashboardVirtualizedCardGrid();
        var first = new NoteDocument(); var next = new NoteDocument();
        grid.Items.Add(new NoteCardViewModel(first, _ => { }));
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        engine.Bind(grid, grid, grid);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lookup = typeof(DashboardMotionEngine).GetMethod("EnterItemLookup", flags)!;
        var before = (Dictionary<Guid, (DashboardVirtualizedCardGrid, object)>)lookup.Invoke(engine, null)!;
        Assert.DoesNotContain(next.Id, before.Keys);
        engine.PreparePendingEnter(next.Id);
        grid.Items.Add(new NoteCardViewModel(next, _ => { }));
        var after = (Dictionary<Guid, (DashboardVirtualizedCardGrid, object)>)lookup.Invoke(engine, null)!;
        Assert.Contains(next.Id, after.Keys);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(40)]
    public void Instant_entries_do_not_bypass_the_visible_entrance_budget(int instantCount) => Sta(() =>
    {
        var grid = new DashboardVirtualizedCardGrid { ItemCellSize = new Size(244, 164), Width = 500 };
        var ids = new List<Guid>();
        for (var index = 0; index < 40; index++)
        {
            var document = new NoteDocument(); ids.Add(document.Id);
            grid.Items.Add(new NoteCardViewModel(document, _ => { }));
        }
        using var host = new Host(grid);
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        engine.Bind(grid, grid, grid);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var instant = (HashSet<Guid>)typeof(DashboardMotionEngine).GetField("_instantEnterIds", flags)!.GetValue(engine)!;
        instant.UnionWith(ids.Take(instantCount));
        var animated = (IReadOnlyList<Guid>)typeof(DashboardMotionEngine).GetMethod("ResolveAnimatedEnterIds", flags)!
            .Invoke(engine, [ids])!;
        Assert.Equal(Math.Min(40 - instantCount, UiMotion.MaxBulkAnimatedEnters), animated.Count);
        Assert.DoesNotContain(animated, id => ids.Take(instantCount).Contains(id));
        Assert.All(ids.Except(animated), id => Assert.Contains(id, instant));
    });

    [Fact]
    public void Completing_an_entrance_keeps_an_overlapping_reflow_at_its_current_position() => Sta(() =>
    {
        var card = new Border { Opacity = .4 };
        var (scale, presence) = UiMotion.EnsureScaleTranslateGroup(card);
        scale.ScaleX = scale.ScaleY = .98; presence.Y = 3;
        var layout = UiMotion.ChannelTranslate(card, MotionChannel.Layout);
        layout.X = -80; layout.Y = -5;
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var translations = (ConditionalWeakTable<FrameworkElement, TranslateTransform>)typeof(DashboardMotionEngine)
            .GetField("ReflowTranslations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        translations.Add(card, layout);
        ((HashSet<FrameworkElement>)typeof(DashboardMotionEngine).GetField("_activeReflowTargets", flags)!.GetValue(engine)!).Add(card);
        typeof(DashboardMotionEngine).GetMethod("FinalizeEnteredCard", flags)!.Invoke(engine, [card, card]);
        Assert.Equal(-80, layout.X); Assert.Equal(-5, layout.Y);
        Assert.Equal(0, presence.Y); Assert.Equal(1, scale.ScaleX); Assert.Equal(1, card.Opacity);
    });

    [Fact]
    public void Retargeting_captures_the_current_pose_and_coalesced_adds_keep_that_snapshot() => Sta(() =>
    {
        var document = new NoteDocument();
        var card = new Border { DataContext = new NoteCardViewModel(document, _ => { }), Opacity = .45 };
        UiMotion.EnsureScaleTranslateGroup(card);
        var translate = UiMotion.ChannelTranslate(card, MotionChannel.Layout);
        translate.X = -60; translate.Y = -4;
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var translations = (ConditionalWeakTable<FrameworkElement, TranslateTransform>)typeof(DashboardMotionEngine)
            .GetField("ReflowTranslations", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        translations.Add(card, translate);
        ((HashSet<FrameworkElement>)typeof(DashboardMotionEngine).GetField("_activeReflowTargets", flags)!.GetValue(engine)!).Add(card);
        ((Dictionary<FrameworkElement, double>)typeof(DashboardMotionEngine).GetField("_reflowOpacityTargets", flags)!.GetValue(engine)!)[card] = 1;
        engine.CapturePositions(freezeViewport: false);
        var offsets = (Dictionary<Guid, Vector>)typeof(DashboardMotionEngine).GetField("_interruptedReflowOffsets", flags)!.GetValue(engine)!;
        var opacity = (Dictionary<Guid, double>)typeof(DashboardMotionEngine).GetField("_interruptedReflowOpacities", flags)!.GetValue(engine)!;
        Assert.Equal(new Vector(-60, -4), offsets[document.Id]);
        Assert.Equal(.45, opacity[document.Id]);
        Assert.Equal(0, translate.X); Assert.Equal(0, translate.Y); Assert.Equal(1, card.Opacity);
        typeof(DashboardMotionEngine).GetField("_reflowScheduled", flags)!.SetValue(engine, true);
        engine.CapturePositions(freezeViewport: false);
        Assert.Equal(new Vector(-60, -4), offsets[document.Id]);
        Assert.Equal(.45, opacity[document.Id]);
    });

    [Fact]
    public void Interrupted_row_handoff_restores_opacity_without_cancelling_presence() => Sta(() =>
    {
        var card = new Border { Width = 220, Height = 140, Opacity = .2 };
        using var host = new Host(card);
        var scale = new ScaleTransform(.98, .98); card.RenderTransform = scale;
        SurfaceMotion.AnimateTracks(card, MotionChannel.Presence,
            [new(scale, ScaleTransform.ScaleXProperty, 1, true)], 280, UiMotion.InOutSine);
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var roots = (HashSet<FrameworkElement>)typeof(DashboardMotionEngine).GetField("_activeReflowTargets", flags)!.GetValue(engine)!;
        var opacity = (Dictionary<FrameworkElement, double>)typeof(DashboardMotionEngine).GetField("_reflowOpacityTargets", flags)!.GetValue(engine)!;
        roots.Add(card); opacity[card] = 1;
        typeof(DashboardMotionEngine).GetMethod("CancelActiveReflowTransforms", flags)!.Invoke(engine, null);
        Assert.Equal(1, card.Opacity);
        Assert.Empty(roots); Assert.Empty(opacity);
        Assert.True(NativeSurfaceMotion.IsRunning(card, MotionChannel.Presence));
    });

    [Fact]
    public void Arrival_light_is_separate_from_hover_and_hidden_in_reduced_motion() => Sta(() =>
    {
        var face = new Grid { Width = 220, Height = 140 };
        using var host = new Host(face);
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        SurfaceLight.PulseArrival(face, false, 0);
        Assert.Empty(face.Children.Cast<UIElement>());
        MotionPolicy.Configure(MotionPreference.Automatic, false);
        SurfaceLight.PulseArrival(face, false, 0);
        var arrival = Assert.Single(face.Children.Cast<FrameworkElement>());
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
        Assert.True(NativeSurfaceMotion.IsRunning(arrival, MotionChannel.Presence));
        var hover = new List<NativeMotionTrack>(); SurfaceLight.AddTracks(face, hover, true);
        SurfaceMotion.AnimateTracks(face, MotionChannel.Interaction, hover, 160, UiMotion.OutCubic);
        Assert.Equal(2, face.Children.Count);
        Assert.True(NativeSurfaceMotion.IsRunning(arrival, MotionChannel.Presence));
        MotionPolicy.Configure(MotionPreference.Reduced, false);
        Assert.All(face.Children.Cast<FrameworkElement>(), light => Assert.Equal(Visibility.Hidden, light.Visibility));
        Assert.Equal(0, arrival.Opacity);
        Assert.False(NativeSurfaceMotion.IsRunning(arrival, MotionChannel.Presence));
    });

    [Fact]
    public void Interrupted_item_reflow_preserves_visual_position_then_settles_into_the_new_slot() => Sta(() =>
    {
        var items = new ObservableCollection<string> { "A", "B", "C", "D" };
        var list = new ItemsControl { ItemsSource = items, Width = 500 };
        var tile = new FrameworkElementFactory(typeof(Border));
        tile.SetValue(FrameworkElement.WidthProperty, 110d);
        tile.SetValue(FrameworkElement.HeightProperty, 80d);
        list.ItemTemplate = new DataTemplate { VisualTree = tile };
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        using var host = new Host(list);
        var before = LayoutFlipTransition.Capture(list);
        Assert.Equal(4, before.Count);
        var element = before[0].Element;
        items.Insert(0, "First add");
        LayoutFlipTransition.Play(list, before);
        Advance(element, 65);
        var interrupted = LayoutFlipTransition.Capture(list);
        var visualPoint = element.TranslatePoint(new Point(), list);
        items.Insert(0, "Second add");
        LayoutFlipTransition.Play(list, interrupted);
        var resumedPoint = element.TranslatePoint(new Point(), list);
        Assert.InRange((visualPoint - resumedPoint).Length, 0, .001);
        foreach (var position in interrupted) Advance(position.Element, 10000);
        foreach (var position in interrupted)
        {
            var offset = UiMotion.ChannelTranslate(position.Element, MotionChannel.Layout);
            Assert.Equal(0, offset.X);
            Assert.Equal(0, offset.Y);
        }
    });

    [Fact]
    public void Section_reveal_does_not_animate_containers_owned_by_dashboard_reflow() => Sta(() =>
    {
        var grid = new DashboardVirtualizedCardGrid { ItemCellSize = new Size(120, 100), Width = 500 };
        grid.Items.Add("A"); grid.Items.Add("B");
        using var host = new Host(grid);
        Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Empty(LayoutFlipTransition.Capture(grid));
    });

    [Theory]
    [InlineData(false, 1, 1, false, 0, 0)] // Appending leaves existing slots still.
    [InlineData(false, 1, 2, true, -244, 0)] // A real shift within a row slides normally.
    [InlineData(false, 3, 4, true, 0, -14)] // Wrapping never sweeps diagonally through the grid.
    [InlineData(false, 0, 8, true, 0, -14)] // Bulk insertion also stays local across rows.
    [InlineData(true, 0, 1, true, 0, -96)] // List insertion retains its vertical reflow.
    public void Add_reflow_respects_actual_slots_and_row_boundaries(bool list, int oldIndex, int newIndex,
        bool moves, double x, double y) => Sta(() =>
    {
        var engine = new DashboardMotionEngine(Dispatcher.CurrentDispatcher, DashboardMotionScope.Notes);
        var id = Guid.NewGuid();
        var snapshots = (Dictionary<Guid, int>)typeof(DashboardMotionEngine)
            .GetField("_indexSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        snapshots[id] = oldIndex;
        object?[] args = [id, new Dictionary<Guid, int> { [id] = newIndex },
            new DashboardMotionViewport.GridReflowMetrics(list, 244, 164, 4), 0d, 0d];
        var result = (bool)typeof(DashboardMotionEngine).GetMethod("TryComputeAddReflowDelta",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, args)!;
        Assert.Equal(moves, result);
        Assert.Equal(x, (double)args[3]!); Assert.Equal(y, (double)args[4]!);
    });

    private static void Advance(FrameworkElement element, double ms)
    {
        var clocks = (ConditionalWeakTable<object, RenderTransitionClock>)typeof(UiMotion)
            .GetField("Clocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var key = UiMotion.Channel(element, MotionChannel.Layout);
        if (clocks.TryGetValue(key, out var clock))
            typeof(RenderTransitionClock).GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(clock, [ms]);
    }

    private sealed class Host : IDisposable
    {
        private readonly Window _window;
        internal Host(FrameworkElement content)
        {
            _window = new Window { Content = content, Width = 620, Height = 480, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false };
            _window.Show(); _window.UpdateLayout();
        }
        public void Dispose() => _window.Close();
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception ex) { failure = ex; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
