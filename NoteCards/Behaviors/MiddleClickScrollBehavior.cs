using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NoteCards.Controls;

namespace NoteCards.Behaviors;

/// <summary>
/// Browser-style middle-click autoscroll. Middle-click sets an anchor; moving the pointer away
/// scrolls continuously. Any click or Escape ends the session.
/// </summary>
public static class MiddleClickScrollBehavior
{
    private const double ClickMoveThreshold = 5;
    private const double AutoscrollDeadZone = 15;
    private const double AutoscrollExponent = 2.2;
    private const double AutoscrollScale = 24;
    private const double MaxAutoscrollSpeed = 4000;
    private const double VelocitySmoothing = 18;
    private const double IndicatorSize = 40;
    private const double MinFrameSeconds = 1.0 / 240.0;
    private const double MaxFrameSeconds = 1.0 / 45.0;
    private const int MaxSubstepsPerFrame = 2;

    private static AutoscrollSession? _activeSession;
    private static EventHandler? _renderingHandler;
    private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    private sealed class State
    {
        public bool IsAttached;
        public bool HasPendingMiddleClick;
        public Point PendingAnchor;
        public MouseButtonEventHandler? MouseDownHandler;
        public MouseButtonEventHandler? MouseUpHandler;
    }

    private sealed class AutoscrollSession
    {
        public required ScrollViewer ScrollViewer { get; init; }
        public required Window Window { get; init; }
        public required Point AnchorScreen { get; init; }
        public required Canvas OverlayLayer { get; init; }
        public IndicatorVisual? Indicator;
        public double VelocityX;
        public double VelocityY;
        public double CurrentX;
        public double CurrentY;
        public MouseButtonEventHandler? WindowMouseDownHandler;
        public KeyEventHandler? WindowKeyDownHandler;
        public DependencyPropertyChangedEventHandler? VisibilityHandler;
    }

    private sealed class IndicatorVisual
    {
        public required Canvas Root { get; init; }
        public required Shape ArrowUp { get; init; }
        public required Shape ArrowDown { get; init; }
        public required Shape ArrowLeft { get; init; }
        public required Shape ArrowRight { get; init; }
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(MiddleClickScrollBehavior),
        new PropertyMetadata(null));

