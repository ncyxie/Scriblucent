using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

/// <summary>Samples actual desktop pixels from the isolated window on a worker thread.
/// This observes visible changes; it is not an ETW presentation timestamp or a production frame profiler.</summary>
internal sealed class OnscreenMotionProbe : IDisposable
{
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Stopwatch _time = Stopwatch.StartNew();
    private volatile bool _stop;
    private readonly List<(double Time, uint Hash)> _samples = new();
    private Exception? _error;
    private readonly int _x, _y, _width, _height;
    internal OnscreenMotionProbe(FrameworkElement region)
    {
        var origin = region.PointToScreen(new Point());
        var end = region.PointToScreen(new Point(region.ActualWidth, region.ActualHeight));
        _x = (int)Math.Floor(origin.X) - 24; _y = (int)Math.Floor(origin.Y) - 24;
        _width = Math.Clamp((int)Math.Ceiling(end.X - origin.X) + 48, 32, 700);
        _height = Math.Clamp((int)Math.Ceiling(end.Y - origin.Y) + 48, 32, 320);
        _worker = new Thread(Sample) { IsBackground = true, Name = "Isolated on-screen motion probe" };
        _worker.Start(); _ready.Wait(TimeSpan.FromSeconds(3));
    }
    private void Sample()
    {
        var screen = GetDC(IntPtr.Zero); var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = _width, Height = -_height, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var pixels = new byte[_width * _height * 4];
            while (!_stop)
            {
                if (!BitBlt(memory, 0, 0, _width, _height, screen, _x, _y, 0x00CC0020))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                uint hash = 2166136261;
                for (var i = 0; i < pixels.Length; i += 16) hash = unchecked((hash ^ BitConverter.ToUInt32(pixels, i)) * 16777619);
                _samples.Add((_time.Elapsed.TotalMilliseconds, hash)); _ready.Set();
                Thread.Sleep(8);
            }
        }
        catch (Exception ex) { _error = ex; _ready.Set(); }
        finally
        {
            SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen);
        }
    }
    internal bool Available => _error is null && _samples.Count > 1;
    internal object Finish(string name)
    {
        _stop = true; _worker.Join(TimeSpan.FromSeconds(3));
        var changes = new List<double>();
        for (var i = 1; i < _samples.Count; i++)
            if (_samples[i].Hash != _samples[i - 1].Hash) changes.Add(_samples[i].Time);
        var gaps = changes.Zip(changes.Skip(1), (a, b) => b - a).Order().ToArray();
        return new { name, available = Available, kind = "Desktop pixel changes, not ETW presentation timestamps", samples = _samples.Count,
            visibleChanges = changes.Count, distinctImages = _samples.Select(s => s.Hash).Distinct().Count(),
            changeGapP95Ms = gaps.Length == 0 ? 0 : gaps[Math.Min(gaps.Length - 1, (int)Math.Ceiling(gaps.Length * .95) - 1)],
            changeGapMaxMs = gaps.LastOrDefault(), captureWidth = _width, captureHeight = _height, error = _error?.ToString() };
    }
    public void Dispose() { _stop = true; _worker.Join(TimeSpan.FromSeconds(3)); _ready.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, ImageSize; internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr destination, int x, int y,
        int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
