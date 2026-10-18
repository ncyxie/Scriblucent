using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

public static class SliderSurfaceBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SliderSurfaceBehavior), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SliderSurfaceBehavior));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Slider slider) return;
        if (slider.GetValue(StateProperty) is State old) old.Dispose();
        slider.ClearValue(StateProperty);
        if (e.NewValue is true) slider.SetValue(StateProperty, new State(slider));
    }
    private sealed class State
    {
        private readonly Slider _slider;
        private FrameworkElement? _thumbFace;
        private bool _dragging;
        private ControlTemplate? _template;
        public State(Slider slider)
        {
            _slider = slider;
            slider.Loaded += Loaded; slider.Unloaded += Unloaded;
            slider.ValueChanged += Value; slider.SizeChanged += Sized;
            slider.IsKeyboardFocusWithinChanged += Focus;
            slider.MouseEnter += Hover; slider.MouseLeave += Hover;
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted));
            if (slider.IsLoaded) Loaded(slider, new RoutedEventArgs());
        }
        private void Loaded(object sender, RoutedEventArgs e)
        { MotionPolicy.Changed -= Policy; MotionPolicy.Changed += Policy; _slider.LayoutUpdated -= Layout; _slider.LayoutUpdated += Layout; _template = _slider.Template; UpdateFill(); Feedback(false); }
        private void Unloaded(object sender, RoutedEventArgs e)
        { MotionPolicy.Changed -= Policy; _slider.LayoutUpdated -= Layout; _dragging = false; if (_thumbFace != null) SurfaceMotion.Stop(_thumbFace); _thumbFace = null; _template = null; }
        private void Layout(object? sender, EventArgs e)
        {
            if (ReferenceEquals(_template, _slider.Template)) return;
            if (_thumbFace != null) SurfaceMotion.Stop(_thumbFace);
            _template = _slider.Template; _thumbFace = null; UpdateFill(); Feedback(false);
        }
        private void Policy() => Feedback(false);
        private void Value(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateFill();
        private void Sized(object sender, SizeChangedEventArgs e) => UpdateFill();
        private void Focus(object sender, DependencyPropertyChangedEventArgs e) => Feedback(true);
        private void Hover(object sender, MouseEventArgs e) => Feedback(true, e.RoutedEvent == Mouse.MouseEnterEvent);
        private void DragStarted(object sender, DragStartedEventArgs e) { _dragging = true; Feedback(true); }
        private void DragCompleted(object sender, DragCompletedEventArgs e) { _dragging = false; Feedback(true); }
        private void UpdateFill()
        {
            if (_slider.Template?.FindName("PART_Track", _slider) is not Track track) return;
            if (_slider.Template.FindName("ValueFill", _slider) is not Border fill) return; // Hue has no overlay.
            var fraction = _slider.Maximum <= _slider.Minimum ? 0 : Math.Clamp((_slider.Value - _slider.Minimum) / (_slider.Maximum - _slider.Minimum), 0, 1);
            var thumbWidth = track.Thumb?.ActualWidth ?? 16;
            fill.Width = Math.Max(0, thumbWidth / 2 + fraction * Math.Max(0, track.ActualWidth - thumbWidth));
        }
        private void Feedback(bool animate, bool? hovered = null)
        {
            if (_slider.Template?.FindName("PART_Track", _slider) is not Track { Thumb: { } thumb }) return;
            thumb.ApplyTemplate();
            if (thumb.Template.FindName("ThumbFace", thumb) is not FrameworkElement face) return;
            if (_thumbFace != null && !ReferenceEquals(_thumbFace, face)) SurfaceMotion.Stop(_thumbFace);
            _thumbFace = face;
            var active = _slider.IsEnabled && (_dragging || (hovered ?? _slider.IsMouseOver) || _slider.IsKeyboardFocusWithin);
            var scale = UiMotion.EnsureScale(face);
            var size = active && MotionPolicy.Spatial ? 16d / 14 : 1;
            var tracks = new List<NativeMotionTrack> { new(scale, ScaleTransform.ScaleXProperty, size, true), new(scale, ScaleTransform.ScaleYProperty, size, true) };
            if (face is Border border)
                AccentMotion.Set(border, _slider.IsKeyboardFocusWithin || _dragging,
                    _slider.TryFindResource("AccentRimBrush") as Brush, 7, 2, animate);
            SurfaceMotion.AnimateTracks(face, MotionChannel.Interaction, tracks, active ? 140 : 160, UiMotion.OutSine, instant: !animate);
        }
        public void Dispose()
        {
            Unloaded(_slider, new RoutedEventArgs());
            _slider.Loaded -= Loaded; _slider.Unloaded -= Unloaded; _slider.ValueChanged -= Value; _slider.SizeChanged -= Sized;
            _slider.IsKeyboardFocusWithinChanged -= Focus; _slider.MouseEnter -= Hover; _slider.MouseLeave -= Hover;
            _slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted));
            _slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted));
        }
    }
}