    private static readonly DependencyProperty OverlayLayerProperty = DependencyProperty.RegisterAttached(
        "OverlayLayer",
        typeof(Canvas),
        typeof(MiddleClickScrollBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MiddleClickScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if (e.NewValue is true)
            Attach(scrollViewer);
        else
            Detach(scrollViewer);
    }

    private static void Attach(ScrollViewer scrollViewer)
    {
        var state = GetOrCreateState(scrollViewer);
        if (state.IsAttached)
            return;

        state.MouseDownHandler = (_, args) => OnPreviewMouseDown(scrollViewer, state, args);
        state.MouseUpHandler = (_, args) => OnPreviewMouseUp(scrollViewer, state, args);

        scrollViewer.PreviewMouseDown += state.MouseDownHandler;
        scrollViewer.PreviewMouseUp += state.MouseUpHandler;
        scrollViewer.IsVisibleChanged += OnIsVisibleChanged;
        state.IsAttached = true;
    }

    private static void Detach(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        state.HasPendingMiddleClick = false;

        if (_activeSession?.ScrollViewer == scrollViewer)
            EndSession();

        if (state.MouseDownHandler != null)
            scrollViewer.PreviewMouseDown -= state.MouseDownHandler;
        if (state.MouseUpHandler != null)
            scrollViewer.PreviewMouseUp -= state.MouseUpHandler;
        scrollViewer.IsVisibleChanged -= OnIsVisibleChanged;

        state.IsAttached = false;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        if (e.NewValue is false && _activeSession?.ScrollViewer == scrollViewer)
            EndSession();
    }

    private static void OnPreviewMouseDown(ScrollViewer scrollViewer, State state, MouseButtonEventArgs e)
    {
        if (_activeSession != null)
            return;

        if (e.ChangedButton != MouseButton.Middle || e.MiddleButton != MouseButtonState.Pressed)
            return;

        if (!CanScroll(scrollViewer))
            return;

        state.HasPendingMiddleClick = true;
        state.PendingAnchor = e.GetPosition(scrollViewer);
    }

    private static void OnPreviewMouseUp(ScrollViewer scrollViewer, State state, MouseButtonEventArgs e)
    {
        if (_activeSession != null)
            return;

        if (e.ChangedButton != MouseButton.Middle || !state.HasPendingMiddleClick)
            return;

        state.HasPendingMiddleClick = false;

        if (!CanScroll(scrollViewer))
            return;

        var releasePoint = e.GetPosition(scrollViewer);
        if ((releasePoint - state.PendingAnchor).Length > ClickMoveThreshold)
            return;

        StartSession(scrollViewer, releasePoint);
        e.Handled = true;
    }

    private static void StartSession(ScrollViewer scrollViewer, Point anchorInViewer)
    {
        EndSession();

        var window = Window.GetWindow(scrollViewer);
        if (window == null)
            return;

        var overlay = GetOrCreateOverlayLayer(window);
        if (overlay == null)
            return;

        var anchorScreen = scrollViewer.PointToScreen(anchorInViewer);
        var indicator = CreateIndicatorVisual();
        var anchorOnOverlay = overlay.PointFromScreen(anchorScreen);
        Canvas.SetLeft(indicator.Root, anchorOnOverlay.X - IndicatorSize / 2);
        Canvas.SetTop(indicator.Root, anchorOnOverlay.Y - IndicatorSize / 2);
        overlay.Children.Add(indicator.Root);

        var session = new AutoscrollSession
        {
            ScrollViewer = scrollViewer,
            Window = window,
            AnchorScreen = anchorScreen,
            OverlayLayer = overlay,
            Indicator = indicator,
        };

        session.WindowMouseDownHandler = (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Middle)
                return;

            EndSession();
        };
        session.WindowKeyDownHandler = (_, args) =>
        {
            if (args.Key == Key.Escape)
                EndSession();
        };
        session.VisibilityHandler = (_, args) =>
        {
            if (args.NewValue is false)
                EndSession();
        };

        window.PreviewMouseDown += session.WindowMouseDownHandler;
        window.PreviewKeyDown += session.WindowKeyDownHandler;
        scrollViewer.IsVisibleChanged += session.VisibilityHandler;

        session.CurrentX = scrollViewer.HorizontalOffset;
        session.CurrentY = scrollViewer.VerticalOffset;

        DashboardScrollViewportHelper.BeginAnimatedScroll(scrollViewer);
        SmoothScrollBehavior.SetExternalDrive(scrollViewer, true);
        SmoothScrollBehavior.NotifyOffsetChanged(scrollViewer, session.CurrentX, session.CurrentY);
        _activeSession = session;
        _lastRenderingTime = TimeSpan.MinValue;

        if (_renderingHandler == null)
        {
            _renderingHandler = OnRendering;
            CompositionTarget.Rendering += _renderingHandler;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_activeSession == null)
        {
            StopRendering();
            return;
        }

        if (e is RenderingEventArgs renderingArgs
            && renderingArgs.RenderingTime == _lastRenderingTime)
            return;

        double deltaSeconds = 1.0 / 60.0;
        if (e is RenderingEventArgs re)
        {
            if (_lastRenderingTime != TimeSpan.MinValue)
            {
                deltaSeconds = (re.RenderingTime - _lastRenderingTime).TotalSeconds;
                deltaSeconds = Math.Clamp(deltaSeconds, MinFrameSeconds, MaxFrameSeconds);
            }

            _lastRenderingTime = re.RenderingTime;
        }

        var session = _activeSession;
        var scrollViewer = session.ScrollViewer;
        var currentScreen = session.Window.PointToScreen(Mouse.GetPosition(session.Window));
        var offset = currentScreen - session.AnchorScreen;
        UpdateIndicatorDirection(session, offset);

        var substeps = SmoothScrollCompositor.IsActive(scrollViewer)
            ? 1
            : MaxSubstepsPerFrame;
        var substepSeconds = deltaSeconds / substeps;
        for (var step = 0; step < substeps; step++)
        {
            if (scrollViewer.ScrollableHeight > 0)
            {
                var targetVelocity = ComputeAutoscrollVelocity(offset.Y);
                session.VelocityY = SmoothToward(session.VelocityY, targetVelocity, substepSeconds);
                session.CurrentY = Clamp(
                    session.CurrentY + session.VelocityY * substepSeconds,
                    0,
                    scrollViewer.ScrollableHeight);
            }

            if (scrollViewer.ScrollableWidth > 0)
            {
                var targetVelocity = ComputeAutoscrollVelocity(offset.X);
                session.VelocityX = SmoothToward(session.VelocityX, targetVelocity, substepSeconds);
                session.CurrentX = Clamp(
                    session.CurrentX + session.VelocityX * substepSeconds,
                    0,
                    scrollViewer.ScrollableWidth);
            }
        }

        if (!SmoothScrollCompositor.TryApply(
                scrollViewer,
                session.CurrentX,
                session.CurrentY,
                forceCommit: false,
                out _,
                out _))
        {
            if (scrollViewer.ScrollableWidth > 0)
                scrollViewer.ScrollToHorizontalOffset(session.CurrentX);
            if (scrollViewer.ScrollableHeight > 0)
                scrollViewer.ScrollToVerticalOffset(session.CurrentY);
        }

        SmoothScrollBehavior.NotifyOffsetChanged(scrollViewer, session.CurrentX, session.CurrentY);

        if (SmoothScrollCompositor.IsActive(scrollViewer))
            DashboardScrollViewportHelper.NotifyVisualScrollOffsetChanged(scrollViewer);
    }

