using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

public static class SettingsNavigationBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SettingsNavigationBehavior), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SettingsNavigationBehavior));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement root) return;
        root.Loaded -= Loaded; root.Unloaded -= Unloaded;
        Unloaded(root, new RoutedEventArgs());
        if (e.NewValue is true) { root.Loaded += Loaded; root.Unloaded += Unloaded; }
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        if (root.GetValue(StateProperty) == null) root.SetValue(StateProperty, new State(root));
    }
    private static void Unloaded(object sender, RoutedEventArgs e)
    { var root = (FrameworkElement)sender; (root.GetValue(StateProperty) as State)?.Dispose(); root.ClearValue(StateProperty); }
    private sealed class State : IDisposable
    {
        private readonly FrameworkElement _root;
        private readonly ScrollViewer? _viewer;
        private readonly Border? _plate;
        private readonly WrapPanel? _nav;
        private readonly (Button Button, FrameworkElement Section)[] _items;
        private readonly RectangleGeometry _clip = new() { RadiusX = 10, RadiusY = 10 };
        private AnimationClock? _clock;
        private EventHandler? _completed;
        private Rect _target;
        private bool _initialized;
        private int _active = -1;
        internal State(FrameworkElement root)
        {
            _root = root;
            _viewer = root.FindName("SettingsScrollViewer") as ScrollViewer;
            _plate = root.FindName("CategorySelectionPlate") as Border;
            _nav = root.FindName("CategoryNavigation") as WrapPanel;
            _items = _nav?.Children.OfType<Button>().Select(b => (b, root.FindName(b.Tag as string ?? "") as FrameworkElement))
                .Where(i => i.Item2 != null).Select(i => (i.b, i.Item2!)).ToArray() ?? [];
            if (_plate != null) _plate.Clip = _clip;
            foreach (var item in _items) item.Button.Click += Click;
            if (_viewer != null)
            {
                _viewer.ScrollChanged += Scrolled; _viewer.PreviewMouseWheel += Wheel;
                _viewer.PreviewMouseDown += MouseDown;
            }
            root.LayoutUpdated += Layout; MotionPolicy.Changed += Policy;
            Update(false);
        }
        private void Click(object sender, RoutedEventArgs e)
        {
            if (_viewer?.Content is not UIElement content) return;
            var item = _items.First(i => ReferenceEquals(i.Button, sender));
            SmoothScrollBehavior.ScrollTo(_viewer, item.Section.TranslatePoint(new Point(), content).Y);
        }
        private void Wheel(object sender, MouseWheelEventArgs e) { if (_viewer != null) SmoothScrollBehavior.CancelTarget(_viewer); }
        private void MouseDown(object sender, MouseButtonEventArgs e)
        {
            for (var node = e.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, _viewer); node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is ScrollBar) { if (_viewer != null) SmoothScrollBehavior.CancelTarget(_viewer); break; }
        }
        private void Scrolled(object sender, ScrollChangedEventArgs e) => Update(true);
        private void Layout(object? sender, EventArgs e) => Update(true); // Events only, no idle render observer.
        private void Policy() { Stop(); _clip.Rect = _target; }
        private void Update(bool animate)
        {
            if (_plate == null || _viewer?.Content is not UIElement content || _items.Length == 0) return;
            var active = 0;
            if (_viewer.ScrollableHeight > 0 && _viewer.VerticalOffset >= _viewer.ScrollableHeight - 1) active = _items.Length - 1;
            else for (var i = 0; i < _items.Length; i++)
                if (_items[i].Section.TranslatePoint(new Point(), content).Y <= _viewer.VerticalOffset + 24) active = i;
            var button = _items[active].Button;
            if (button.ActualWidth <= 0 || button.ActualHeight <= 0) return;
            var next = new Rect(button.TranslatePoint(new Point(), _plate), button.RenderSize);
            if (_initialized && next == _target && _active == active) return;
            if (_active != active)
            {
                _active = active;
                foreach (var item in _items) item.Button.SetResourceReference(Control.ForegroundProperty,
                    ReferenceEquals(item.Button, button) ? "TextColor" : "TextColorSecondary");
            }
            var visible = _clip.Rect; Stop(); _target = next; _clip.Rect = next;
            if (!animate || !_initialized || !MotionPolicy.Spatial) { _initialized = true; return; }
            _initialized = true;
            var clock = new RectAnimation(visible, next, TimeSpan.FromMilliseconds(220)) { EasingFunction = UiMotion.OutCubic, FillBehavior = FillBehavior.Stop }.CreateClock();
            _clock = clock; _completed = (_, _) => { if (ReferenceEquals(_clock, clock)) Stop(); };
            clock.Completed += _completed; RenderTransitionClock.NativeStarted(); _clip.ApplyAnimationClock(RectangleGeometry.RectProperty, clock);
        }
        private void Stop()
        {
            if (_clock == null) return;
            _clock.Completed -= _completed; _clip.ApplyAnimationClock(RectangleGeometry.RectProperty, null);
            _clock = null; _completed = null; RenderTransitionClock.NativeStopped();
        }
        public void Dispose()
        {
            Stop(); foreach (var item in _items) item.Button.Click -= Click;
            if (_viewer != null) { _viewer.ScrollChanged -= Scrolled; _viewer.PreviewMouseWheel -= Wheel; _viewer.PreviewMouseDown -= MouseDown; SmoothScrollBehavior.CancelTarget(_viewer); }
            _root.LayoutUpdated -= Layout; MotionPolicy.Changed -= Policy;
        }
    }
}
