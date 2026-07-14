using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Scriblucent.Animations;

/// <summary>
/// Window content rises on open and sinks on close using live rendering (no bitmap cache).
/// Enter waits for the first content render so the motion surface is not an empty rectangle.
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

        ScheduleEnterWhenRendered(window, state, attempt: 0);

        window.Closing -= OnWindowClosing;
        window.Closing += OnWindowClosing;
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

        if (window.FindName(state.ElementName) is not FrameworkElement root)
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
            if (window.FindName(state.ElementName) is FrameworkElement root)
                UiMotion.EnsureContentRevealRestState(root);
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

        if (window.FindName(state.ElementName) is not FrameworkElement root)
            return;

        root.Opacity = 1;
        UiMotion.EnsureTranslate(root).Y = UiMotion.ResolveWindowEnterLiftY(window);
    }

    private static void TryPlayEnter(Window window, WindowMotionState state, int attempt)
    {
        if (window.FindName(state.ElementName) is not FrameworkElement root)
            return;

        root.UpdateLayout();

        var hasExtent = root.ActualWidth >= 1 && root.ActualHeight >= 1;
        if (!hasExtent && attempt < EnterReadyMaxAttempts)
        {
            ScheduleEnterWhenRendered(window, state, attempt + 1);
            return;
        }

        state.IsEnterAnimating = true;
        ChildWindowOwnerMotion.TryBegin(window);

        var durationMs = UiMotion.ResolveWindowEnterDurationMs(window);
        var offsetY = UiMotion.ResolveWindowEnterLiftY(window);
        UiMotion.AnimateWindowContentEnter(
            root,
            durationMs: durationMs,
            offsetY: offsetY,
            onComplete: () => CompleteEnter(window, state));
    }

    private static void CompleteEnter(Window window, WindowMotionState state)
    {
        state.IsEnterAnimating = false;
        ChildWindowOwnerMotion.TryEnd(window);

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

        if (state.AllowImmediateClose || UiMotion.PrefersReducedMotion || e.Cancel || state.IsExitAnimating)
            return;

        // DialogResult already committed (legacy callers): do not cancel — that clears the result
        // and breaks ShowDialog. Prefer CloseWithDialogResult for animated Save/OK.
        if (window.DialogResult.HasValue)
            return;

        if (window.FindName(state.ElementName) is not FrameworkElement root)
            return;

        e.Cancel = true;
        BeginExitAnimation(window, state, root);
    }

    private static void BeginExitAnimation(Window window, WindowMotionState state, FrameworkElement root)
    {
        state.IsExitAnimating = true;
        ChildWindowOwnerMotion.TryBegin(window);

        UiMotion.AnimateWindowContentExit(
            root,
            onComplete: () =>
            {
                ChildWindowOwnerMotion.TryEnd(window);
                state.BeforeFinalClose?.Invoke();
                state.IsExitAnimating = false;

                // Defer off the render clock — setting DialogResult during CompositionTarget.Rendering is unsafe.
                window.Dispatcher.BeginInvoke(
                    () => FinishExit(window, state),
                    DispatcherPriority.Normal);
            });
    }

    private static void FinishExit(Window window, WindowMotionState state)
    {
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

    private sealed class WindowMotionState
    {
        public string ElementName = "ContentRoot";
        public Action? BeforeFinalClose;
        public bool AllowImmediateClose;
        public bool IsExitAnimating;
        public bool IsEnterAnimating;
        public bool? PendingDialogResult;
        public List<Action> AfterEnterCallbacks = new();
    }
}
