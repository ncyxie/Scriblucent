using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>
/// Enter and exit motion for dashboard pin and schedule icons.
/// </summary>
/// <remarks>
/// <para>Pin-in: <see cref="ScheduleReveal"/> during reflow, played by <see cref="FlushPending"/> after layout settles.</para>
/// <para>Pin-out: plays immediately on attached elements when the source property becomes false.</para>
/// </remarks>
public static class IndicatorRevealMotionBehavior
{
    private const int EnterMs = 160;
    private const int ExitMs = 120;

    public static int RevealAnimationMs => EnterMs;

    public static int DismissAnimationMs => ExitMs;
    private const double EnterLiftY = 4;
    private const int PendingWindowMs = 4000;
    private const int RetryDelayMs = 80;

    private sealed class Registration
    {
        public required WeakReference<FrameworkElement> Element;
        public Guid DocumentId;
        public string PropertyName = string.Empty;
    }

    private sealed class State
    {
        public bool IsAttached;
        public string SourceProperty = string.Empty;
        public Guid RegisteredDocumentId;
        public INotifyPropertyChanged? ObservedContext;
        public PropertyChangedEventHandler? PropertyChangedHandler;
        public DependencyPropertyChangedEventHandler? DataContextChangedHandler;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
    }

    private static readonly object Gate = new();
    private static readonly List<Registration> Registrations = new();
    private static readonly Dictionary<(Guid Id, string Property), long> PendingUntilMs = new();
    private static readonly HashSet<(Guid Id, string Property)> AnimatingKeys = new();
    private static readonly HashSet<(Guid Id, string Property)> PendingDismissKeys = new();
    private static int _flushScheduled;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(IndicatorRevealMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(IndicatorRevealMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SourcePropertyProperty = DependencyProperty.RegisterAttached(
        "SourceProperty",
        typeof(string),
        typeof(IndicatorRevealMotionBehavior),
        new PropertyMetadata(string.Empty, OnSourcePropertyChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetSourceProperty(DependencyObject element, string value)
        => element.SetValue(SourcePropertyProperty, value);

    public static string GetSourceProperty(DependencyObject element)
        => (string)element.GetValue(SourcePropertyProperty);

    public static void ScheduleReveal(object dataContext, string propertyName, bool deferPlayback = false)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return;

        var documentId = GetDocumentId(dataContext);
        if (documentId == Guid.Empty)
            return;

        var key = (documentId, propertyName);
        lock (Gate)
        {
            PendingDismissKeys.Remove(key);
            PendingUntilMs[key] = Environment.TickCount64 + PendingWindowMs;
        }

        if (!deferPlayback && !DashboardMotionEngine.ShouldDeferVirtualizationRefresh())
            RequestPlay(retryDelayMs: 0);
    }

    public static void FlushPending()
        => RequestPlayAfterLayout();

    /// <summary>Clears leaked pin/schedule visuals when a virtualized container is recycled.</summary>
    public static void ResetContainer(DependencyObject root)
    {
        foreach (var element in EnumerateIndicatorElements(root))
            ResetElementVisual(element);
    }

    /// <summary>Re-syncs indicator visuals after a container is bound to a new item.</summary>
    public static void SyncContainer(DependencyObject root)
    {
        foreach (var element in EnumerateIndicatorElements(root))
            SyncElement(element);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.NewValue is true)
            Attach(element);
        else
            Detach(element);
    }

    private static void OnSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        state.SourceProperty = (e.NewValue as string) ?? string.Empty;
        if (element.IsLoaded)
            SyncIndicatorState(element, state);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        if (state.IsAttached)
            return;

        element.RenderTransformOrigin = new Point(0.5, 0.5);

        state.DataContextChangedHandler = (_, _) =>
        {
            ObserveDataContext(element, state);
            if (element.IsLoaded)
                InitializeLoadedElement(element, state);
        };
        state.LoadedHandler = (_, _) => InitializeLoadedElement(element, state);
        state.UnloadedHandler = (_, _) =>
        {
            Unregister(element);
            Detach(element);
        };

        element.DataContextChanged += state.DataContextChangedHandler;
        element.Loaded += state.LoadedHandler;
        element.Unloaded += state.UnloadedHandler;
        state.IsAttached = true;

        if (element.IsLoaded)
            InitializeLoadedElement(element, state);
    }

    private static void InitializeLoadedElement(FrameworkElement element, State state)
    {
        RebindElement(element, state);

        if (HasPendingReveal(element.DataContext, state.SourceProperty)
            && !DashboardMotionEngine.ShouldDeferVirtualizationRefresh())
            RequestPlay(retryDelayMs: 0);
    }

    private static void EnsureRestingIndicatorsVisible(IReadOnlyList<Registration> snapshot)
    {
        var now = Environment.TickCount64;

        foreach (var registration in snapshot)
        {
            if (!registration.Element.TryGetTarget(out var element) || !element.IsLoaded)
                continue;

            if (element.DataContext is null
                || !ReadBoolProperty(element.DataContext, registration.PropertyName))
                continue;

            var key = (registration.DocumentId, registration.PropertyName);
            lock (Gate)
            {
                if (AnimatingKeys.Contains(key) || IsRevealPending(key, now))
                    continue;
            }

            if (element.Visibility != Visibility.Visible || element.Opacity < 0.5)
                ApplyRestingVisible(element);
        }
    }

    private static void RebindElement(FrameworkElement element, State state)
    {
        UiMotion.Stop(element);
        ClearTrackedMotionKeys(state);
        state.SourceProperty = GetSourceProperty(element);
        ObserveDataContext(element, state);
        Register(element, state);
        SyncIndicatorState(element, state);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        UiMotion.Stop(element);
        ClearTrackedMotionKeys(state);

        if (state.DataContextChangedHandler != null)
            element.DataContextChanged -= state.DataContextChangedHandler;
        if (state.LoadedHandler != null)
            element.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler != null)
            element.Unloaded -= state.UnloadedHandler;

        UnobserveDataContext(state);
        state.IsAttached = false;
    }

