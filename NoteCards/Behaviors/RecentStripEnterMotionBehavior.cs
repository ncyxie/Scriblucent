using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NoteCards.Animations;

namespace NoteCards.Behaviors;

/// <summary>
/// Staggered enter for horizontal recent-item strips when the collection refreshes.
/// </summary>
public static class RecentStripEnterMotionBehavior
{
    private const int EnterMs = 260;
    private const int StaggerMs = 55;
    private const double EnterLiftY = 8;

    private const int EnterReadyMaxAttempts = 32;

    private sealed class State
    {
        public bool IsAttached;
        public bool IsReady;
        public bool HoldEnterPrep;
        public INotifyCollectionChanged? ObservedCollection;
        public NotifyCollectionChangedEventHandler? CollectionChangedHandler;
        public EventHandler? GeneratorStatusChangedHandler;
        public RoutedEventHandler? LoadedHandler;
        public EventHandler? ItemsSourceChangedHandler;
        public DispatcherTimer? CoalesceTimer;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(RecentStripEnterMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(RecentStripEnterMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Keeps recent cards hidden during dashboard cross-fade so only the stagger enter is visible.</summary>
    public static void PrepEnterHold(ItemsControl itemsControl)
    {
        if (!GetIsEnabled(itemsControl) || UiMotion.PrefersReducedMotion)
            return;

        var state = GetOrCreateState(itemsControl);
        if (!state.IsAttached)
            Attach(itemsControl);

        state.HoldEnterPrep = true;
        if (itemsControl.IsLoaded)
            state.IsReady = true;

        itemsControl.UpdateLayout();
        PrepAllContainers(itemsControl);
    }

    public static void TryPrepEnterHold(DependencyObject root)
    {
        foreach (var control in EnumerateRecentStripControls(root))
            PrepEnterHold(control);
    }

    /// <summary>Replays stagger enter on an already-bound recent strip (e.g. after dashboard switch).</summary>
    public static void RequestEnterAnimation(ItemsControl itemsControl)
    {
        if (!GetIsEnabled(itemsControl))
            return;

        var state = GetOrCreateState(itemsControl);
        if (!state.IsAttached)
            Attach(itemsControl);

        state.HoldEnterPrep = false;
        if (itemsControl.IsLoaded)
            state.IsReady = true;

        ScheduleEnterWhenReady(itemsControl, state, attempt: 0);
    }

    public static void TryRequestEnterAnimation(DependencyObject root)
    {
        foreach (var control in EnumerateRecentStripControls(root))
            RequestEnterAnimation(control);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl)
            return;

        if (e.NewValue is true)
            Attach(itemsControl);
        else
            Detach(itemsControl);
    }

    private static void Attach(ItemsControl itemsControl)
    {
        var state = GetOrCreateState(itemsControl);
        if (state.IsAttached)
            return;

        state.LoadedHandler = (_, _) =>
        {
            state.IsReady = true;
            ObserveItemsSource(itemsControl, state);
            ScheduleAnimate(itemsControl, state);
        };
        state.GeneratorStatusChangedHandler = (_, _) =>
        {
            if (itemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                return;

            if (state.HoldEnterPrep)
                PrepAllContainers(itemsControl);
            else
                ScheduleAnimate(itemsControl, state);
        };
        state.ItemsSourceChangedHandler = (_, _) =>
        {
            ObserveItemsSource(itemsControl, state);
            ScheduleAnimate(itemsControl, state);
        };

        itemsControl.Loaded += state.LoadedHandler;
        itemsControl.Unloaded += (_, _) => Detach(itemsControl);
        itemsControl.ItemContainerGenerator.StatusChanged += state.GeneratorStatusChangedHandler;
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl))
            .AddValueChanged(itemsControl, state.ItemsSourceChangedHandler);
        state.IsAttached = true;

        if (itemsControl.IsLoaded)
        {
            state.IsReady = true;
            ObserveItemsSource(itemsControl, state);
        }
    }

    private static void Detach(ItemsControl itemsControl)
    {
        if (itemsControl.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            itemsControl.Loaded -= state.LoadedHandler;
        if (state.GeneratorStatusChangedHandler != null)
            itemsControl.ItemContainerGenerator.StatusChanged -= state.GeneratorStatusChangedHandler;
        if (state.ItemsSourceChangedHandler != null)
            DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl))
                .RemoveValueChanged(itemsControl, state.ItemsSourceChangedHandler);

        UnsubscribeCollection(state);
        state.CoalesceTimer?.Stop();
        state.IsAttached = false;
        state.IsReady = false;
    }

