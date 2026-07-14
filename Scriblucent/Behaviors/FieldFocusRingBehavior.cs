using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Scriblucent.Behaviors;

/// <summary>
/// Draws a focus ring as an adorner so field layout never shifts when focus appears.
/// While the ring is visible, <see cref="CompositionTarget.Rendering"/> keeps the
/// AdornerLayer synced to ancestor RenderTransforms (secondary-window rise/sink).
/// No visual-tree reparenting — that previously crashed on field click.
/// </summary>
public static class FieldFocusRingBehavior
{
    private sealed class State
    {
        public bool IsAttached;
        public FrameworkElement? Element;
        public DependencyPropertyChangedEventHandler? FocusWithinHandler;
        public SizeChangedEventHandler? SizeChangedHandler;
        public RoutedEventHandler? LoadedHandler;
        public FieldFocusRingAdorner? Adorner;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(FieldFocusRingBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(FieldFocusRingBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly HashSet<State> ActiveRings = new();
    private static bool _renderingHooked;
    private static bool _rendering;

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
            Attach(element);
        else
            Detach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        state.Element = element;
        if (state.IsAttached)
        {
            Sync(element, state);
            return;
        }

        state.IsAttached = true;
        state.FocusWithinHandler = (_, _) => Sync(element, state);
        state.SizeChangedHandler = (_, _) =>
        {
            if (state.Adorner is null)
                return;
            state.Adorner.InvalidateVisual();
            TryUpdateAdorner(element);
        };
        state.LoadedHandler = (_, _) => Sync(element, state);

        element.IsKeyboardFocusWithinChanged += state.FocusWithinHandler;
        element.SizeChanged += state.SizeChangedHandler;
        element.Loaded += state.LoadedHandler;

        Sync(element, state);
    }

    private static void Detach(FrameworkElement element)
    {
        var state = element.GetValue(StateProperty) as State;
        if (state is null || !state.IsAttached)
            return;

        if (state.FocusWithinHandler is not null)
            element.IsKeyboardFocusWithinChanged -= state.FocusWithinHandler;
        if (state.SizeChangedHandler is not null)
            element.SizeChanged -= state.SizeChangedHandler;
        if (state.LoadedHandler is not null)
            element.Loaded -= state.LoadedHandler;

        RemoveAdorner(element, state);
        state.IsAttached = false;
        state.Element = null;
    }

    private static void Sync(FrameworkElement element, State state)
    {
        if (!element.IsLoaded)
            return;

        if (element.IsKeyboardFocusWithin)
            EnsureAdorner(element, state);
        else
            RemoveAdorner(element, state);
    }

    private static void EnsureAdorner(FrameworkElement element, State state)
    {
        if (state.Adorner is not null)
        {
            state.Adorner.InvalidateVisual();
            TryUpdateAdorner(element);
            TrackActive(state, active: true);
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer is null)
            return;

        try
        {
            state.Adorner = new FieldFocusRingAdorner(element);
            layer.Add(state.Adorner);
            layer.Update(element);
            TrackActive(state, active: true);
        }
        catch
        {
            // Adorner layer can be mid-teardown during window close/animation.
            state.Adorner = null;
            TrackActive(state, active: false);
        }
    }

    private static void RemoveAdorner(FrameworkElement element, State state)
    {
        TrackActive(state, active: false);

        if (state.Adorner is null)
            return;

        try
        {
            var layer = AdornerLayer.GetAdornerLayer(element);
            layer?.Remove(state.Adorner);
        }
        catch
        {
            // Ignore teardown races.
        }

        state.Adorner = null;
    }

    private static void TryUpdateAdorner(FrameworkElement element)
    {
        try
        {
            AdornerLayer.GetAdornerLayer(element)?.Update(element);
        }
        catch
        {
            // Ignore if visual is disconnecting.
        }
    }

    private static void TrackActive(State state, bool active)
    {
        if (active)
        {
            if (ActiveRings.Add(state))
                EnsureRenderingHook();
        }
        else
        {
            ActiveRings.Remove(state);
            if (ActiveRings.Count == 0)
                TeardownRenderingHook();
        }
    }

    private static void EnsureRenderingHook()
    {
        if (_renderingHooked)
            return;

        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private static void TeardownRenderingHook()
    {
        if (!_renderingHooked)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_rendering)
            return;

        if (ActiveRings.Count == 0)
        {
            TeardownRenderingHook();
            return;
        }

        _rendering = true;
        try
        {
            // Copy before iterating — Update can trigger focus/layout side effects.
            var snapshot = ActiveRings.ToArray();
            foreach (var state in snapshot)
            {
                var element = state.Element;
                if (element is null || state.Adorner is null || !element.IsLoaded)
                    continue;

                // Re-arrange so ancestor RenderTransforms (window enter/exit lift) apply each frame.
                TryUpdateAdorner(element);
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State existing)
            return existing;

        var state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }

    private sealed class FieldFocusRingAdorner : Adorner
    {
        public FieldFocusRingAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            if (AdornedElement is null)
                return transform;

            var layer = Parent as AdornerLayer ?? AdornerLayer.GetAdornerLayer(AdornedElement);
            if (layer is null)
                return transform;

            try
            {
                // TransformToAncestor is safer than TransformToVisual during partial trees.
                return AdornedElement.TransformToAncestor(layer) ?? transform;
            }
            catch
            {
                return transform;
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (AdornedElement is not FrameworkElement fe)
                return;

            var width = fe.ActualWidth;
            var height = fe.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var thickness = ResolveThickness(fe);
            if (thickness <= 0)
                return;

            var brush = ResolveBrush(fe);
            if (brush is null)
                return;

            var radius = ResolveRadius(fe);
            var inset = thickness * 0.5;
            var rect = new Rect(
                inset,
                inset,
                Math.Max(0, width - thickness),
                Math.Max(0, height - thickness));

            var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            if (pen.CanFreeze)
                pen.Freeze();

            drawingContext.DrawRoundedRectangle(null, pen, rect, radius, radius);
        }

        private static double ResolveThickness(FrameworkElement fe)
        {
            if (FindThemeResource(fe, "Field.FocusBorderThickness") is Thickness t)
                return Math.Max(t.Left, Math.Max(t.Top, Math.Max(t.Right, t.Bottom)));

            return 2;
        }

        private static Brush? ResolveBrush(FrameworkElement fe)
        {
            if (FindThemeResource(fe, "AccentPrimary") is Brush brush)
                return brush;

            return null;
        }

        private static double ResolveRadius(FrameworkElement fe)
        {
            if (fe is Border border)
                return Math.Max(0, border.CornerRadius.TopLeft);

            if (FindThemeResource(fe, "Radius.Control") is CornerRadius token)
                return Math.Max(0, token.TopLeft);

            return 10;
        }

        private static object? FindThemeResource(FrameworkElement fe, string key)
        {
            try
            {
                return fe.TryFindResource(key);
            }
            catch
            {
                return null;
            }
        }
    }
}
