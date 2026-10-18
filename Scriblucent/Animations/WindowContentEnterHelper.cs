using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Scriblucent.Animations;

/// <summary>
/// Choreographs the client stage, actual chrome, and actions while preserving close semantics.
/// Enter waits for layout; the stage, actual chrome, and actions use one coordinated rule.
/// </summary>
public static class WindowContentEnterHelper
{
    private const int EnterReadyMaxAttempts = 24;

    private static readonly ConditionalWeakTable<Window, WindowMotionState> States = new();

    public static bool IsEnterAnimating(Window window)
        => States.TryGetValue(window, out var state) && state.IsEnterAnimating;

    public static void RunAfterEnter(Window window, Action action)
    {
        if (action is null)
            return;

        var state = States.GetValue(window, _ => new WindowMotionState());
        if (!state.IsEnterAnimating)
        {
            action();
            return;
        }

        state.AfterEnterCallbacks.Add(action);
    }

    public static void AnimateOnLoaded(
        Window window,
        string elementName = "ContentRoot",
        Action? beforeFinalClose = null)
    {
        var state = States.GetValue(window, _ => new WindowMotionState());
        state.ElementName = elementName;
        state.BeforeFinalClose = beforeFinalClose;

        if (!state.EnterScheduled)
        {
            state.EnterScheduled = true;
            state.IsEnterAnimating = !UiMotion.PrefersReducedMotion;
            ScheduleEnterWhenRendered(window, state, attempt: 0);
        }

        window.Closing -= OnWindowClosing;
        window.Closing += OnWindowClosing;
        window.Closed -= OnWindowClosed;
        window.Closed += OnWindowClosed;
    }

    public static void AllowImmediateClose(Window window)
    {
        if (States.TryGetValue(window, out var state))
            state.AllowImmediateClose = true;
    }

    public static bool ShouldBypassCloseHandling(Window window)
        => States.TryGetValue(window, out var state) && state.AllowImmediateClose;

    /// <summary>
    /// Plays the same exit animation as a plain close, then sets <see cref="Window.DialogResult"/>.
    /// Do not set <see cref="Window.DialogResult"/> before calling this — cancelling that close
    /// clears the result and can make a later set throw.
    /// </summary>
    public static void CloseWithDialogResult(Window window, bool dialogResult)
    {
        var state = States.GetValue(window, _ => new WindowMotionState());
        state.PendingDialogResult = dialogResult;

        if (UiMotion.PrefersReducedMotion)
        {
            ApplyPendingDialogResult(window, state);
            return;
        }

        if (state.IsExitAnimating)
            return;

        if (ResolveMotionRoot(window, state) is not { } root)
        {
            ApplyPendingDialogResult(window, state);
            return;
        }

        BeginExitAnimation(window, state, root);
    }

    private static void ScheduleEnterWhenRendered(Window window, WindowMotionState state, int attempt)
    {
        if (UiMotion.PrefersReducedMotion)
        {
            if (ResolveMotionRoot(window, state) is { } root)
                root.SetCurrentValue(UIElement.OpacityProperty, 1.0);
            return;
        }

        void Queue()
            => window.Dispatcher.BeginInvoke(
                () => TryPlayEnter(window, state, attempt),
                attempt == 0 ? DispatcherPriority.Render : DispatcherPriority.Loaded);

        if (window.IsLoaded)
        {
            PrepareEnterStart(window, state);
            Queue();
            return;
        }

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;
            PrepareEnterStart(window, state);
            Queue();
        }