    private static void ObserveItemsSource(ItemsControl itemsControl, State state)
    {
        UnsubscribeCollection(state);

        if (itemsControl.ItemsSource is not INotifyCollectionChanged collection)
            return;

        state.ObservedCollection = collection;
        state.CollectionChangedHandler = (_, _) => ScheduleAnimate(itemsControl, state);
        collection.CollectionChanged += state.CollectionChangedHandler;
    }

    private static void UnsubscribeCollection(State state)
    {
        if (state.ObservedCollection == null || state.CollectionChangedHandler == null)
            return;

        state.ObservedCollection.CollectionChanged -= state.CollectionChangedHandler;
        state.ObservedCollection = null;
        state.CollectionChangedHandler = null;
    }

    private static void ScheduleAnimate(ItemsControl itemsControl, State state)
    {
        if (!state.IsReady || itemsControl.Items.Count == 0 || state.HoldEnterPrep)
            return;

        state.CoalesceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        state.CoalesceTimer.Stop();
        state.CoalesceTimer.Tag = itemsControl;
        state.CoalesceTimer.Tick -= OnCoalesceTick;
        state.CoalesceTimer.Tick += OnCoalesceTick;
        state.CoalesceTimer.Start();
    }

    private static void ScheduleEnterWhenReady(ItemsControl itemsControl, State state, int attempt)
    {
        if (!state.IsReady && itemsControl.IsLoaded)
            state.IsReady = true;

        if (!state.IsReady || itemsControl.Items.Count == 0)
            return;

        itemsControl.UpdateLayout();

        var elements = CollectContainers(itemsControl);
        var generatorReady = itemsControl.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated;
        if (!generatorReady || elements.Count < itemsControl.Items.Count)
        {
            if (attempt < EnterReadyMaxAttempts)
            {
                itemsControl.Dispatcher.BeginInvoke(
                    () => ScheduleEnterWhenReady(itemsControl, state, attempt + 1),
                    attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.ApplicationIdle);
            }

            return;
        }

        ScheduleAnimate(itemsControl, state);
    }

    private static void OnCoalesceTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer)
            return;

        timer.Stop();
        timer.Tick -= OnCoalesceTick;
        if (timer.Tag is ItemsControl target)
            PlayEnter(target);
    }

    private static void PlayEnter(ItemsControl itemsControl)
    {
        if (itemsControl.GetValue(StateProperty) is State state)
            state.HoldEnterPrep = false;

        var elements = CollectContainers(itemsControl);
        if (elements.Count == 0 || elements.Count < itemsControl.Items.Count)
            return;

        if (UiMotion.ShouldSkipDashboardRevealMotion())
        {
            foreach (var element in elements)
            {
                UiMotion.EnsureContentRevealRestState(element);
                var scale = UiMotion.EnsureScale(element);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            return;
        }

        UiMotion.AnimateStaggeredContentEnter(
            elements,
            durationMs: EnterMs,
            offsetY: EnterLiftY,
            staggerMs: StaggerMs);
    }

    private static List<FrameworkElement> CollectContainers(ItemsControl itemsControl)
    {
        var elements = new List<FrameworkElement>();
        for (var index = 0; index < itemsControl.Items.Count; index++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
                elements.Add(container);
        }

        return elements;
    }

    private static void PrepAllContainers(ItemsControl itemsControl)
    {
        foreach (var element in CollectContainers(itemsControl))
            PrepElementEnterStart(element);
    }

    private static void PrepElementEnterStart(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        UiMotion.Stop(element);
        UiMotion.Clear(element, UIElement.OpacityProperty);
        var translate = UiMotion.EnsureTranslate(element);
        UiMotion.Clear(translate, TranslateTransform.YProperty);
        var scale = UiMotion.EnsureScale(element, UiMotion.StaggerEnterScale);
        UiMotion.Clear(scale, ScaleTransform.ScaleXProperty);
        UiMotion.Clear(scale, ScaleTransform.ScaleYProperty);
        element.Opacity = 0;
        translate.Y = EnterLiftY;
        scale.ScaleX = UiMotion.StaggerEnterScale;
        scale.ScaleY = UiMotion.StaggerEnterScale;
    }

    private static State GetOrCreateState(ItemsControl itemsControl)
    {
        if (itemsControl.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        itemsControl.SetValue(StateProperty, state);
        return state;
    }

    private static IEnumerable<ItemsControl> EnumerateRecentStripControls(DependencyObject root)
    {
        if (root is ItemsControl itemsControl && GetIsEnabled(itemsControl))
            yield return itemsControl;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateRecentStripControls(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }
}