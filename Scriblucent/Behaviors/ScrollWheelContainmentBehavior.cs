using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Behaviors;

/// <summary>
/// Keeps mouse-wheel input inside a <see cref="ScrollViewer"/> so open popups do not scroll the page behind them.
/// </summary>
public static class ScrollWheelContainmentBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ScrollWheelContainmentBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if ((bool)e.NewValue)
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        var next = scrollViewer.VerticalOffset - e.Delta;
        if (next < 0)
            next = 0;
        else if (next > scrollViewer.ScrollableHeight)
            next = scrollViewer.ScrollableHeight;

        scrollViewer.ScrollToVerticalOffset(next);
        e.Handled = true;
    }
}