    private static void ObserveDataContext(FrameworkElement element, State state)
    {
        UnobserveDataContext(state);

        if (element.DataContext is not INotifyPropertyChanged context)
            return;

        state.ObservedContext = context;
        state.PropertyChangedHandler = (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.PropertyName)
                && !string.Equals(args.PropertyName, state.SourceProperty, StringComparison.Ordinal))
                return;

            if (element.DataContext is null || string.IsNullOrWhiteSpace(state.SourceProperty))
                return;

            if (ReadBoolProperty(element.DataContext, state.SourceProperty)
                && !HasPendingReveal(element.DataContext, state.SourceProperty)
                && !DashboardMotionEngine.ShouldDeferVirtualizationRefresh())
                ScheduleReveal(element.DataContext, state.SourceProperty);

            SyncIndicatorState(element, state);
        };
        context.PropertyChanged += state.PropertyChangedHandler;
    }

    private static void UnobserveDataContext(State state)
    {
        if (state.ObservedContext == null || state.PropertyChangedHandler == null)
            return;

        state.ObservedContext.PropertyChanged -= state.PropertyChangedHandler;
        state.ObservedContext = null;
        state.PropertyChangedHandler = null;
    }

    private static void Register(FrameworkElement element, State state)
    {
        var documentId = GetDocumentId(element.DataContext);
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(state.SourceProperty))
            return;

        lock (Gate)
        {
            PurgeDeadRegistrations_NoLock();
            for (var index = Registrations.Count - 1; index >= 0; index--)
            {
                if (Registrations[index].Element.TryGetTarget(out var existing)
                    && ReferenceEquals(existing, element))
                    Registrations.RemoveAt(index);
            }

            Registrations.Add(new Registration
            {
                Element = new WeakReference<FrameworkElement>(element),
                DocumentId = documentId,
                PropertyName = state.SourceProperty
            });

            state.RegisteredDocumentId = documentId;
        }
    }

    private static void Unregister(FrameworkElement element)
    {
        lock (Gate)
        {
            for (var index = Registrations.Count - 1; index >= 0; index--)
            {
                if (Registrations[index].Element.TryGetTarget(out var target) && !ReferenceEquals(target, element))
                    continue;

                Registrations.RemoveAt(index);
            }
        }
    }

    private static void RequestPlayAfterLayout()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        dispatcher.BeginInvoke(() =>
        {
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
                PlayPendingIndicators();
            }, DispatcherPriority.ApplicationIdle);
        }, DispatcherPriority.Loaded);
    }

    private static void RequestPlay(int retryDelayMs)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(() =>
        {
            if (retryDelayMs > 0)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(retryDelayMs) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    PlayPendingIndicators();
                };
                timer.Start();
                return;
            }

            PlayPendingIndicators();
        }, DispatcherPriority.Loaded);
    }

    private static void PlayPendingIndicators()
    {
        if (UiMotion.IsMotionDisabled)
        {
            lock (Gate)
            {
                PendingUntilMs.Clear();
                PendingDismissKeys.Clear();
                AnimatingKeys.Clear();
            }

            return;
        }

        List<Registration> snapshot;
        List<(Guid Id, string Property)> pendingRevealKeys;
        List<(Guid Id, string Property)> pendingDismissKeys;
        lock (Gate)
        {
            PurgeDeadRegistrations_NoLock();
            snapshot = Registrations.ToList();
            pendingRevealKeys = PendingUntilMs.Keys.ToList();
            pendingDismissKeys = PendingDismissKeys.ToList();
        }

        var now = Environment.TickCount64;
        var needsRetry = false;
        var handledKeys = new HashSet<(Guid Id, string Property)>();

        foreach (var registration in snapshot)
        {
            if (!registration.Element.TryGetTarget(out var element) || !element.IsLoaded)
                continue;

            var key = (registration.DocumentId, registration.PropertyName);

            if (IsDismissPending(key))
            {
                if (TryStartDismiss(element, key))
                    handledKeys.Add(key);
                else
                    needsRetry = true;

                continue;
            }

            if (!IsRevealPending(key, now))
                continue;

            lock (Gate)
            {
                if (AnimatingKeys.Contains(key))
                {
                    handledKeys.Add(key);
                    continue;
                }
            }

            if (element.DataContext is null || !ReadBoolProperty(element.DataContext, registration.PropertyName))
            {
                ApplyInactive(element);
                ClearPending(key);
                handledKeys.Add(key);
                continue;
            }

            if (IsAncestorFadingIn(element))
            {
                if (HasPendingReveal(element.DataContext, registration.PropertyName))
                    ApplyPendingHold(element);
                else
                    ApplyRestingVisible(element);

                needsRetry = true;
                continue;
            }

            StartReveal(element, key);
            handledKeys.Add(key);
        }

        EnsureRestingIndicatorsVisible(snapshot);

        foreach (var key in pendingRevealKeys)
        {
            if (!IsRevealPending(key, now) || handledKeys.Contains(key))
                continue;

            needsRetry = true;
        }

        foreach (var key in pendingDismissKeys)
        {
            if (!IsDismissPending(key) || handledKeys.Contains(key))
                continue;

            needsRetry = true;
        }

        if (needsRetry)
            RequestPlay(RetryDelayMs);
    }

    private static void SyncIndicatorState(FrameworkElement element, State state)
    {
        if (string.IsNullOrWhiteSpace(state.SourceProperty))
            return;

        if (element.DataContext is null)
        {
            ApplyInactive(element);
            return;
        }

        var key = (GetDocumentId(element.DataContext), state.SourceProperty);

        if (!ReadBoolProperty(element.DataContext, state.SourceProperty))
        {
            lock (Gate)
            {
                PendingUntilMs.Remove(key);
                if (AnimatingKeys.Contains(key))
                    return;
            }

            if (TryStartDismiss(element, key))
                return;

            lock (Gate)
            {
                if (PendingDismissKeys.Contains(key))
                    return;
            }

            ApplyInactive(element);
            return;
        }

        lock (Gate)
        {
            PendingDismissKeys.Remove(key);
            if (AnimatingKeys.Contains(key))
            {
                if (element.Visibility == Visibility.Visible && element.Opacity > 0.5)
                    return;

                AnimatingKeys.Remove(key);
            }
        }

        if (HasPendingReveal(element.DataContext, state.SourceProperty))
            ApplyPendingHold(element);
        else
            ApplyRestingVisible(element);
    }

    private static bool TryStartDismiss(FrameworkElement element, (Guid Id, string Property) key)
    {
        if (UiMotion.IsMotionDisabled
            || element.Visibility != Visibility.Visible
            || element.Opacity < 0.5)
        {
            lock (Gate)
                PendingDismissKeys.Remove(key);
            return false;
        }

        if (IsAncestorFadingIn(element))
        {
            lock (Gate)
                PendingDismissKeys.Add(key);
            return false;
        }

        StartDismiss(element, key);
        return true;
    }

    private static void ApplyPendingHold(FrameworkElement element)
    {
        UiMotion.Stop(element);
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = EnterLiftY;
    }

    private static void ApplyRestingVisible(FrameworkElement element)
    {
        UiMotion.Stop(element);
        element.Visibility = Visibility.Visible;
        element.Opacity = 1;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private static void ApplyInactive(FrameworkElement element)
    {
        UiMotion.Stop(element);
        element.Visibility = Visibility.Collapsed;
        element.Opacity = 0;
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private static void StartReveal(FrameworkElement element, (Guid Id, string Property) key)
    {
        lock (Gate)
            AnimatingKeys.Add(key);

        UiMotion.Stop(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.Y = EnterLiftY;

        UiMotion.Run(
            element,
            new[]
            {
                UiMotion.Track(EnterMs, UiMotion.OutSine, 0, 1, value => element.Opacity = value),
                UiMotion.Track(EnterMs, UiMotion.InOutSine, EnterLiftY, 0, value => translate.Y = value)
            },
            onComplete: () =>
            {
                lock (Gate)
                {
                    AnimatingKeys.Remove(key);
                    if (key.Id != Guid.Empty)
                        PendingUntilMs.Remove(key);
                }
            });
    }

    private static void StartDismiss(FrameworkElement element, (Guid Id, string Property) key)
    {
        lock (Gate)
        {
            AnimatingKeys.Add(key);
            PendingDismissKeys.Remove(key);
            PendingUntilMs.Remove(key);
        }

        UiMotion.Stop(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        element.Visibility = Visibility.Visible;
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        var startOpacity = element.Opacity;
        var startLift = translate.Y;

        UiMotion.Run(
            element,
            new[]
            {
                UiMotion.Track(ExitMs, UiMotion.InCubic, startOpacity, 0, value => element.Opacity = value),
                UiMotion.Track(ExitMs, UiMotion.InCubic, startLift, EnterLiftY, value => translate.Y = value)
            },
            onComplete: () =>
            {
                ApplyInactive(element);
                lock (Gate)
                    AnimatingKeys.Remove(key);
            });
    }

    private static bool HasPendingReveal(object? dataContext, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return false;

        var documentId = GetDocumentId(dataContext);
        if (documentId == Guid.Empty)
            return false;

        lock (Gate)
        {
            return PendingUntilMs.TryGetValue((documentId, propertyName), out var untilMs)
                && Environment.TickCount64 <= untilMs;
        }
    }

    private static bool IsRevealPending((Guid Id, string Property) key, long now)
    {
        lock (Gate)
            return PendingUntilMs.TryGetValue(key, out var untilMs) && now <= untilMs;
    }

    private static bool IsDismissPending((Guid Id, string Property) key)
    {
        lock (Gate)
            return PendingDismissKeys.Contains(key);
    }

    private static void ClearPending((Guid Id, string Property) key)
    {
        lock (Gate)
            PendingUntilMs.Remove(key);
    }

    private static bool IsAncestorFadingIn(FrameworkElement element)
    {
        for (var current = element.Parent as FrameworkElement;
             current is not null;
             current = current.Parent as FrameworkElement)
        {
            if (current.Opacity < 0.96)
                return true;
        }

        return false;
    }

    private static Guid GetDocumentId(object? dataContext)
    {
        if (dataContext is null)
            return Guid.Empty;

        var documentProperty = dataContext.GetType().GetProperty("Document");
        if (documentProperty?.GetValue(dataContext) is not { } document)
            return Guid.Empty;

        var idProperty = document.GetType().GetProperty("Id");
        return idProperty?.GetValue(document) is Guid id ? id : Guid.Empty;
    }

    private static bool ReadBoolProperty(object dataContext, string propertyName)
    {
        var property = dataContext.GetType().GetProperty(propertyName);
        return property?.GetValue(dataContext) is true;
    }

    private static void PurgeDeadRegistrations_NoLock()
    {
        for (var index = Registrations.Count - 1; index >= 0; index--)
        {
            if (!Registrations[index].Element.TryGetTarget(out _))
                Registrations.RemoveAt(index);
        }
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State state)
            return state;

        state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }

    private static void SyncElement(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;

        if (element.GetValue(StateProperty) is State state && state.IsAttached)
        {
            RebindElement(element, state);
            return;
        }

        ResetElementVisual(element);
        if (element.IsLoaded)
            Attach(element);
    }

    private static void ResetElementVisual(FrameworkElement element)
    {
        UiMotion.Stop(element);

        if (element.GetValue(StateProperty) is State state)
            ClearTrackedMotionKeys(state);

        ApplyInactive(element);
    }

    private static void ClearTrackedMotionKeys(State state)
    {
        if (state.RegisteredDocumentId == Guid.Empty || string.IsNullOrWhiteSpace(state.SourceProperty))
            return;

        var key = (state.RegisteredDocumentId, state.SourceProperty);
        lock (Gate)
        {
            AnimatingKeys.Remove(key);
            PendingDismissKeys.Remove(key);
            PendingUntilMs.Remove(key);
        }

        state.RegisteredDocumentId = Guid.Empty;
    }

    private static IEnumerable<FrameworkElement> EnumerateIndicatorElements(DependencyObject root)
    {
        if (root is FrameworkElement element && GetIsEnabled(element))
            yield return element;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in EnumerateIndicatorElements(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }
}