    private static void EndSession()
    {
        if (_activeSession == null)
            return;

        var session = _activeSession;
        _activeSession = null;

        SmoothScrollCompositor.TryApply(
            session.ScrollViewer,
            session.CurrentX,
            session.CurrentY,
            forceCommit: true,
            out _,
            out _);
        DashboardScrollViewportHelper.EndAnimatedScroll(
            session.ScrollViewer,
            session.CurrentX,
            session.CurrentY);
        SmoothScrollBehavior.SetExternalDrive(session.ScrollViewer, false);
        SmoothScrollBehavior.NotifyOffsetChanged(
            session.ScrollViewer,
            session.ScrollViewer.HorizontalOffset,
            session.ScrollViewer.VerticalOffset);

        if (session.Indicator != null)
            session.OverlayLayer.Children.Remove(session.Indicator.Root);

        if (session.WindowMouseDownHandler != null)
            session.Window.PreviewMouseDown -= session.WindowMouseDownHandler;
        if (session.WindowKeyDownHandler != null)
            session.Window.PreviewKeyDown -= session.WindowKeyDownHandler;
        if (session.VisibilityHandler != null)
            session.ScrollViewer.IsVisibleChanged -= session.VisibilityHandler;

        StopRendering();
    }

    private static void StopRendering()
    {
        if (_renderingHandler == null)
            return;

        CompositionTarget.Rendering -= _renderingHandler;
        _renderingHandler = null;
        _lastRenderingTime = TimeSpan.MinValue;
    }

    private static double ComputeAutoscrollVelocity(double offset)
    {
        var magnitude = Math.Abs(offset);
        if (magnitude <= AutoscrollDeadZone)
            return 0;

        var speed = Math.Pow(magnitude - AutoscrollDeadZone, AutoscrollExponent) * AutoscrollScale;
        return Math.Sign(offset) * Math.Min(MaxAutoscrollSpeed, speed);
    }

    private static double SmoothToward(double current, double target, double deltaSeconds)
    {
        var factor = 1.0 - Math.Exp(-VelocitySmoothing * deltaSeconds);
        return current + (target - current) * factor;
    }

    private static bool CanScroll(ScrollViewer scrollViewer)
        => scrollViewer.ScrollableHeight > 0
           || scrollViewer.ScrollableWidth > 0
           || scrollViewer.ExtentHeight > scrollViewer.ViewportHeight + 1
           || scrollViewer.ExtentWidth > scrollViewer.ViewportWidth + 1;

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    private static State GetOrCreateState(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        scrollViewer.SetValue(StateProperty, state);
        return state;
    }

    private static Canvas? GetOrCreateOverlayLayer(Window window)
    {
        if (window.GetValue(OverlayLayerProperty) is Canvas existing)
            return existing;

        if (window.Content is not Panel rootPanel)
            return null;

        var overlay = new Canvas
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
        };
        Panel.SetZIndex(overlay, int.MaxValue);

