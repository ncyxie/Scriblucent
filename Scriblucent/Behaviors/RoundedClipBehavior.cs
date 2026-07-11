using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value)
        => element.SetValue(CornerRadiusProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject element)
        => (CornerRadius)element.GetValue(CornerRadiusProperty);

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
        element.Loaded += OnLoaded;

        if (element is Border)
        {
            state.CornerRadiusChangedHandler = (_, _) => UpdateClip(element);
            state.CornerRadiusDescriptor = DependencyPropertyDescriptor.FromProperty(
                Border.CornerRadiusProperty,
                typeof(Border));
            state.CornerRadiusDescriptor?.AddValueChanged(element, state.CornerRadiusChangedHandler);
        }

        UpdateClip(element);
    }

    private static void Detach(FrameworkElement element)
    {
        var state = element.GetValue(StateProperty) as State;
        if (state is null || !state.IsAttached)
            return;

        if (state.SizeChangedHandler is not null)
            element.SizeChanged -= state.SizeChangedHandler;
        element.Loaded -= OnLoaded;

        if (state.CornerRadiusDescriptor is not null && state.CornerRadiusChangedHandler is not null)
            state.CornerRadiusDescriptor.RemoveValueChanged(element, state.CornerRadiusChangedHandler);

        state.IsAttached = false;
        element.Clip = null;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            UpdateClip(element);
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

        var radius = ResolveRadius(element);
        var rx = Math.Max(0, Math.Min(radius.TopLeft, width / 2));
        var ry = Math.Max(0, Math.Min(radius.TopLeft, height / 2));

        if (element.Clip is RectangleGeometry existing)
        {
            existing.Rect = new Rect(0, 0, width, height);
            existing.RadiusX = rx;
            existing.RadiusY = ry;
            return;
        }

        element.Clip = new RectangleGeometry(new Rect(0, 0, width, height), rx, ry);
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
