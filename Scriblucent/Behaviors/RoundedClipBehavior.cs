using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Clips a FrameworkElement to a rounded rectangle. WPF Border.CornerRadius only
/// rounds the border/background — children still paint square corners unless Clip is set.
/// </summary>
public static class RoundedClipBehavior
{
    private sealed class State
    {
        public bool IsAttached;
        public SizeChangedEventHandler? SizeChangedHandler;
        public DependencyPropertyDescriptor? CornerRadiusDescriptor;
        public EventHandler? CornerRadiusChangedHandler;
        public DependencyPropertyDescriptor? BorderThicknessDescriptor;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Optional explicit radius. When unset (all zeros) and the target is a Border,
    /// Border.CornerRadius is used.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius",
        typeof(CornerRadius),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(new CornerRadius(), OnCornerRadiusChanged));

    /// <summary>
    /// DIPs left unpainted at the top so a full-window dialog does not cover the caption stroke.
    /// The element still lays out at full size, so a 1:1 backdrop does not shift.
    /// </summary>
    public static readonly DependencyProperty TopInsetProperty = DependencyProperty.RegisterAttached(
        "TopInset",
        typeof(double),
        typeof(RoundedClipBehavior),
        new PropertyMetadata(0d, OnCornerRadiusChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value)
        => element.SetValue(CornerRadiusProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject element)
        => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetTopInset(DependencyObject element, double value)
        => element.SetValue(TopInsetProperty, value);

    public static double GetTopInset(DependencyObject element)
        => (double)element.GetValue(TopInsetProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
            Attach(element);
        else
            Detach(element);
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && GetIsEnabled(element))
            UpdateClip(element);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        if (state.IsAttached)
        {
            UpdateClip(element);
            return;
        }

        state.IsAttached = true;
        state.SizeChangedHandler = (_, _) => UpdateClip(element);
        element.SizeChanged += state.SizeChangedHandler;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;

        if (element is Border)
        {
            state.CornerRadiusChangedHandler = (_, _) => UpdateClip(element);
            state.CornerRadiusDescriptor = DependencyPropertyDescriptor.FromProperty(
                Border.CornerRadiusProperty,
                typeof(Border));
            state.CornerRadiusDescriptor?.AddValueChanged(element, state.CornerRadiusChangedHandler);
            state.BorderThicknessDescriptor = DependencyPropertyDescriptor.FromProperty(Border.BorderThicknessProperty, typeof(Border));
            state.BorderThicknessDescriptor?.AddValueChanged(element, state.CornerRadiusChangedHandler);
        }

        UpdateClip(element);
    }

    private static void Detach(FrameworkElement element)
    {
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        var state = element.GetValue(StateProperty) as State;
        if (state is null || !state.IsAttached)
            return;

        if (state.SizeChangedHandler is not null)
            element.SizeChanged -= state.SizeChangedHandler;
        element.Loaded -= OnLoaded;

        if (state.CornerRadiusDescriptor is not null && state.CornerRadiusChangedHandler is not null)
            state.CornerRadiusDescriptor.RemoveValueChanged(element, state.CornerRadiusChangedHandler);
        if (state.BorderThicknessDescriptor is not null && state.CornerRadiusChangedHandler is not null)
            state.BorderThicknessDescriptor.RemoveValueChanged(element, state.CornerRadiusChangedHandler);

        state.IsAttached = false;
        element.Clip = null;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && GetIsEnabled(element)) Attach(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        Detach(element);
        if (GetIsEnabled(element)) element.Loaded += OnLoaded;
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State existing)
            return existing;

        var state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }

    private static void UpdateClip(FrameworkElement element)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            element.Clip = null;
            return;
        }

        var topInset = Math.Max(0, GetTopInset(element));
        if (topInset >= height)
            topInset = 0;

        var radius = ResolveRadius(element);
        if (element is Border border || radius.TopLeft != radius.TopRight || radius.TopLeft != radius.BottomLeft || radius.TopLeft != radius.BottomRight)
        {
            var contour = SurfaceContour.Create(new Size(width, height - topInset), radius,
                element is Border frame ? SurfaceContour.EffectiveFrame(frame) : new Thickness());
            if (topInset > 0) { contour = contour.Clone(); contour.Transform = new TranslateTransform(0, topInset); contour.Freeze(); }
            element.Clip = contour;
            return;
        }
        var rx = Math.Max(0, Math.Min(radius.TopLeft, width / 2));
        var ry = Math.Max(0, Math.Min(radius.TopLeft, (height - topInset) / 2));

        var rect = new Rect(0, topInset, width, height - topInset);
        if (element.Clip is RectangleGeometry existing)
        {
            existing.Rect = rect;
            existing.RadiusX = rx;
            existing.RadiusY = ry;
            return;
        }

        element.Clip = new RectangleGeometry(rect, rx, ry);
    }

    private static CornerRadius ResolveRadius(FrameworkElement element)
    {
        var explicitRadius = GetCornerRadius(element);
        if (explicitRadius.TopLeft > 0
            || explicitRadius.TopRight > 0
            || explicitRadius.BottomLeft > 0
            || explicitRadius.BottomRight > 0)
        {
            return explicitRadius;
        }

        if (element is Border border)
            return border.CornerRadius;

        return default;
    }
}
