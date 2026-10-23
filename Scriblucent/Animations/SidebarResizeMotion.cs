using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

/// <summary>The sidebar edge and dashboard viewport share one width clock, including interrupted reversals.</summary>
public static class SidebarResizeMotion
{
    public static readonly DependencyProperty IsLabelProperty = DependencyProperty.RegisterAttached(
        "IsLabel", typeof(bool), typeof(SidebarResizeMotion), new PropertyMetadata(false));
    public static void SetIsLabel(DependencyObject target, bool value) => target.SetValue(IsLabelProperty, value);
    public static bool GetIsLabel(DependencyObject target) => (bool)target.GetValue(IsLabelProperty);
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SidebarResizeMotion), new PropertyMetadata(false, EnabledChanged));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement sidebar) return;
        sidebar.Loaded -= Loaded;
        if (e.NewValue is true) sidebar.Loaded += Loaded;
        else if (States.TryGetValue(sidebar, out var state)) { state.Dispose(); States.Remove(sidebar); }
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var sidebar = (FrameworkElement)sender;
        if (sidebar.Parent is not Grid root || root.ColumnDefinitions.Count == 0) return;
        var state = States.GetValue(sidebar, _ => new State(sidebar, root));
        state.Settle(root.ColumnDefinitions[0].Width.Value);
    }
    internal static double VisibleWidth(FrameworkElement sidebar, double fallback) =>
        States.TryGetValue(sidebar, out var state) ? state.Clip.Rect.Width : fallback;
    internal static void Animate(FrameworkElement sidebar, ColumnDefinition column, double from, double to,
        int duration, Action? completed)
    {
        if (sidebar.Parent is not Grid root) { column.Width = new(to); completed?.Invoke(); return; }
        var state = States.GetValue(sidebar, _ => new State(sidebar, root));
        state.Animate(column, from, to, duration, completed);
    }
    private sealed class State
    {
        private readonly FrameworkElement _sidebar;
        private readonly Grid _root;
        private readonly Window? _owner;
        private readonly FrameworkElement? _content;
        internal readonly RectangleGeometry Clip = new();
        private Stopwatch? _clock;
        private readonly DispatcherTimer _frameWake;
        private double _from, _lastFrameMs;
        private int _duration;
        private EventHandler? _layoutObserver;
        private ColumnDefinition? _column;
        private double _target;
        private Action? _completed;
        private bool _disposed;
        private readonly List<FrameworkElement> _labels = new();
        private readonly List<SidebarSelectionSurface> _plates = new();
        private readonly List<InteractionSurface> _faces = new();
        private readonly List<Separator> _dividers = new();
        internal State(FrameworkElement sidebar, Grid root)
        {
            _sidebar = sidebar; _root = root;
            _frameWake = new DispatcherTimer(DispatcherPriority.Render, sidebar.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(8) };
            _frameWake.Tick += WakeFrame;
            _owner = Window.GetWindow(sidebar);
            _content = root.Children.OfType<FrameworkElement>().FirstOrDefault(x => x.Name == "SidebarContentStage");
            sidebar.Width = 232; sidebar.HorizontalAlignment = HorizontalAlignment.Left;
            sidebar.Clip = Clip; sidebar.ClipToBounds = false;
            Collect(sidebar);
            _target = root.ColumnDefinitions[0].Width.Value;
            Clip.Rect = new(0, 0, _target, Math.Max(1, sidebar.ActualHeight));
            root.SizeChanged += Resized; sidebar.Unloaded += Unloaded; MotionPolicy.Changed += PolicyChanged;
            if (_owner is not null) { _owner.DpiChanged += DpiChanged; _owner.Closed += OwnerClosed; }
        }
        private void Collect(DependencyObject node)
        {
            if (node is FrameworkElement element && GetIsLabel(element)) _labels.Add(element);
            if (node is SidebarSelectionSurface plate) _plates.Add(plate);
            if (node is InteractionSurface face) _faces.Add(face);
            if (node is Separator divider) _dividers.Add(divider);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i));
        }
        private void StopClip()
        {
            _frameWake.Stop();
            _clock?.Stop();
            if (_layoutObserver is not null) RenderTransitionClock.RemoveFrameObserver(_layoutObserver);
            _layoutObserver = null;
            _clock = null;
        }
        private void WakeFrame(object? sender, EventArgs e)
        {
            if (_clock is not { IsRunning: true } clock) return;
            var elapsed = clock.Elapsed.TotalMilliseconds;
            // Normal updates follow Rendering (including high-refresh displays). If WPF
            // coasts on independent opacity/transform clocks, wake this dependent layout
            // transition rather than skipping its eased start/end. No timer survives settle.
            if (elapsed - _lastFrameMs >= 14) Advance(elapsed);
        }
        private void Advance(double elapsed)
        {
            var progress = _duration <= 0 ? 1 : Math.Clamp(elapsed / _duration, 0, 1);
            ApplyWidth(_from + (_target - _from) * UiMotion.InOutSine.Ease(progress));
            if (progress >= 1) { Settle(_target); Finish(); }
        }
        internal void ApplyWidth(double width)
        {
            // Near the eased endpoints several clock values occupy the same device
            // pixel. Match layout rounding before touching the column so those ticks
            // cannot remeasure the dashboard for an edge that has not actually moved.
            if (_root.UseLayoutRounding)
            {
                var scale = VisualTreeHelper.GetDpi(_root).DpiScaleX;
                width = Math.Round(width * scale) / scale;
            }
            if (_column is not null && _column.Width.Value != width) _column.Width = new(width);
            Clip.Rect = new(0, 0, width, Math.Max(1, _sidebar.ActualHeight));
            foreach (var plate in _plates) plate.PaintedWidth = width - 16;
            foreach (var face in _faces) face.PaintedWidth = width - 16;
            // The sidebar retains its expanded layout under the animated clip. Keep both
            // divider insets inside the visible width instead of clipping off the right end.
            foreach (var divider in _dividers) divider.Width = Math.Max(0, width - 32);
        }
        internal void Animate(ColumnDefinition column, double from, double to, int duration, Action? completed)
        {
            var visible = Clip.Rect.Width;
            StopClip(); _target = to; _completed = completed;
            _column = column;
            // Reflow the bounded, virtualized viewport as its edge moves. Committing the final
            // width before translating exposed the new layout at the start of the gesture.
            ApplyWidth(visible);
            if (!MotionPolicy.Spatial || !_sidebar.IsLoaded || duration <= 0) { Settle(to); Finish(); return; }
            if (_content is not null)
            {
                SurfaceMotion.Stop(_content, MotionChannel.Layout);
                UiMotion.ChannelTranslate(_content, MotionChannel.Layout).X = 0;
            }
            int index = 0;
            foreach (var label in _labels)
            {
                SurfaceMotion.Stop(label, MotionChannel.Presence);
                var move = UiMotion.ChannelTranslate(label, MotionChannel.Presence);
                if (to > 64 && label.Opacity == 0) move.X = -6;
                SurfaceMotion.AnimateTracks(label, MotionChannel.Presence,
                    [new(label, UIElement.OpacityProperty, to > 64 ? 1 : 0),
                     new(move, TranslateTransform.XProperty, 0, true)], to > 64 ? 140 : 90, UiMotion.OutSine,
                    startDelayMs: to > 64 ? 40 + Math.Min(2, index++ / 3) * 16 : 0);
            }
            _from = visible; _duration = duration; _lastFrameMs = 0;
            _clock = Stopwatch.StartNew();
            _layoutObserver = (_, _) =>
            {
                if (_clock is not { IsRunning: true } clock) return;
                _lastFrameMs = clock.Elapsed.TotalMilliseconds;
                Advance(_lastFrameMs);
            };
            RenderTransitionClock.AddFrameObserver(_layoutObserver);
            _frameWake.Start();
        }
        internal void Settle(double width)
        {
            StopClip(); _target = width; ApplyWidth(width);
            if (_content is not null) { SurfaceMotion.Stop(_content, MotionChannel.Layout); UiMotion.ChannelTranslate(_content, MotionChannel.Layout).X = 0; }
            foreach (var plate in _plates) { SurfaceMotion.Stop(plate, MotionChannel.Layout); plate.PaintedWidth = width - 16; }
            foreach (var face in _faces) { SurfaceMotion.Stop(face, MotionChannel.Layout); face.PaintedWidth = width - 16; }
            foreach (var label in _labels)
            { SurfaceMotion.Stop(label, MotionChannel.Presence); label.Opacity = width > 64 ? 1 : 0; UiMotion.ChannelTranslate(label, MotionChannel.Presence).X = 0; }
        }
        private void Finish() { var callback = _completed; _completed = null; callback?.Invoke(); }
        private void Resized(object sender, SizeChangedEventArgs e) { Settle(_target); Finish(); }
        private void DpiChanged(object sender, DpiChangedEventArgs e) { Settle(_target); Finish(); }
        private void PolicyChanged() { Settle(_target); Finish(); }
        private void Unloaded(object sender, RoutedEventArgs e) { Dispose(); States.Remove(_sidebar); }
        private void OwnerClosed(object? sender, EventArgs e) { Dispose(); States.Remove(_sidebar); }
        internal void Dispose()
        {
            if (_disposed) return; _disposed = true;
            Settle(_target); Finish(); _root.SizeChanged -= Resized; _sidebar.Unloaded -= Unloaded; MotionPolicy.Changed -= PolicyChanged;
            _frameWake.Tick -= WakeFrame;
            if (_owner is not null) { _owner.DpiChanged -= DpiChanged; _owner.Closed -= OwnerClosed; }
        }
    }
}
