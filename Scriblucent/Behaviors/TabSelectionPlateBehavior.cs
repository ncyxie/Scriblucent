using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Behaviors;

/// <summary>One painted selection surface moves between tabs without measuring their headers.</summary>
public static class TabSelectionPlateBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TabSelectionPlateBehavior), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(TabSelectionPlateBehavior));
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    internal static void SettleAfterDrag(TabControl tabs) => (tabs.GetValue(StateProperty) as State)?.SettleAfterDrag();
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TabControl tabs) return;
        tabs.Loaded -= Loaded; tabs.Unloaded -= Unloaded;
        (tabs.GetValue(StateProperty) as State)?.Dispose(); tabs.ClearValue(StateProperty);
        if (!(bool)args.NewValue) return;
        tabs.Loaded += Loaded; tabs.Unloaded += Unloaded;
        if (tabs.IsLoaded) Loaded(tabs, new RoutedEventArgs());
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var tabs = (TabControl)sender;
        if (tabs.GetValue(StateProperty) is null) tabs.SetValue(StateProperty, new State(tabs));
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var tabs = (TabControl)sender;
        (tabs.GetValue(StateProperty) as State)?.Dispose(); tabs.ClearValue(StateProperty);
    }
    private sealed class State : IDisposable
    {
        private readonly TabControl _tabs;
        private readonly TabSelectionSurface? _plate;
        private readonly ScrollViewer? _viewer;
        private readonly Button? _previous, _next;
        private AnimationClock? _clock;
        private readonly RenderTransitionClock _scrollMotion = new();
        private double _scrollTarget;
        private bool _scrolling;
        private EventHandler? _completed;
        private Rect _target;
        private Rect _selectionFrom = Rect.Empty;
        private Point _selectionAnchor;
        private bool _initialized, _disposed, _queued, _revealQueued, _selectionPending;
        private readonly Window? _owner;
        private readonly HashSet<TabItem> _headers = new();
        internal State(TabControl tabs)
        {
            _tabs = tabs; tabs.ApplyTemplate();
            _plate = tabs.Template.FindName("TabSelectionPlate", tabs) as TabSelectionSurface;
            _viewer = tabs.Template.FindName("TabHeaderScrollViewer", tabs) as ScrollViewer;
            _previous = tabs.Template.FindName("ScrollTabsLeft", tabs) as Button;
            _next = tabs.Template.FindName("ScrollTabsRight", tabs) as Button;
            if (_viewer is not null) { _viewer.ScrollChanged += Scrolled; _viewer.PreviewMouseWheel += Wheel; _viewer.SizeChanged += Resized; }
            if (_previous is not null) _previous.Click += ScrollPrevious;
            if (_next is not null) _next.Click += ScrollNext;
            tabs.SelectionChanged += Selected; tabs.SizeChanged += Resized;
            tabs.PreviewMouseMove += Dragged; tabs.PreviewMouseLeftButtonUp += Released;
            tabs.ItemContainerGenerator.ItemsChanged += HeadersChanged;
            MotionPolicy.Changed += PreferenceChanged;
            _owner = Window.GetWindow(tabs);
            if (_owner is not null) _owner.Closed += Closed;
            ObserveHeaders(); Queue();
        }
        private void ObserveHeaders()
        {
            var current = _tabs.Items.OfType<TabItem>().ToHashSet();
            foreach (var stale in _headers.Where(item => !current.Contains(item)).ToArray())
            { stale.SizeChanged -= HeaderResized; _headers.Remove(stale); }
            foreach (var item in current)
                if (_headers.Add(item)) item.SizeChanged += HeaderResized;
        }
        private void HeadersChanged(object sender, System.Windows.Controls.Primitives.ItemsChangedEventArgs args)
        { ObserveHeaders(); Queue(); }
        private void HeaderResized(object sender, SizeChangedEventArgs args)
        { if (ReferenceEquals(sender, _tabs.SelectedItem)) _revealQueued = true; Queue(); }
        private void Closed(object? sender, EventArgs args) => Dispose();
        private void Selected(object sender, SelectionChangedEventArgs args)
        {
            if (!ReferenceEquals(args.Source, _tabs)) return;
            CancelScroll();
            _selectionFrom = _plate?.Bounds ?? Rect.Empty;
            if (!_selectionFrom.IsEmpty && _plate is not null)
                _selectionAnchor = _plate.TranslatePoint(new Point(_selectionFrom.X + _selectionFrom.Width / 2, _selectionFrom.Bottom - 8), _tabs);
            _selectionPending = true; _revealQueued = true; Queue();
        }
        private void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (_viewer is null) return;
            // Test the full rail, not the narrower viewport while its arrows are visible.
            var railWidth = (_viewer.Parent as FrameworkElement)?.ActualWidth ?? _viewer.ActualWidth;
            var overflow = _viewer.ExtentWidth > railWidth + 1;
            if (_previous is not null)
            {
                _previous.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
                _previous.IsEnabled = _viewer.HorizontalOffset > 1;
            }
            if (_next is not null)
            {
                _next.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
                _next.IsEnabled = _viewer.HorizontalOffset < _viewer.ScrollableWidth - 1;
            }
            Update(false);
        }
        private void ScrollPrevious(object sender, RoutedEventArgs args) => Scroll(-1);
        private void ScrollNext(object sender, RoutedEventArgs args) => Scroll(1);
        private void Scroll(int direction)
        {
            if (_viewer is not null)
                ScrollBy(direction * _viewer.ViewportWidth * .75);
        }
        private void ScrollBy(double distance)
        {
            if (_viewer is null || _disposed || _viewer.ScrollableWidth <= 1) return;
            var from = _viewer.HorizontalOffset;
            var reversing = _scrolling && Math.Sign(distance) != Math.Sign(_scrollTarget - from);
            var target = Math.Clamp((!_scrolling || reversing ? from : _scrollTarget) + distance, 0, _viewer.ScrollableWidth);
            _scrollMotion.Stop();
            _scrollTarget = target;
            _scrolling = MotionPolicy.Spatial && Math.Abs(target - from) > .1;
            if (!_scrolling) { _viewer.ScrollToHorizontalOffset(target); return; }
            _scrollMotion.Run([new(240, UiMotion.InOutSine,
                progress =>
                {
                    _viewer.ScrollToHorizontalOffset(Math.Clamp(from + (target - from) * progress, 0, _viewer.ScrollableWidth));
                    // The first eased frame can have the same offset. Keep idle rails producing
                    // frames until the glide finishes, without introducing a polling timer.
                    if (progress < 1) _viewer.InvalidateVisual();
                })],
                () => _scrolling = false, startOnNextFrame: true);
        }
        private void CancelScroll()
        {
            if (!_scrolling || _viewer is null) return;
            _scrollMotion.Stop(); _scrolling = false;
            // Replace any pending WPF offset with the visible pose before selection or dragging.
            _viewer.ScrollToHorizontalOffset(_viewer.HorizontalOffset);
        }
        private void Wheel(object sender, MouseWheelEventArgs args)
        {
            if (_viewer is null || _viewer.ScrollableWidth <= 1) return;
            ScrollBy(-args.Delta * .5);
            args.Handled = true;
        }
        private void Resized(object sender, SizeChangedEventArgs args) => Update(false);
        private void Dragged(object sender, MouseEventArgs args)
        {
            if (args.LeftButton == MouseButtonState.Pressed && _tabs.IsMouseCaptureWithin) Update(false);
        }
        private void Released(object sender, MouseButtonEventArgs args) => Queue();
        internal void SettleAfterDrag()
        {
            CancelScroll();
            Stop();
            _selectionPending = false;
            _selectionFrom = Rect.Empty;
            _target = Rect.Empty;
            Update(false);
        }
        private void PreferenceChanged()
        {
            if (_scrolling && _viewer is not null)
            {
                _scrollMotion.Stop(); _scrolling = false;
                _viewer.ScrollToHorizontalOffset(Math.Clamp(_scrollTarget, 0, _viewer.ScrollableWidth));
            }
            Stop(); if (_plate is not null) _plate.Bounds = _target;
        }
        private void Queue()
        {
            if (_queued || _disposed) return;
            _queued = true;
            _tabs.Dispatcher.BeginInvoke(() =>
            {
                _queued = false;
                if (_disposed) return;
                if (_revealQueued && _tabs.SelectedItem is TabItem selected)
                {
                    CancelScroll();
                    selected.BringIntoView();
                    if (_viewer is not null)
                    {
                        if (_tabs.Items.IndexOf(selected) == 0) _viewer.ScrollToLeftEnd();
                        else if (_tabs.Items.IndexOf(selected) == _tabs.Items.Count - 1) _viewer.ScrollToRightEnd();
                    }
                }
                _revealQueued = false;
                Update(true);
            }, DispatcherPriority.Loaded);
        }
        private void Update(bool animate)
        {
            if (_disposed || _plate is null) return;
            if (_viewer is not null)
            {
                var viewport = _viewer.TranslatePoint(new Point(), _plate);
                _plate.ViewportBounds = new Rect(viewport.X, 0, Math.Max(0, _viewer.ActualWidth), Math.Max(0, _plate.ActualHeight));
            }
            if (_tabs.SelectedItem is not TabItem selected || selected.ActualWidth <= 0 || selected.ActualHeight <= 0)
            { Stop(); _plate.Visibility = Visibility.Hidden; return; }
            var origin = selected.TranslatePoint(new Point(), _plate);
            var next = new Rect(origin, selected.RenderSize);
            // Selection changes title weight and can resize/scroll headers before the queued update.
            // Keep the visible departure pose through those layout events instead of teleporting it.
            if (_selectionPending && !animate) return;
            var visible = _selectionPending && !_selectionFrom.IsEmpty ? _selectionFrom : _plate.Bounds;
            if (_selectionPending && animate)
            {
                _selectionPending = false;
                if (_initialized && !_plate.Bounds.IsEmpty)
                {
                    Point Anchor(Rect rect) => _plate.TranslatePoint(new Point(rect.X + rect.Width / 2, rect.Bottom - 8), _tabs);
                    CommandLightBehavior.SettleSelection(_tabs, _selectionAnchor, Anchor(next));
                }
            }
            if (_initialized && next == _target) return;
            Stop(); _target = next; _plate.Bounds = next; _plate.Visibility = Visibility.Visible;
            if (!animate || !_initialized || !MotionPolicy.Spatial) { _initialized = true; return; }
            _initialized = true;
            var clock = new RectAnimation(visible, next, TimeSpan.FromMilliseconds(220))
                { EasingFunction = UiMotion.OutCubic, FillBehavior = FillBehavior.Stop }.CreateClock();
            _clock = clock;
            _completed = (_, _) => { if (ReferenceEquals(_clock, clock)) Stop(); };
            clock.Completed += _completed;
            RenderTransitionClock.NativeStarted(); _plate.ApplyAnimationClock(TabSelectionSurface.BoundsProperty, clock);
        }
        private void Stop()
        {
            if (_clock is null) return;
            _clock.Completed -= _completed; _plate?.ApplyAnimationClock(TabSelectionSurface.BoundsProperty, null);
            _clock = null; _completed = null; RenderTransitionClock.NativeStopped();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; CancelScroll(); _scrollMotion.Stop(); Stop();
            _tabs.SelectionChanged -= Selected; _tabs.SizeChanged -= Resized;
            _tabs.PreviewMouseMove -= Dragged; _tabs.PreviewMouseLeftButtonUp -= Released;
            _tabs.ItemContainerGenerator.ItemsChanged -= HeadersChanged;
            if (_viewer is not null) { _viewer.ScrollChanged -= Scrolled; _viewer.PreviewMouseWheel -= Wheel; _viewer.SizeChanged -= Resized; }
            if (_previous is not null) _previous.Click -= ScrollPrevious;
            if (_next is not null) _next.Click -= ScrollNext;
            foreach (var header in _headers) header.SizeChanged -= HeaderResized;
            _headers.Clear();
            MotionPolicy.Changed -= PreferenceChanged;
            if (_owner is not null) _owner.Closed -= Closed;
        }
    }
}
