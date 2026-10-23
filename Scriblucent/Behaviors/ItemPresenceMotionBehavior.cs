using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Animate semantic list mutations, never recycled containers or scroll realization.</summary>
public static class ItemPresenceMotionBehavior
{
    private sealed class State
    {
        internal bool Attached, Queued;
        internal readonly HashSet<object> Seen = new();
        internal List<LayoutFlipTransition.Position> Previous = new();
        internal readonly Dictionary<FrameworkElement, object> Identities = new();
        internal NotifyCollectionChangedEventHandler? Changed;
        internal ScrollChangedEventHandler? Scrolled;
        internal ExitGhost? Ghost;
    }
    private static readonly ConditionalWeakTable<ItemsControl, State> States = new();
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ItemPresenceMotionBehavior), new PropertyMetadata(false, OnEnabled));
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty EnterDurationProperty = DependencyProperty.RegisterAttached(
        "EnterDuration", typeof(int), typeof(ItemPresenceMotionBehavior), new PropertyMetadata(220));
    public static void SetEnterDuration(DependencyObject d, int value) => d.SetValue(EnterDurationProperty, value);
    public static int GetEnterDuration(DependencyObject d) => (int)d.GetValue(EnterDurationProperty);
    public static readonly DependencyProperty RiseProperty = DependencyProperty.RegisterAttached(
        "Rise", typeof(double), typeof(ItemPresenceMotionBehavior), new PropertyMetadata(8.0));
    public static void SetRise(DependencyObject d, double value) => d.SetValue(RiseProperty, value);
    public static double GetRise(DependencyObject d) => (double)d.GetValue(RiseProperty);
    public static readonly DependencyProperty EntryOffsetXProperty = DependencyProperty.RegisterAttached(
        "EntryOffsetX", typeof(double), typeof(ItemPresenceMotionBehavior), new PropertyMetadata(0d));
    public static void SetEntryOffsetX(DependencyObject target, double value) => target.SetValue(EntryOffsetXProperty, value);
    public static double GetEntryOffsetX(DependencyObject target) => (double)target.GetValue(EntryOffsetXProperty);
    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.Loaded -= Loaded;
        list.Unloaded -= Unloaded;
        if ((bool)e.NewValue)
        {
            list.Loaded += Loaded;
            list.Unloaded += Unloaded;
            if (list.IsLoaded) Attach(list);
        }
        else Detach(list);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Attach((ItemsControl)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Detach((ItemsControl)sender);
    private static object Identity(object item) => item.GetType().GetProperty("Id")?.GetValue(item) ?? item;
    private static void Attach(ItemsControl list)
    {
        var state = States.GetValue(list, _ => new());
        if (state.Attached) return;
        state.Attached = true;
        state.Changed = (_, args) => Changed(list, state, args);
        ((INotifyCollectionChanged)list.Items).CollectionChanged += state.Changed;
        state.Scrolled = (_, _) => Snapshot(list, state);
        list.AddHandler(ScrollViewer.ScrollChangedEvent, state.Scrolled);
        list.Dispatcher.BeginInvoke(() => { if (state.Attached) Snapshot(list, state); }, DispatcherPriority.Loaded);
    }
    private static void Detach(ItemsControl list)
    {
        if (!States.TryGetValue(list, out var state) || !state.Attached) return;
        ((INotifyCollectionChanged)list.Items).CollectionChanged -= state.Changed;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, state.Scrolled!);
        state.Attached = false;
        state.Queued = false;
        ReleaseGhost(list, state);
        foreach (var position in state.Previous) UiMotion.Cleanup(position.Element);
        state.Previous.Clear(); state.Identities.Clear(); state.Seen.Clear();
    }
    private static void Snapshot(ItemsControl list, State state)
    {
        if (state.Queued) return;
        state.Previous = LayoutFlipTransition.Capture(list);
        state.Identities.Clear();
        if (state.Seen.Count > 256) state.Seen.Clear();
        foreach (var p in state.Previous)
            if (p.Element.DataContext is { } item) { var key = Identity(item); state.Identities[p.Element] = key; state.Seen.Add(key); }
    }
    private static void Changed(ItemsControl list, State state, NotifyCollectionChangedEventArgs args)
    {
        if (state.Queued || !list.IsVisible) return;
        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is { } removed)
        {
            var old = removed.Cast<object>().Take(8).Select(Identity).ToHashSet();
            PlayExits(list, state, state.Identities.Values.Where(id => !old.Contains(id)).ToHashSet());
        }
        state.Queued = true;
        list.Dispatcher.BeginInvoke(() =>
        {
            if (!state.Attached || !list.IsLoaded) { state.Queued = false; return; }
            list.UpdateLayout();
            if (!UiMotion.SuppressChildEntrances)
            {
                var after = LayoutFlipTransition.Capture(list);
                LayoutFlipTransition.Play(list, state.Previous.Where(p => state.Identities.TryGetValue(p.Element, out var id)
                    && p.Element.DataContext is { } item && Equals(id, Identity(item))).ToList());
                var index = 0;
                foreach (var p in after)
                {
                    if (p.Element.DataContext is not { } item || state.Seen.Contains(Identity(item))) continue;
                    var element = p.Element;
                    var translate = UiMotion.ChannelTranslate(element, MotionChannel.Presence);
                    if (index >= MotionPolicy.PresenceItemBudget)
                    {
                        SurfaceMotion.Stop(element, MotionChannel.Presence);
                        element.Opacity = 1; translate.X = translate.Y = 0;
                        continue;
                    }
                    element.Opacity = 0;
                    translate.Y = MotionPolicy.Spatial ? GetRise(list) : 0;
                    translate.X = MotionPolicy.Spatial ? GetEntryOffsetX(list) : 0;
                    SurfaceMotion.AnimateTracks(element, MotionChannel.Presence,
                        [new(element, UIElement.OpacityProperty, 1, Profile: new(UiMotion.OutSine, 180)),
                         new(translate, TranslateTransform.XProperty, 0, true),
                         new(translate, TranslateTransform.YProperty, 0, true)], GetEnterDuration(list), UiMotion.InOutSine,
                        cache: true, startDelayMs: Math.Min(90, index++ * 30));
                }
            }
            state.Queued = false;
            Snapshot(list, state);
        }, DispatcherPriority.Loaded);
    }
    private static void PlayExits(ItemsControl list, State state, HashSet<object> current)
    {
        ReleaseGhost(list, state);
        if (!MotionPolicy.Spatial || AdornerLayer.GetAdornerLayer(list) is not { } layer) return;
        var images = new List<(BitmapSource Image, Rect Bounds)>();
        double reserved = 0;
        foreach (var p in state.Previous)
        {
            if (images.Count >= MotionPolicy.PresenceItemBudget) break;
            if (!state.Identities.TryGetValue(p.Element, out var id) || current.Contains(id)) continue;
            var width = p.Element.ActualWidth; var height = p.Element.ActualHeight;
            if (width < 1 || height < 1) continue;
            var scale = Math.Min(1, Math.Sqrt(128_000 / (width * height)));
            var pixels = MotionCacheBudget.TryReservePixels(Math.Floor(width * scale) * Math.Floor(height * scale));
            if (pixels == 0) continue;
            try
            {
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(p.Element); bitmap.Freeze();
                images.Add((bitmap, new Rect(p.Point, new Size(width, height))));
                reserved += pixels;
            }
            catch (InvalidOperationException) { MotionCacheBudget.Release(pixels); }
        }
        if (images.Count == 0) return;
        var ghost = new ExitGhost(list, images) { IsHitTestVisible = false, ReservedPixels = reserved };
        state.Ghost = ghost;
        layer.Add(ghost);
        SurfaceMotion.Animate(ghost, 0, 120, UiMotion.InQuad, false, () => ReleaseGhost(list, state));
    }
    private static void ReleaseGhost(ItemsControl list, State state)
    {
        if (state.Ghost is not { } ghost) return;
        SurfaceMotion.Stop(ghost);
        AdornerLayer.GetAdornerLayer(list)?.Remove(ghost);
        state.Ghost = null;
        MotionCacheBudget.Release(ghost.ReservedPixels);
    }
    private sealed class ExitGhost(UIElement element, List<(BitmapSource Image, Rect Bounds)> images) : Adorner(element)
    {
        internal double ReservedPixels;
        protected override void OnRender(DrawingContext dc)
        {
            foreach (var (image, bounds) in images) dc.DrawImage(image, bounds);
        }
    }
}
