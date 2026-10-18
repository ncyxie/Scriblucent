using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>One scroll owner for a drag, independent of insertion/grouping targets and recycled cards.</summary>
internal sealed class ItemDragScroll : IDisposable
{
    private readonly ScrollViewer _viewer;
    private readonly FrameworkElement _viewport;
    private readonly Action<Point> _scrolled;
    private readonly DispatcherTimer _timer;
    private long _lastTick = Stopwatch.GetTimestamp();
    private double _velocity;
    private double _edgeHold;
    private int _edgeDirection;
    private double _wheelRemaining;
    private bool _disposed, _previewQueued;
    private readonly MouseHook _mouseHookHandler;
    private IntPtr _mouseHook;

    internal ItemDragScroll(ScrollViewer viewer, Action<Point> scrolled)
    {
        _viewer = viewer;
        _viewport = FindViewport(viewer) ?? (FrameworkElement)viewer;
        _scrolled = scrolled;
        SmoothScrollBehavior.Interrupt(viewer);
        SmoothScrollBehavior.SetExternalDrive(viewer, true);
        // OLE's modal drag loop consumes wheel messages before WPF can route them.
        // Observe only this UI thread, only for this drag, and only inside its viewport.
        _mouseHookHandler = MouseMessage;
        _mouseHook = SetWindowsHookEx(7 /* WH_MOUSE */, _mouseHookHandler, IntPtr.Zero, GetCurrentThreadId());
        // This is live input, not background preparation. OLE can starve background
        // work beyond the window, particularly while dragging a selected block.
        // One input-priority tick still yields to layout/rendering and queues no backlog.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, Tick, viewer.Dispatcher);
    }

    internal static double TargetVelocity(Point point, Rect viewport, double edgeHold = 0)
    {
        // The owned native drag keeps running outside the window. Vertical overshoot
        // expresses more speed, rather than leaving a narrow scroll target altogether.
        // Outside, allow diagonal pushes as well. Inside, the lateral margin stays
        // modest so a neighbouring column or the sidebar does not drive this scroller.
        if (viewport.IsEmpty || viewport.Height <= 0 || viewport.Width <= 0) return 0;
        double overshoot = Math.Max(0, Math.Max(viewport.Top - point.Y, point.Y - viewport.Bottom));
        double lateralMargin = 48 + overshoot;
        if (point.X < viewport.Left - lateralMargin || point.X > viewport.Right + lateralMargin) return 0;
        double edge = Math.Min(viewport.Height / 2, Math.Clamp(viewport.Height * .2, 48, 96));
        double strength = point.Y < viewport.Top + edge ? -(viewport.Top + edge - point.Y) / edge
            : point.Y > viewport.Bottom - edge ? (point.Y - viewport.Bottom + edge) / edge : 0;
        double pressure = Math.Abs(strength);
        double speed = Math.Clamp(viewport.Height * 2.2, 420, 1200)
            * (pressure <= 1 ? pressure * pressure : 1 + 3 * (1 - Math.Exp(-(pressure - 1) / 2)));
        // Holding at the outer edge still accelerates when the physical screen stops
        // the pointer. Inward movement immediately gives precise placement back.
        double holdWeight = Math.Clamp((pressure - (1 - 12 / edge)) / (12 / edge), 0, 1);
        speed *= 1 + holdWeight * 1.8 * (1 - Math.Exp(-Math.Max(0, edgeHold - .35) / .85));
        return Math.Sign(strength) * Math.Min(speed, Math.Clamp(viewport.Height * 7, 1800, 6000));
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (_disposed || !_viewer.IsLoaded || !_viewer.IsVisible) { Dispose(); return; }
        long now = Stopwatch.GetTimestamp();
        double interval = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        // OLE may deliver late dispatcher ticks outside the window. As with the wheel
        // controller, account for up to 200 ms instead of losing most of that movement.
        double elapsed = Math.Clamp(interval, .001, .2);
        _lastTick = now;
        if (!DashboardDragGhost.TryGetCursorScreenPoint(out var screen)) return;
        var point = _viewer.PointFromScreen(screen);
        var viewport = _viewport.TransformToAncestor(_viewer).TransformBounds(new Rect(_viewport.RenderSize));
        double target = TargetVelocity(point, viewport);
        int direction = target != 0 && (point.Y <= viewport.Top + 12 || point.Y >= viewport.Bottom - 12)
            ? Math.Sign(target) : 0;
        _edgeHold = direction != 0 && direction == _edgeDirection ? Math.Min(10, _edgeHold + interval) : 0;
        _edgeDirection = direction;
        target = TargetVelocity(point, viewport, _edgeHold);
        // Stop at the edge of the active band rather than coasting under a drop target.
        _velocity = target == 0 || !MotionPolicy.Spatial ? target : _velocity + (target - _velocity) * (1 - Math.Exp(-elapsed / .07));
        double wheelStep = MotionPolicy.Spatial ? _wheelRemaining * (1 - Math.Exp(-elapsed / .07)) : _wheelRemaining;
        _wheelRemaining -= wheelStep;
        if (Math.Abs(_wheelRemaining) < .3) { wheelStep += _wheelRemaining; _wheelRemaining = 0; }
        double maxStep = Math.Max(48, viewport.Height);
        double step = Math.Clamp(_velocity * elapsed + wheelStep, -maxStep, maxStep);
        double offset = Math.Clamp(_viewer.VerticalOffset + step, 0, _viewer.ScrollableHeight);
        if (Math.Abs(offset - _viewer.VerticalOffset) < .01) return;
        _viewer.ScrollToVerticalOffset(offset);
        if (_previewQueued) return;
        _previewQueued = true;
        _viewer.Dispatcher.BeginInvoke(() =>
        {
            _previewQueued = false;
            if (!_disposed && DashboardDragGhost.TryGetCursorScreenPoint(out var current)) _scrolled(current);
        }, DispatcherPriority.Loaded);
    }

    private IntPtr MouseMessage(int code, IntPtr message, IntPtr data)
    {
        if (code == 0 && message == (IntPtr)0x020A && !_disposed && _viewer.IsLoaded && _viewer.IsVisible
            && (GetKeyState(0x11 /* VK_CONTROL */) & 0x8000) == 0)
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            var point = _viewer.PointFromScreen(new Point(mouse.X, mouse.Y));
            var bounds = _viewport.TransformToAncestor(_viewer).TransformBounds(new Rect(_viewport.RenderSize));
            bounds.Inflate(0, 24);
            if (bounds.Contains(point))
            {
                int delta = (short)(mouse.MouseData >> 16);
                double lead = Math.Max(240, _viewer.ViewportHeight * .7);
                _wheelRemaining = Math.Clamp(_wheelRemaining - delta / 120d * _viewer.ViewportHeight * .23, -lead, lead);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private static ScrollContentPresenter? FindViewport(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollContentPresenter viewport) return viewport;
            if (child is not ItemsPresenter && FindViewport(child) is { } found) return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop();
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        SmoothScrollBehavior.SetExternalDrive(_viewer, false);
        SmoothScrollBehavior.NotifyOffsetChanged(_viewer, _viewer.HorizontalOffset, _viewer.VerticalOffset);
    }

    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X, Y;
        public IntPtr Window;
        public uint HitTest;
        public UIntPtr ExtraInfo;
        public uint MouseData;
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