        window.Loaded += OnLoaded;
    }

    private static void PrepareEnterStart(Window window, WindowMotionState state)
    {
        if (UiMotion.PrefersReducedMotion)
            return;

        if (state.IsClosed || state.IsExitAnimating || ResolveMotionRoot(window, state) is not { } root)
            return;

        WindowMotion.Prepare(root);
    }

    private static void TryPlayEnter(Window window, WindowMotionState state, int attempt)
    {
        if (state.IsClosed || state.IsExitAnimating)
            return;

        if (ResolveMotionRoot(window, state) is not { } root)
        {
            CompleteEnter(window, state);
            return;
        }

        root.UpdateLayout();

        var hasExtent = root.ActualWidth >= 1 && root.ActualHeight >= 1;
        if (!hasExtent && attempt < EnterReadyMaxAttempts)
        {
            ScheduleEnterWhenRendered(window, state, attempt + 1);
            return;
        }

        state.IsEnterAnimating = true;
        UiMotion.AnimateWindowContentEnter(
            root,
            onComplete: () => CompleteEnter(window, state));
    }

    private static void CompleteEnter(Window window, WindowMotionState state)
    {
        state.IsEnterAnimating = false;

        if (state.AfterEnterCallbacks.Count == 0)
            return;

        var callbacks = state.AfterEnterCallbacks.ToArray();
        state.AfterEnterCallbacks.Clear();
        foreach (var callback in callbacks)
            callback();
    }

    private static void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window || !States.TryGetValue(window, out var state))
            return;

        if (state.AllowImmediateClose || UiMotion.PrefersReducedMotion || e.Cancel)
            return;

        if (state.IsExitAnimating)
        {
            e.Cancel = true;
            return;
        }

        // DialogResult already committed (legacy callers): do not cancel — that clears the result
        // and breaks ShowDialog. Prefer CloseWithDialogResult for animated Save/OK.
        if (window.DialogResult.HasValue)
            return;

        if (ResolveMotionRoot(window, state) is not { } root)
            return;

        e.Cancel = true;
        BeginExitAnimation(window, state, root);
    }

    private static void BeginExitAnimation(Window window, WindowMotionState state, FrameworkElement root)
    {
        state.IsExitAnimating = true;
        state.IsEnterAnimating = false;
        state.AfterEnterCallbacks.Clear();

        UiMotion.AnimateWindowContentExit(
            root,
            onComplete: () =>
            {
                state.BeforeFinalClose?.Invoke();

                // Finish after animation callbacks unwind, rather than closing during a timing tick.
                window.Dispatcher.BeginInvoke(
                    () => FinishExit(window, state),
                    DispatcherPriority.Normal);
            });
    }

    private static void FinishExit(Window window, WindowMotionState state)
    {
        if (state.IsClosed)
            return;

        state.IsExitAnimating = false;
        state.AllowImmediateClose = true;

        if (state.PendingDialogResult.HasValue)
        {
            ApplyPendingDialogResult(window, state);
            return;
        }

        window.Close();
    }

    private static void ApplyPendingDialogResult(Window window, WindowMotionState state)
    {
        var result = state.PendingDialogResult;
        state.PendingDialogResult = null;
        state.AllowImmediateClose = true;

        if (result.HasValue)
            window.DialogResult = result;
        else
            window.Close();
    }

    private static FrameworkElement? ResolveMotionRoot(Window window, WindowMotionState state)
        => state.MotionRoot = window.Content is Scriblucent.Controls.FeatureWindowShell shell
            ? shell : window.FindName(state.ElementName) as FrameworkElement ?? window.Content as FrameworkElement;

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !States.TryGetValue(window, out var state))
            return;

        state.IsClosed = true;
        state.IsEnterAnimating = false;
        state.IsExitAnimating = false;
        state.AfterEnterCallbacks.Clear();
        if (state.MotionRoot is { } root)
        {
            WindowMotion.Stop(root);
            SurfaceMotion.Stop(root);
        }
        window.Closing -= OnWindowClosing;
        window.Closed -= OnWindowClosed;
    }

    private sealed class WindowMotionState
    {
        public string ElementName = "ContentRoot";
        public FrameworkElement? MotionRoot;
        public bool EnterScheduled;
        public bool IsClosed;
        public Action? BeforeFinalClose;
        public bool AllowImmediateClose;
        public bool IsExitAnimating;
        public bool IsEnterAnimating;
        public bool? PendingDialogResult;
        public List<Action> AfterEnterCallbacks = new();
    }
}
