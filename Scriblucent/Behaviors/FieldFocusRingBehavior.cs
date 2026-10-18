using Scriblucent;
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
    static FieldFocusRingBehavior()
    {
        Scriblucent.Animations.RenderTransitionClock.ActivityChanged += () =>
        {
            if (Scriblucent.Animations.RenderTransitionClock.IsAnyRunning && ActiveRings.Count > 0)
                EnsureRenderingHook();
            else
            {
                OnRendering(null, EventArgs.Empty);
                TeardownRenderingHook();
            }
        };
    }
    private sealed class State
    {
        public bool IsAttached;
        public FrameworkElement? Element;
        public DependencyPropertyChangedEventHandler? FocusWithinHandler;
        public SizeChangedEventHandler? SizeChangedHandler;
        public RoutedEventHandler? LoadedHandler;
        public FieldFocusRingAdorner? Adorner;
        public Window? Window;
        public ScrollChangedEventHandler? ScrollHandler;
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
    private static bool _appEventsHooked;
    private static bool _appIsActive = true;

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
        element.Unloaded += OnUnloaded;

        Sync(element, state);
    }

    private static void Detach(FrameworkElement element)
    {
        element.Unloaded -= OnUnloaded;
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

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.GetValue(StateProperty) is State state)
            RemoveAdorner(element, state);
    }

    private static void Sync(FrameworkElement element, State state)
    {
        if (!element.IsLoaded)
            return;

        if (element.IsKeyboardFocusWithin)
            EnsureAdorner(element, state);
        else
            RemoveAdorner(element, state, animate: true);
    }

    private static void EnsureAdorner(FrameworkElement element, State state)
    {
        if (state.Adorner is not null)
        {
            Scriblucent.Animations.UiMotion.Fade(state.Adorner, 1, 160, Scriblucent.Animations.UiMotion.OutSine);
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
            state.Adorner = new FieldFocusRingAdorner(element) { Opacity = .35 };
            layer.Add(state.Adorner);
            Scriblucent.Animations.UiMotion.Fade(state.Adorner, 1, 160, Scriblucent.Animations.UiMotion.OutSine);
            layer.Update(element);
            state.Window = Window.GetWindow(element);
            state.ScrollHandler = (_, _) => TryUpdateAdorner(element);
            state.Window?.AddHandler(ScrollViewer.ScrollChangedEvent, state.ScrollHandler);
            TrackActive(state, active: true);
        }
        catch
        {
            // Adorner layer can be mid-teardown during window close/animation.
            state.Adorner = null;
            TrackActive(state, active: false);
        }
    }

    private static void RemoveAdorner(FrameworkElement element, State state, bool animate = false)
    {
        if (animate && state.Adorner is { } fading && element.IsLoaded)
        {
            Scriblucent.Animations.UiMotion.Fade(fading, 0, 130, Scriblucent.Animations.UiMotion.OutSine,
                () => { if (!element.IsKeyboardFocusWithin && ReferenceEquals(state.Adorner, fading)) RemoveAdorner(element, state); });
            return;
        }
        if (state.Adorner is { } stopping) Scriblucent.Animations.SurfaceMotion.Stop(stopping);
        if (state.ScrollHandler is not null) state.Window?.RemoveHandler(ScrollViewer.ScrollChangedEvent, state.ScrollHandler);
        state.ScrollHandler = null;
        state.Window = null;
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
        EnsureAppEventsHooked();

        if (_renderingHooked || !_appIsActive || !Scriblucent.Animations.RenderTransitionClock.IsAnyRunning)
            return;

        Scriblucent.Animations.RenderTransitionClock.Rendered += OnRendering;
        _renderingHooked = true;
    }

    private static void TeardownRenderingHook()
    {
        if (!_renderingHooked)
            return;

        Scriblucent.Animations.RenderTransitionClock.Rendered -= OnRendering;
        _renderingHooked = false;
    }

    // The per-frame sync is only useful while some window is actually visible on screen.
    // WPF does not clear IsKeyboardFocusWithin on minimize/deactivate, so without this the
    // render hook (and ActiveRings) would otherwise stay alive and keep ticking every frame
    // forever after a field was ever focused, even with the app minimized or backgrounded.
    private static void EnsureAppEventsHooked()
    {
        if (_appEventsHooked || Application.Current is not { } app)
            return;

        _appEventsHooked = true;
        app.Activated += (_, _) =>
        {
            _appIsActive = true;
            if (ActiveRings.Count > 0)
                EnsureRenderingHook();
        };
        app.Deactivated += (_, _) =>
        {
            _appIsActive = false;
            TeardownRenderingHook();
        };
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
        private (Size Size, CornerRadius Radius, Thickness Frame, double Stroke)? _geometryKey;
        private Geometry? _geometry;
        private static readonly DependencyProperty RimBrushProperty = DependencyProperty.Register("RimBrush", typeof(Brush), typeof(FieldFocusRingAdorner),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public FieldFocusRingAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            SetResourceReference(RimBrushProperty, "AccentRimBrush");
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

            var brush = GetValue(RimBrushProperty) as Brush;
            if (brush is null)
                return;

            var radius = fe is Border face ? face.CornerRadius : new CornerRadius(ResolveRadius(fe));
            var frame = fe is Border border ? Scriblucent.Animations.SurfaceContour.EffectiveFrame(border) : new Thickness();
            var key = (fe.RenderSize, radius, frame, thickness);
            if (_geometryKey != key)
            {
                _geometryKey = key;
                _geometry = Scriblucent.Animations.SurfaceContour.Ring(
                    Scriblucent.Animations.SurfaceContour.Create(fe.RenderSize, radius, frame),
                    Scriblucent.Animations.SurfaceContour.Create(fe.RenderSize, radius, frame, thickness));
            }
            drawingContext.DrawGeometry(brush, null, _geometry);
        }

        // Focus thickness is a shared token; paint uses a dynamic rim brush on the adorner.
        private static double? _cachedThickness;
        private static bool _themeChangedHooked;

        private static void EnsureThemeInvalidationHooked()
        {
            if (_themeChangedHooked)
                return;

            _themeChangedHooked = true;
            ThemeManager.ThemeChanged += (_, _) =>
            {
                _cachedThickness = null;
            };
        }

        private static double ResolveThickness(FrameworkElement fe)
        {
            EnsureThemeInvalidationHooked();
            if (_cachedThickness is { } cached)
                return cached;

            var thickness = FindThemeResource(fe, "Field.FocusBorderThickness") is Thickness t
                ? Math.Max(t.Left, Math.Max(t.Top, Math.Max(t.Right, t.Bottom)))
                : 2;
            _cachedThickness = thickness;
            return thickness;
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
