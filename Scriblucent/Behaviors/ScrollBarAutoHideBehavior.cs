using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

public static class ScrollBarAutoHideBehavior
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(AutoHideState),
        typeof(ScrollBarAutoHideBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable",
        typeof(bool),
        typeof(ScrollBarAutoHideBehavior),
        new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value)
    {
        element.SetValue(EnableProperty, value);
    }

    public static bool GetEnable(DependencyObject element)
    {
        return (bool)element.GetValue(EnableProperty);
    }

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if ((bool)e.NewValue)
        {
            Attach(scrollViewer);
            return;
        }

        Detach(scrollViewer);
    }

    private static void Attach(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is AutoHideState)
            return;

        var state = new AutoHideState(scrollViewer);
        scrollViewer.SetValue(StateProperty, state);

        scrollViewer.Loaded += ScrollViewer_Loaded;
        scrollViewer.Unloaded += ScrollViewer_Unloaded;
        scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        scrollViewer.PreviewMouseMove += ScrollViewer_Interaction;
        scrollViewer.PreviewMouseWheel += ScrollViewer_Interaction;
        scrollViewer.PreviewMouseDown += ScrollViewer_Interaction;
        scrollViewer.PreviewKeyDown += ScrollViewer_Interaction;
        scrollViewer.MouseEnter += ScrollViewer_Interaction;

        if (scrollViewer.IsLoaded)
            InitializeState(scrollViewer, state);
    }

    private static void Detach(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is not AutoHideState state)
            return;

        scrollViewer.ClearValue(StateProperty);

        scrollViewer.Loaded -= ScrollViewer_Loaded;
        scrollViewer.Unloaded -= ScrollViewer_Unloaded;
        scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
        scrollViewer.PreviewMouseMove -= ScrollViewer_Interaction;
        scrollViewer.PreviewMouseWheel -= ScrollViewer_Interaction;
        scrollViewer.PreviewMouseDown -= ScrollViewer_Interaction;
        scrollViewer.PreviewKeyDown -= ScrollViewer_Interaction;
        scrollViewer.MouseEnter -= ScrollViewer_Interaction;

        state.Dispose();
    }

    private static void ScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.GetValue(StateProperty) is not AutoHideState state)
            return;

        InitializeState(scrollViewer, state);
    }

    private static void ScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.GetValue(StateProperty) is not AutoHideState state)
            return;

        state.Timer.Stop();
    }

    private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.GetValue(StateProperty) is not AutoHideState state)
            return;

        if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0)
            state.RefreshScrollBars();

        ShowScrollBars(state);
    }

    private static void ScrollViewer_Interaction(object? sender, EventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.GetValue(StateProperty) is not AutoHideState state)
            return;

        ShowScrollBars(state);
    }

    private static void InitializeState(ScrollViewer scrollViewer, AutoHideState state)
    {
        scrollViewer.Dispatcher.BeginInvoke(() =>
        {
            state.RefreshScrollBars();
            ShowScrollBars(state);
        }, DispatcherPriority.Loaded);
    }

    private static void ShowScrollBars(AutoHideState state)
    {
        AnimateScrollBars(state.ScrollBars, 1.0);
        state.Timer.Stop();
        state.Timer.Start();
    }

    private static void HideScrollBars(AutoHideState state)
    {
        if (state.Owner.IsMouseOver || state.Owner.IsKeyboardFocusWithin)
        {
            ShowScrollBars(state);
            return;
        }

        if (state.ScrollBars.Any(sb => sb.IsMouseOver || sb.IsMouseCaptureWithin))
        {
            ShowScrollBars(state);
            return;
        }

        AnimateScrollBars(state.ScrollBars, 0);
    }

    private static void AnimateScrollBars(IReadOnlyList<ScrollBar> scrollBars, double targetOpacity)
    {
        foreach (var scrollBar in scrollBars)
        {
            if (scrollBar.Visibility != Visibility.Visible)
                continue;

            UiMotion.Clear(scrollBar, UIElement.OpacityProperty);
            UiMotion.Fade(scrollBar, targetOpacity, 180, UiMotion.OutQuad);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    private sealed class AutoHideState : IDisposable
    {
        public AutoHideState(ScrollViewer owner)
        {
            Owner = owner;
            Timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2.4)
            };
            Timer.Tick += Timer_Tick;
        }

        public ScrollViewer Owner { get; }
        public DispatcherTimer Timer { get; }
        public List<ScrollBar> ScrollBars { get; } = [];

        public void RefreshScrollBars()
        {
            Owner.ApplyTemplate();
            ScrollBars.Clear();
            ScrollBars.AddRange(FindVisualChildren<ScrollBar>(Owner));
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            HideScrollBars(this);
        }

        public void Dispose()
        {
            Timer.Stop();
            Timer.Tick -= Timer_Tick;
        }
    }
}
