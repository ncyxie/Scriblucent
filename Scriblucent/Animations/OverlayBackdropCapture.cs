using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Rendering;

namespace Scriblucent.Animations;

/// <summary>Capture is UI-only; Gaussian processing is worker-only. Leases share owner snapshots.</summary>
public static class OverlayBackdropCapture
{
    public readonly record struct Measurement(double CaptureMs, double BlurMs, double PublicationMs,
        int Pixels, bool Applied, string? Failure, int CaptureThread, int BlurThread)
    {
        public double TraversalMs { get; init; }
        public double RasterMs { get; init; }
        public double FreezeMs { get; init; }
        public double RestoreMs { get; init; }
        public double RequestToPreparedMs { get; init; }
        public double PreparedToPublicationMs { get; init; }
    }
    private readonly record struct CapturePhases(double Traversal, double Raster, double Freeze, double Restore);
    public static event Action<Measurement>? Measured;
    private sealed class Snapshot
    {
        internal BitmapSource? Image;
        internal Size Size;
        internal DpiScale Dpi;
        internal object? Theme;
        internal int Users, Version;
        internal double Reserved;
        internal CancellationTokenSource? Cancellation;
        internal Task<BitmapSource?>? Pending;
        internal TaskCompletionSource? Prepared;
        internal Measurement Metrics;
    }
    private static readonly ConditionalWeakTable<Window, Snapshot> Snapshots = new();
    private static readonly SemaphoreSlim BlurGate = new(1, 1);
    internal static bool IsProcessing => BlurGate.CurrentCount == 0
        || Snapshots.Any(entry => entry.Value.Pending is { IsCompleted: false });
    internal static double RetainedPixels => Snapshots.Sum(entry => entry.Value.Reserved);
    internal static void Invalidate(Window window)
    {
        if (!Snapshots.TryGetValue(window, out var state)) return;
        state.Version++;
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose(); state.Cancellation = null;
        state.Prepared?.TrySetCanceled(); state.Prepared = null;
        state.Pending = null; state.Image = null;
        MotionCacheBudget.Release(state.Reserved); state.Reserved = 0;
    }

    public static IDisposable Request(Window? owner, Action<BitmapSource?> completed)
        => new RequestLease(owner, completed);

    // Capture readiness is separate from worker readiness: no foreground clock starts inside UI raster work.
    internal static IDisposable RequestPrepared(Window? owner, Action<BitmapSource?> completed, Action prepared,
        Func<Task>? beforeRefresh = null, bool deferInitial = false)
        => new RequestLease(owner, completed, prepared, beforeRefresh, deferInitial);