        if (rootPanel is Grid grid)
        {
            Grid.SetColumnSpan(overlay, Math.Max(1, grid.ColumnDefinitions.Count));
            Grid.SetRowSpan(overlay, Math.Max(1, grid.RowDefinitions.Count));
        }

        rootPanel.Children.Add(overlay);
        window.SetValue(OverlayLayerProperty, overlay);
        return overlay;
    }

    private static IndicatorVisual CreateIndicatorVisual()
    {
        var ringFill = new SolidColorBrush(Color.FromArgb(245, 252, 252, 252));
        var ringStroke = new SolidColorBrush(Color.FromArgb(255, 110, 110, 110));
        var accent = new SolidColorBrush(Color.FromArgb(255, 58, 58, 58));
        ringFill.Freeze();
        ringStroke.Freeze();
        accent.Freeze();

        var canvas = new Canvas
        {
            Width = IndicatorSize,
            Height = IndicatorSize,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Background = Brushes.Transparent,
        };

        var ring = new Ellipse
        {
            Width = 30,
            Height = 30,
            Stroke = ringStroke,
            StrokeThickness = 1.25,
            Fill = ringFill,
        };
        Canvas.SetLeft(ring, 5);
        Canvas.SetTop(ring, 5);

        var dot = new Ellipse { Width = 4, Height = 4, Fill = accent };
        Canvas.SetLeft(dot, 18);
        Canvas.SetTop(dot, 18);

        var arrowUp = CreateChevron(accent, 0);
        Canvas.SetLeft(arrowUp, 16);
        Canvas.SetTop(arrowUp, 8);

        var arrowDown = CreateChevron(accent, 180);
        Canvas.SetLeft(arrowDown, 16);
        Canvas.SetTop(arrowDown, 26);

        var arrowLeft = CreateChevron(accent, 270);
        Canvas.SetLeft(arrowLeft, 8);
        Canvas.SetTop(arrowLeft, 16);

        var arrowRight = CreateChevron(accent, 90);
        Canvas.SetLeft(arrowRight, 26);
        Canvas.SetTop(arrowRight, 16);

        canvas.Children.Add(ring);
        canvas.Children.Add(dot);
        canvas.Children.Add(arrowUp);
        canvas.Children.Add(arrowDown);
        canvas.Children.Add(arrowLeft);
        canvas.Children.Add(arrowRight);

        foreach (var arrow in new[] { arrowUp, arrowDown, arrowLeft, arrowRight })
            arrow.Opacity = 0;

        return new IndicatorVisual
        {
            Root = canvas,
            ArrowUp = arrowUp,
            ArrowDown = arrowDown,
            ArrowLeft = arrowLeft,
            ArrowRight = arrowRight,
        };
    }

    private static Path CreateChevron(Brush fill, double angleDegrees)
        => new()
        {
            Width = 8,
            Height = 8,
            Data = new PathGeometry
            {
                Figures =
                {
                    new PathFigure(new Point(4, 1.5), new[]
                    {
                        new LineSegment(new Point(7, 6.5), true),
                        new LineSegment(new Point(1, 6.5), true),
                    }, true),
                },
            },
            Fill = fill,
            Stretch = Stretch.None,
            RenderTransform = new RotateTransform(angleDegrees, 4, 4),
            Opacity = 0,
        };

    private static void UpdateIndicatorDirection(AutoscrollSession session, Vector offset)
    {
        if (session.Indicator == null)
            return;

        var speedX = session.ScrollViewer.ScrollableWidth > 0 ? ComputeAutoscrollVelocity(offset.X) : 0;
        var speedY = session.ScrollViewer.ScrollableHeight > 0 ? ComputeAutoscrollVelocity(offset.Y) : 0;

        session.Indicator.ArrowUp.Opacity = speedY < 0 ? 1 : 0;
        session.Indicator.ArrowDown.Opacity = speedY > 0 ? 1 : 0;
        session.Indicator.ArrowLeft.Opacity = speedX < 0 ? 1 : 0;
        session.Indicator.ArrowRight.Opacity = speedX > 0 ? 1 : 0;
    }
}