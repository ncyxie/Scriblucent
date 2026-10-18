using Scriblucent.Animations;
using Scriblucent.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>Paint ready card tiles during a scroll session; keep cold cards live.</summary>
public static class ScrollTileBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollTileBehavior), new PropertyMetadata(false, EnabledChanged));
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty IsScrollingProperty = DependencyProperty.RegisterAttached(
        "IsScrolling", typeof(bool), typeof(ScrollTileBehavior), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsScrolling(DependencyObject element) => (bool)element.GetValue(IsScrollingProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(ScrollTileBehavior));

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        viewer.Loaded -= Loaded; viewer.Unloaded -= Unloaded;
        if (args.NewValue is true)
        { viewer.Loaded += Loaded; viewer.Unloaded += Unloaded; if (viewer.IsLoaded) Attach(viewer); }
        else Detach(viewer);
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Attach((ScrollViewer)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Detach((ScrollViewer)sender);
    private static void Attach(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is State) return;
        var state = new State(viewer); viewer.SetValue(StateProperty, state); state.Attach();
    }
    private static void Detach(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is not State state) return;
        state.Detach(); viewer.ClearValue(StateProperty);
    }
    internal static void RegisterHost(DashboardTileHost host)
    {
        for (DependencyObject? node = host; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer && viewer.GetValue(StateProperty) is State state)
            { state.Add(host); break; }
    }
    private static IEnumerable<DashboardTileHost> Hosts(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DashboardTileHost host) yield return host;
            else foreach (var descendant in Hosts(child)) yield return descendant;
        }
    }
    private sealed class State
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _settle;
        private readonly HashSet<DashboardTileHost> _hosts = new();
        private bool _active, _attached, _sessionOnly;
        private Window? _owner;
        internal State(ScrollViewer viewer)
        {
            _viewer = viewer;
            _settle = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _settle.Tick += Settled;
        }
        internal void Attach()
        {
            _attached = true;
            _viewer.ScrollChanged += Changed;
            _viewer.IsVisibleChanged += VisibilityChanged;
            SmoothScrollCompositor.RegisterSessionLifecycle(_viewer, Begin, End);
            _owner = Window.GetWindow(_viewer);
            if (_owner is not null) _owner.Deactivated += Deactivated;
            MotionPolicy.Changed += PolicyChanged;
            foreach (var host in Hosts(_viewer)) Add(host);
        }
        internal void Add(DashboardTileHost host)
        {
            _hosts.RemoveWhere(h => !h.IsLoaded);
            _hosts.Add(host);
            _sessionOnly |= host.PreserveLiveInputInBitmapMode;
            if (_active) CommandLightBehavior.SuspendScrollTargets(host, true);
            host.IsBitmapMode = _active;
        }
        private void Changed(object sender, ScrollChangedEventArgs args)
        {
            if (args.HorizontalChange == 0 && args.VerticalChange == 0) return;
            if (_sessionOnly) return;
            if (SmoothScrollCompositor.IsActive(_viewer)) return;
            if (!MotionPolicy.Spatial) { End(); return; }
            Begin(); _settle.Stop(); _settle.Start();
        }
        private void Begin()
        {
            if (_active || !_attached) return;
            Scriblucent.Rendering.FlashcardScrollTrace.Record(Scriblucent.Rendering.FlashcardScrollTrace.Kind.Begin, identity: _viewer);
            _active = true; _settle.Stop();
            _viewer.SetValue(IsScrollingProperty, true);
            // Only realized containers are walked once per gesture, not on every frame.
            _hosts.Clear(); foreach (var host in Hosts(_viewer)) _hosts.Add(host);
            foreach (var host in _hosts)
            {
                DashboardScrollRenderGate.ResetHoveredCards(host);
                CollectionSelectionFrame.For(host)?.ResetHover();
                ResetLift(host);
                CommandLightBehavior.SuspendScrollTargets(host, true);
                host.IsBitmapMode = true;
            }
        }
        private static void ResetLift(DependencyObject root)
        {
            if (root is SurfaceMaterialBorder { Parent: Grid element, ActualWidth: >= 300, ActualHeight: >= 200 })
            {
                UiMotion.Stop(UiMotion.Channel(element, MotionChannel.Interaction));
                if (UiMotion.ChannelTranslate(element, MotionChannel.Interaction) is { } translate) translate.Y = 0;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ResetLift(VisualTreeHelper.GetChild(root, i));
        }
        private void Settled(object? sender, EventArgs args) { _settle.Stop(); End(); }
        private void Deactivated(object? sender, EventArgs args) { SmoothScrollBehavior.Interrupt(_viewer); End(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        { if (args.NewValue is false) { SmoothScrollBehavior.Interrupt(_viewer); End(); } }
        private void PolicyChanged() { if (_viewer.Dispatcher.CheckAccess()) { SmoothScrollBehavior.Interrupt(_viewer); End(); } }
        private void End()
        {
            _settle.Stop();
            if (!_active) return;
            Scriblucent.Rendering.FlashcardScrollTrace.Record(Scriblucent.Rendering.FlashcardScrollTrace.Kind.End, identity: _viewer);
            _active = false; _viewer.SetValue(IsScrollingProperty, false);
            foreach (var host in _hosts.ToArray())
            {
                host.IsBitmapMode = false;
                if (host.IsLoaded)
                {
                    CommandLightBehavior.SuspendScrollTargets(host, false);
                    DashboardScrollRenderGate.RestorePointerHoverStates(host);
                    CollectionSelectionFrame.For(host)?.SyncHover();
                    if (EditorChromeContext.GetController(host) is Scriblucent.Views.FlashcardsPreviewWindow editor) editor.RestoreGridCardHover(host);
                    host.QueueWarm();
                }
            }
            _hosts.RemoveWhere(h => !h.IsLoaded);
        }
        internal void Detach()
        {
            End(); _attached = false;
            _viewer.ScrollChanged -= Changed;
            _viewer.IsVisibleChanged -= VisibilityChanged;
            SmoothScrollCompositor.UnregisterSessionLifecycle(_viewer, Begin, End);
            if (_owner is not null) _owner.Deactivated -= Deactivated;
            MotionPolicy.Changed -= PolicyChanged;
            _settle.Stop(); _hosts.Clear();
        }
    }
}