    private sealed class RequestLease : IDisposable
    {
        private readonly Window? _owner;
        private readonly Action<BitmapSource?> _completed;
        private Action? _prepared;
        private readonly Func<Task>? _beforeRefresh;
        private readonly bool _deferInitial;
        private readonly CancellationTokenSource _cancel = new();
        private int _revision;
        private bool _disposed;
        internal RequestLease(Window? owner, Action<BitmapSource?> completed, Action? prepared = null,
            Func<Task>? beforeRefresh = null, bool deferInitial = false)
        {
            _owner = owner; _completed = completed; _prepared = prepared; _beforeRefresh = beforeRefresh;
            _deferInitial = deferInitial;
            if (owner is null) { prepared?.Invoke(); completed(null); return; }
            owner.Dispatcher.VerifyAccess();
            Snapshots.GetValue(owner, _ => new()).Users++;
            owner.Closed += Closed; owner.SizeChanged += SizeChanged; owner.DpiChanged += DpiChanged;
            MotionPolicy.BackdropChanged += MaterialChanged;
            ThemeManager.ThemeChanged += ThemeChanged;
            Restart();
        }
        private void Closed(object? sender, EventArgs e) => Dispose();
        private void SizeChanged(object sender, SizeChangedEventArgs e) => Restart();
        private void DpiChanged(object sender, DpiChangedEventArgs e) => Restart();
        private void MaterialChanged() => Restart();
        private void ThemeChanged(object? sender, EventArgs e) => Restart();
        private async void Restart()
        {
            if (_disposed || _owner is null || _owner.Dispatcher.HasShutdownStarted) return;
            if (!_owner.Dispatcher.CheckAccess()) { _ = _owner.Dispatcher.BeginInvoke(Restart); return; }
            var revision = ++_revision;
            var requestTimer = Stopwatch.StartNew(); double preparedAt = 0;
            try
            {
                // Before preparation, this request still owns starting the entrance. Waiting
                // for that entrance after resize/theme/DPI restart would deadlock both sides.
                if ((_deferInitial || (revision > 1 && _prepared is null)) && _beforeRefresh is not null)
                    await _beforeRefresh().WaitAsync(_cancel.Token);
                if (_disposed || revision != _revision) return;
                var imageTask = GetAsync(_owner);
                var preparation = Snapshots.TryGetValue(_owner, out var pending) ? pending.Prepared?.Task : null;
                if (preparation is not null) await preparation.WaitAsync(_cancel.Token);
                await _owner.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || revision != _revision) return;
                    preparedAt = requestTimer.Elapsed.TotalMilliseconds;
                    var prepared = _prepared; _prepared = null; prepared?.Invoke();
                }, DispatcherPriority.Loaded, _cancel.Token);
                var image = await imageTask.WaitAsync(_cancel.Token);
                await _owner.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || revision != _revision) return;
                    var timer = Stopwatch.StartNew();
                    _completed(image); timer.Stop();
                    if (Snapshots.TryGetValue(_owner, out var state))
                        Measured?.Invoke(state.Metrics with
                        {
                            PublicationMs = timer.Elapsed.TotalMilliseconds,
                            RequestToPreparedMs = preparedAt,
                            PreparedToPublicationMs = requestTimer.Elapsed.TotalMilliseconds - preparedAt
                        });
                }, DispatcherPriority.Background, _cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!_disposed && revision == _revision && !_owner.Dispatcher.HasShutdownStarted)
                    await _owner.Dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed || revision != _revision) return;
                        var prepared = _prepared; _prepared = null; prepared?.Invoke();
                        _completed(null);
                    }, DispatcherPriority.Background);
                Debug.WriteLine($"Backdrop publication: {error.Message}");
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _cancel.Cancel(); _cancel.Dispose();
            if (_owner is null) return;
            _owner.Closed -= Closed; _owner.SizeChanged -= SizeChanged; _owner.DpiChanged -= DpiChanged;
            MotionPolicy.BackdropChanged -= MaterialChanged;
            ThemeManager.ThemeChanged -= ThemeChanged;
            if (Snapshots.TryGetValue(_owner, out var state) && --state.Users == 0) Invalidate(_owner);
        }
    }

    private static Task<BitmapSource?> GetAsync(Window owner)
    {
        var state = Snapshots.GetValue(owner, _ => new());
        if (!MotionPolicy.BackdropBlur || owner.Content is not FrameworkElement root)
        {
            Invalidate(owner);
            state.Metrics = new(0, 0, 0, 0, false, "Material preference", Environment.CurrentManagedThreadId, 0);
            return Task.FromResult<BitmapSource?>(null);
        }
        var size = root.RenderSize; var dpi = VisualTreeHelper.GetDpi(root);
        var theme = root.TryFindResource("WindowBackground");
        if (state.Size != size || !state.Dpi.Equals(dpi) || !ReferenceEquals(state.Theme, theme)) Invalidate(owner);
        state.Size = size; state.Dpi = dpi; state.Theme = theme;
        if (state.Image is not null) return Task.FromResult<BitmapSource?>(state.Image);
        if (state.Pending is not null) return state.Pending;
        state.Cancellation = new();
        state.Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Pending = CaptureAndBlurAsync(owner, root, state, state.Version, state.Prepared, state.Cancellation.Token);
        return state.Pending;
    }

    private static async Task<BitmapSource?> CaptureAndBlurAsync(Window owner, FrameworkElement root,
        Snapshot state, int version, TaskCompletionSource prepared, CancellationToken token)
    {
        double reserved = 0;
        var ownsWorkerGate = false;
        try
        {
            var captureTimer = new Stopwatch(); var captureThread = 0;
            CapturePhases phases = default;
            // Render the immediate scrim feedback before preparing the foreground entrance.
            var captured = await owner.Dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested(); captureThread = Environment.CurrentManagedThreadId;
                captureTimer.Start();
                var result = OverlayBackgroundSuspender.WithPageVisible(owner, () => CapturePage(root, out phases));
                captureTimer.Stop();
                if (result is not null)
                    reserved = MotionCacheBudget.TryReservePixels(result.PixelWidth * (double)result.PixelHeight, requireSpatial: false);
                return result;
            }, DispatcherPriority.Background, token);
            token.ThrowIfCancellationRequested();
            prepared.TrySetResult();
            if (captured is null || reserved == 0)
            {
                state.Metrics = new(captureTimer.Elapsed.TotalMilliseconds, 0, 0, 0, false,
                    captured is null ? "Capture unavailable" : "Texture budget exhausted", captureThread, 0)
                    { TraversalMs = phases.Traversal, RasterMs = phases.Raster, FreezeMs = phases.Freeze, RestoreMs = phases.Restore };
                return null;
            }
            // Only Gaussian processing is serialized. Another owner cannot hold up this owner's arrival.
            await BlurGate.WaitAsync(token); ownsWorkerGate = true;
            var blurThread = 0; var blurTimer = new Stopwatch();
            var captureDpi = new DpiScale(captured.DpiX / 96, captured.DpiY / 96);
            var image = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); blurThread = Environment.CurrentManagedThreadId;
                blurTimer.Start(); var result = SkiaBackdropBlur.Blur(captured, 18, captureDpi); blurTimer.Stop();
                return result;
            }, token);
            token.ThrowIfCancellationRequested();
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                if (version != state.Version || state.Size != root.RenderSize
                    || !state.Dpi.Equals(VisualTreeHelper.GetDpi(root))
                    || !ReferenceEquals(state.Theme, root.TryFindResource("WindowBackground"))) return null;
                state.Metrics = new(captureTimer.Elapsed.TotalMilliseconds, blurTimer.Elapsed.TotalMilliseconds, 0,
                    captured.PixelWidth * captured.PixelHeight, image is not null,
                    image is null ? "Gaussian blur failed" : null, captureThread, blurThread)
                    { TraversalMs = phases.Traversal, RasterMs = phases.Raster, FreezeMs = phases.Freeze, RestoreMs = phases.Restore };
                state.Image = image;
                if (image is not null) { state.Reserved = reserved; reserved = 0; }
                return image;
            }, DispatcherPriority.Background, token);
        }
        finally
        {
            prepared.TrySetResult();
            MotionCacheBudget.Release(reserved);
            if (ownsWorkerGate) BlurGate.Release();
        }
    }

    private static BitmapSource? CapturePage(FrameworkElement root, out CapturePhases phases)
    {
        phases = default;
        double traversal = 0, raster = 0, freeze = 0;
        var phase = Stopwatch.StartNew();
        var hidden = new List<(UIElement Element, Visibility Visibility)>();
        var effects = new List<(UIElement Element, Effect Effect)>();
        try
        {
            void Suspend(DependencyObject current)
            {
                if (current is UIElement element)
                {
                    if (element.Visibility != Visibility.Visible) return;
                    // Chrome can wrap the original content. Exclude overlays at every nested
                    // panel level, otherwise an About/dialog snapshot captures its own surface.
                    if (!ReferenceEquals(element, root) && Panel.GetZIndex(element) >= 200
                        && VisualTreeHelper.GetParent(element) is Panel)
                    {
                        hidden.Add((element, element.Visibility));
                        element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                        return;
                    }
                    if (element.Effect is { } effect) { effects.Add((element, effect)); element.SetCurrentValue(UIElement.EffectProperty, null); }
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) Suspend(VisualTreeHelper.GetChild(current, i));
            }
            Suspend(root);
            traversal = phase.Elapsed.TotalMilliseconds;
            // This runs after layout at Background priority. Do not force another whole-window
            // layout: newly opened hidden overlays would still measure their entire contents.
            var size = root.RenderSize;
            if (size.Width < 1 || size.Height < 1) return null;
            var dpi = VisualTreeHelper.GetDpi(root);
            var width = size.Width * dpi.DpiScaleX; var height = size.Height * dpi.DpiScaleY;
            // The blurred material needs low frequencies, not a half-megapixel sharp image.
            // Keep the same 18 DIP Gaussian while reducing the one-time UI raster cost.
            var pixels = MotionPolicy.LightweightRendering ? 200_000d : 350_000d;
            var reduction = Math.Min(1, Math.Min(1600 / Math.Max(width, height), Math.Sqrt(pixels / (width * height))));
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Floor(width * reduction)),
                Math.Max(1, (int)Math.Floor(height * reduction)), dpi.PixelsPerInchX * reduction,
                dpi.PixelsPerInchY * reduction, PixelFormats.Pbgra32);
            phase.Restart(); bitmap.Render(root); raster = phase.Elapsed.TotalMilliseconds;
            phase.Restart(); bitmap.Freeze(); freeze = phase.Elapsed.TotalMilliseconds;
            return bitmap;
        }
        catch (Exception error) { Debug.WriteLine($"Backdrop capture: {error.Message}"); return null; }
        finally
        {
            phase.Restart();
            foreach (var (element, effect) in effects) element.SetCurrentValue(UIElement.EffectProperty, effect);
            foreach (var (element, visibility) in hidden) element.SetCurrentValue(UIElement.VisibilityProperty, visibility);
            phases = new(traversal, raster, freeze, phase.Elapsed.TotalMilliseconds);
        }
    }

    // Compatibility cache lookups never perform synchronous capture or processing.
    public static BitmapSource? TryCaptureForDimAnimation(Window? window) => Cached(window);
    public static BitmapSource? TryCaptureForDimAnimation(Window? window, double blurRadius) => Cached(window);
    public static BitmapSource? TryCaptureBehindOverlays(Window? window) => Cached(window);
    public static BitmapSource? TryCaptureBehindOverlays(Window? window, double blurRadius) => Cached(window);
    public static BitmapSource? TryCaptureForDialog(Window? window) => Cached(window);
    private static BitmapSource? Cached(Window? window)
    {
        if (!MotionPolicy.BackdropBlur || window?.Content is not FrameworkElement root
            || !Snapshots.TryGetValue(window, out var state)) return null;
        return state.Size == root.RenderSize && state.Dpi.Equals(VisualTreeHelper.GetDpi(root))
            && ReferenceEquals(state.Theme, root.TryFindResource("WindowBackground")) ? state.Image : null;
    }
    public static double GetBlurRadius(Window? window) => 18;
    public static (int Width, int Height) GetScrimLogicalSize(double width, double height)
        => (Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)));
}
