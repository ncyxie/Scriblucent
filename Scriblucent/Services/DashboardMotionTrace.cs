using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.ViewModels;

namespace Scriblucent.Services;

/// <summary>Bounded local hitch evidence under the existing diagnostic-logging preference.
/// Observes the motion clock's existing callbacks; adds no rendering hook or idle timer.
/// Callback gaps are UI scheduling evidence, not physical presentation/FPS measurements.</summary>
internal sealed class DashboardMotionTrace
{
    private static readonly ConditionalWeakTable<Window, DashboardMotionTrace> Traces = new();
    private static readonly object FileGate = new();
    private readonly Window _window;
    private readonly FrameworkElement? _sidebar;
    private readonly string _path;
    private readonly Frame[] _frames = new Frame[32];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _count, _next, _writing;
    private long _lastTimestamp;
    private double _lastReportMs = -2000;
    private TimeSpan _renderTime = TimeSpan.MinValue;
    private readonly record struct Frame(double TimeMs, double GapMs, DashboardScrollSurfaceCache.StateSnapshot Cache,
        double SidebarWidth, int Gen0, int Gen1, int Gen2, long UiAllocatedBytes);

    internal static void Attach(Window window)
    {
        if (DiagnosticLogging.IsEnabled) Traces.GetValue(window, owner => new(owner));
    }
    private DashboardMotionTrace(Window window)
    {
        _window = window;
        _sidebar = window.FindName("SidebarChrome") as FrameworkElement;
        _path = Path.Combine(AppSettingsService.SettingsDirectoryOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent"),
            "Logs", "Dashboard", "dashboard-motion.jsonl");
        RenderTransitionClock.Rendered += Rendered;
        RenderTransitionClock.ActivityChanged += ActivityChanged;
        window.Activated += ResetContinuity;
        window.Deactivated += ResetContinuity;
        window.Closed += Closed;
    }
    private void ActivityChanged() { if (!RenderTransitionClock.IsAnyRunning) ResetContinuity(null, EventArgs.Empty); }
    private void ResetContinuity(object? sender, EventArgs args)
    { _lastTimestamp = 0; _renderTime = TimeSpan.MinValue; _count = _next = 0; }
    private void Rendered(object? sender, EventArgs args)
    {
        if (!_window.Dispatcher.CheckAccess()) return;
        if (!_window.IsActive || _window.WindowState == WindowState.Minimized)
        { ResetContinuity(null, EventArgs.Empty); return; }
        if (args is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _renderTime) return;
            _renderTime = rendering.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var gap = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
        _lastTimestamp = now;
        _frames[_next] = new(_clock.Elapsed.TotalMilliseconds, gap, DashboardScrollSurfaceCache.Snapshot,
            _sidebar is null ? 0 : SidebarResizeMotion.VisibleWidth(_sidebar, 0),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetAllocatedBytesForCurrentThread());
        _next = (_next + 1) % _frames.Length; _count = Math.Min(_frames.Length, _count + 1);
        if (gap < 50 || _clock.Elapsed.TotalMilliseconds - _lastReportMs < 2000 || !DiagnosticLogging.IsEnabled) return;
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;
        _lastReportMs = _clock.Elapsed.TotalMilliseconds;
        var frames = Enumerable.Range(0, _count).Select(i => _frames[(_next - _count + i + _frames.Length) % _frames.Length]).ToArray();
        var report = new
        {
            Utc = DateTimeOffset.UtcNow, Process = Environment.ProcessId,
            Kind = "UI motion callback gap; not presented FPS",
            Dashboard = (_window.DataContext as MainViewModel)?.ActiveDashboard,
            Width = _window.ActualWidth, Height = _window.ActualHeight,
            Dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX,
            Quality = MotionPolicy.Quality.ToString(), RenderTier = RenderCapability.Tier >> 16,
            Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2), Frames = frames
        };
        _ = Task.Run(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(report) + Environment.NewLine;
                lock (FileGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (File.Exists(_path) && new FileInfo(_path).Length > 256 * 1024) File.WriteAllText(_path, "");
                    File.AppendAllText(_path, json);
                }
            }
            catch { /* Diagnostic I/O must not affect the application. */ }
            finally { Interlocked.Exchange(ref _writing, 0); }
        });
    }
    private void Closed(object? sender, EventArgs args)
    {
        RenderTransitionClock.Rendered -= Rendered;
        RenderTransitionClock.ActivityChanged -= ActivityChanged;
        _window.Activated -= ResetContinuity; _window.Deactivated -= ResetContinuity;
        _window.Closed -= Closed;
        Traces.Remove(_window);
    }
}
