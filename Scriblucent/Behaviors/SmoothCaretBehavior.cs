using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Keeps editing native. Only short, same-line keyboard moves briefly borrow a visual caret;
/// native painting and blinking resume as soon as the movement settles.
/// </summary>
public static class SmoothCaretBehavior
{
    internal const int MoveMilliseconds = 85;
    private static int _registered;
    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session", typeof(Session), typeof(SmoothCaretBehavior), new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothCaretBehavior), new PropertyMetadata(true, AvailabilityChanged));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Editors can park a logical selection without exposing their temporary native caret.
    public static readonly DependencyProperty IsSuspendedProperty = DependencyProperty.RegisterAttached(
        "IsSuspended", typeof(bool), typeof(SmoothCaretBehavior), new PropertyMetadata(false, AvailabilityChanged));
    public static bool GetIsSuspended(DependencyObject element) => (bool)element.GetValue(IsSuspendedProperty);
    public static void SetIsSuspended(DependencyObject element, bool value) => element.SetValue(IsSuspendedProperty, value);

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        EventManager.RegisterClassHandler(typeof(TextBoxBase), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(Focused), true);
    }

    private static void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBoxBase box && box.IsKeyboardFocused && GetIsEnabled(box)
            && box.GetValue(SessionProperty) is null)
            box.SetValue(SessionProperty, new Session(box));
    }

    private static void AvailabilityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender.GetValue(SessionProperty) is Session session) session.Cancel();
    }

    internal readonly record struct Snapshot(bool Tracking, bool Moving, bool NativeHidden, Rect Visible, Rect Target, int Moves);
    internal static Snapshot Inspect(TextBoxBase box) => box.GetValue(SessionProperty) is Session session
        ? session.Inspect() : default;

    private sealed class Session
    {
        private readonly TextBoxBase _box;
        private readonly Window? _window;
        private readonly DependencyPropertyDescriptor _readOnly;
        private DispatcherOperation? _pending;
        private CaretAdorner? _caret;
        private AdornerLayer? _layer;
        private ScrollContentPresenter? _viewport;
        private bool _gesture, _composing, _hidden, _disposed;
        private Rect _origin, _target = Rect.Empty;
        private int _moves;

        internal Session(TextBoxBase box)
        {
            _box = box;
            _window = Window.GetWindow(box);
            _readOnly = DependencyPropertyDescriptor.FromProperty(TextBoxBase.IsReadOnlyProperty, typeof(TextBoxBase));
            box.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
            box.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(TextInput), true);
            box.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(CompositionStarted), true);
            box.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(CompositionUpdated), true);
            box.SelectionChanged += SelectionChanged;
            box.TextChanged += TextChanged;
            box.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown), true);
            box.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel), true);
            box.LostKeyboardFocus += LostFocus;
            box.Unloaded += Unloaded;
            box.SizeChanged += SizeChanged;
            box.IsEnabledChanged += Availability;
            box.IsVisibleChanged += Availability;
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled), true);
            box.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(Command), true);
            _readOnly.AddValueChanged(box, ReadOnlyChanged);
            MotionPolicy.Changed += Cancel;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            if (_window is not null) _window.Deactivated += Deactivated;
        }

        private bool Available => !_disposed && _box.IsLoaded && _box.IsVisible && _box.IsEnabled
            && _box.IsKeyboardFocused && !_box.IsReadOnly && GetIsEnabled(_box) && !GetIsSuspended(_box)
            && MotionPolicy.Spatial && !SystemParameters.HighContrast;
        private bool EmptySelection => _box switch
        {
            TextBox text => text.SelectionLength == 0,
            RichTextBox rich => rich.Selection.IsEmpty,
            _ => false
        };

        private void KeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0
                || key is Key.Enter or Key.Tab or Key.Escape or Key.Up or Key.Down or Key.Home or Key.End
                    or Key.PageUp or Key.PageDown or Key.Insert
                || ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && key is Key.Left or Key.Right))
            { Cancel(); return; }
            if (key is Key.Left or Key.Right or Key.Back or Key.Delete) BeginGesture();
        }

        private void TextInput(object sender, TextCompositionEventArgs e)
        {
            var wasComposing = _composing;
            _composing = false;
            // Composition/candidate placement stays entirely native, including its final commit.
            if (e.Handled || wasComposing || e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off
                || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
            { Cancel(); return; }
            BeginGesture();
        }
        private void CompositionStarted(object sender, TextCompositionEventArgs e)
        {
            if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off) return;
            _composing = true; Cancel();
        }
        private void CompositionUpdated(object sender, TextCompositionEventArgs e)
        { _composing = true; Cancel(); }

        private void BeginGesture()
        {
            if (!Available || _composing || !EmptySelection) { Cancel(); return; }
            if (_gesture) return; // More input before layout: one update to the latest native position.
            if (_caret is not null)
            {
                // Validate the native edit's distance, not the visual caret's accumulated lag.
                // Keep the old clock moving until the new text has valid layout to retarget it.
                _origin = _target;
            }
            else
            {
                if (!TryRect(out var rect)) { Cancel(); return; }
                // Never replace an intentionally invisible caret or another caret animation.
                var brush = _box.CaretBrush ?? _box.Foreground ?? Brushes.Black;
                if (brush.Opacity <= 0 || brush is SolidColorBrush { Color.A: 0 }
                    || DependencyPropertyHelper.GetValueSource(_box, TextBoxBase.CaretBrushProperty).IsAnimated)
                    return;
                _layer = AdornerLayer.GetAdornerLayer(_box);
                if (_layer is null) return;
                _viewport = FindViewport(_box);
                if (!ViewportRect().Contains(new Point(rect.X, rect.Y + rect.Height / 2))) return;
                _origin = rect;
                _caret = new CaretAdorner(_box, brush, rect, ViewportRect());
                _layer.Add(_caret);
                // Animation precedence preserves local values, bindings and dynamic theme resources.
                var hide = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1) };
                hide.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                var clock = hide.CreateClock();
                _box.ApplyAnimationClock(TextBoxBase.CaretBrushProperty, clock);
                clock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
                clock.Controller.Begin();
                _hidden = true;
            }
            _gesture = true;
            // RichTextBox may process native typing at Background priority. Changes promote
            // this no-op fallback to Loaded; never settle before the native edit has happened.
            Queue(DispatcherPriority.ApplicationIdle);
        }

        private void SelectionChanged(object sender, RoutedEventArgs e)
        { if (_gesture && EmptySelection) Queue(); else Cancel(); }
        private void TextChanged(object sender, TextChangedEventArgs e)
        { if (_gesture) Queue(); else Cancel(); }
        private void Queue(DispatcherPriority priority = DispatcherPriority.Loaded)
        {
            if (_pending is not null && _pending.Priority >= priority) return;
            _pending?.Abort();
            // After WPF's render-priority layout; no UpdateLayout, polling or per-frame measurements.
            _pending = _box.Dispatcher.BeginInvoke(priority, new Action(Settle));
        }
        private void Settle()
        {
            _pending = null;
            _gesture = false;
            if (!Available || _composing || !EmptySelection || _caret is null || !TryRect(out var rect)
                || !CanGlide(_origin, rect) || !ViewportRect().Contains(new Point(rect.X, rect.Y + rect.Height / 2)))
            { Cancel(); return; }
            _target = rect;
            var caret = _caret;
            _moves++;
            caret.SetGeometry(rect, ViewportRect());
            NativeSurfaceMotion.AnimateTracks(caret, MotionChannel.Interaction,
                [new(caret.Position, TranslateTransform.XProperty, rect.X, Spatial: true)],
                MoveMilliseconds, UiMotion.OutQuad, MovementCompleted);
        }

        private void MovementCompleted()
        {
            // A previous glide may finish while RichTextBox is still applying the next edit.
            // Its queued layout update owns the caret now; don't cancel that pending input.
            if (!_gesture) Cancel();
        }

        private static bool CanGlide(Rect from, Rect to) => !from.IsEmpty && !to.IsEmpty
            && Math.Abs(from.Y - to.Y) < .75 && Math.Abs(from.Height - to.Height) < .75
            && Math.Abs(from.X - to.X) > .1 && Math.Abs(from.X - to.X) <= Math.Max(40, to.Height * 3);

        private bool TryRect(out Rect rect)
        {
            rect = Rect.Empty;
            if (!_box.IsMeasureValid || !_box.IsArrangeValid) return false;
            if (_box is TextBox text)
            {
                // The text view must also be current; the public geometry API can otherwise force layout.
                var view = _viewport ?? FindViewport(_box);
                if (view is { IsMeasureValid: false } or { IsArrangeValid: false }
                    || view?.Content is UIElement { IsMeasureValid: false } or UIElement { IsArrangeValid: false }) return false;
                rect = text.GetRectFromCharacterIndex(text.CaretIndex);
            }
            else if (_box is RichTextBox rich && rich.CaretPosition.HasValidLayout)
                rect = rich.CaretPosition.GetCharacterRect(rich.CaretPosition.LogicalDirection);
            if (rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)
                || !double.IsFinite(rect.Height) || rect.Height <= 0) return false;
            rect = new Rect(rect.X, rect.Y, Math.Max(1, SystemParameters.CaretWidth), rect.Height);
            return true;
        }

        private Rect ViewportRect()
        {
            if (_viewport is { ActualWidth: > 0, ActualHeight: > 0 })
                return new Rect(_viewport.TranslatePoint(new Point(), _box), _viewport.RenderSize);
            return new Rect(_box.RenderSize);
        }
        private static ScrollContentPresenter? FindViewport(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollContentPresenter viewport) return viewport;
                if (FindViewport(child) is { } nested) return nested;
            }
            return null;
        }

        internal Snapshot Inspect() => new(!_disposed, _caret is not null && NativeSurfaceMotion.IsRunning(_caret, MotionChannel.Interaction),
            _hidden, _caret?.CurrentRect ?? Rect.Empty, _target, _moves);

        internal void Cancel()
        {
            _gesture = false;
            _pending?.Abort(); _pending = null;
            if (_caret is { } caret)
            {
                NativeSurfaceMotion.Stop(caret);
                _layer?.Remove(caret);
                _caret = null; _layer = null; _viewport = null;
            }
            if (_hidden)
            {
                _hidden = false;
                _box.ApplyAnimationClock(TextBoxBase.CaretBrushProperty, null);
            }
        }
        private void Command(object sender, ExecutedRoutedEventArgs e)
        {
            if (_gesture && (e.Command == EditingCommands.Backspace || e.Command == EditingCommands.Delete
                || e.Command == EditingCommands.MoveLeftByCharacter || e.Command == EditingCommands.MoveRightByCharacter)) return;
            Cancel();
        }
        private void Scrolled(object sender, ScrollChangedEventArgs e)
        { if (e.HorizontalChange != 0 || e.VerticalChange != 0 || e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) Cancel(); }
        private void MouseDown(object sender, MouseButtonEventArgs e) => Cancel();
        private void MouseWheel(object sender, MouseWheelEventArgs e) => Cancel();
        private void SizeChanged(object sender, SizeChangedEventArgs e) => Cancel();
        private void Availability(object sender, DependencyPropertyChangedEventArgs e) => Cancel();
        private void ReadOnlyChanged(object? sender, EventArgs e) => Cancel();
        private void SystemChanged(object? sender, PropertyChangedEventArgs e) => Cancel();
        private void Deactivated(object? sender, EventArgs e) => Cancel();
        private void LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispose();
        private void Unloaded(object sender, RoutedEventArgs e) => Dispose();
        private void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Cancel();
            _box.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
            _box.RemoveHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(TextInput));
            _box.RemoveHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(CompositionStarted));
            _box.RemoveHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(CompositionUpdated));
            _box.SelectionChanged -= SelectionChanged;
            _box.TextChanged -= TextChanged;
            _box.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown));
            _box.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel));
            _box.LostKeyboardFocus -= LostFocus;
            _box.Unloaded -= Unloaded;
            _box.SizeChanged -= SizeChanged;
            _box.IsEnabledChanged -= Availability;
            _box.IsVisibleChanged -= Availability;
            _box.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            _box.RemoveHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(Command));
            _readOnly.RemoveValueChanged(_box, ReadOnlyChanged);
            MotionPolicy.Changed -= Cancel;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            if (_window is not null) _window.Deactivated -= Deactivated;
            _box.ClearValue(SessionProperty);
        }
    }

    private sealed class CaretAdorner : Adorner
    {
        internal TranslateTransform Position { get; } = new();
        private readonly Brush _brush;
        private Rect _rect;
        private RectangleGeometry _clip;
        internal CaretAdorner(TextBoxBase box, Brush brush, Rect rect, Rect viewport) : base(box)
        {
            IsHitTestVisible = false;
            Focusable = false;
            _brush = brush;
            _rect = rect;
            _clip = new RectangleGeometry(viewport);
            Position.X = rect.X; Position.Y = rect.Y;
        }
        internal Rect CurrentRect => new(Position.X, Position.Y, _rect.Width, _rect.Height);
        internal void SetGeometry(Rect rect, Rect viewport)
        { _rect = rect; _clip = new RectangleGeometry(viewport); _clip.Freeze(); InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.PushClip(_clip);
            drawing.PushTransform(Position);
            drawing.DrawRectangle(_brush, null, new Rect(0, 0, _rect.Width, _rect.Height));
            drawing.Pop(); drawing.Pop();
        }
    }
}
