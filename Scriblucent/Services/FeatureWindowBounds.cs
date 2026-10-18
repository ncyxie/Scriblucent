using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scriblucent.Services;

/// <summary>Fits native secondary windows to their actual monitor, including mixed-DPI owners.</summary>
internal static class FeatureWindowBounds
{
    internal static Rect GetWorkArea(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource { CompositionTarget: { } target } source)
            return SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(source.Handle, 2), ref info)) return SystemParameters.WorkArea;
        return new Rect(target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
            target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    internal static void Attach(Window window)
    {
        window.Loaded += ClampOnce;
    }
    private static void ClampOnce(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;
        window.Loaded -= ClampOnce;
        if (window.WindowState != WindowState.Normal) return;
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null) return;
        var ownerHandle = window.Owner is null ? source.Handle : new WindowInteropHelper(window.Owner).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(ownerHandle, 2), ref info)) return;
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        var bounds = new Rect(topLeft, bottomRight);
        var availableWidth = Math.Max(1, bounds.Width - 32);
        var availableHeight = Math.Max(1, bounds.Height - 32);
        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.Width = Math.Min(window.ActualWidth, availableWidth);
        window.Height = Math.Min(window.ActualHeight, availableHeight);
        window.Left = Math.Clamp(window.Left, bounds.Left + 16, bounds.Right - 16 - window.Width);
        window.Top = Math.Clamp(window.Top, bounds.Top + 16, bounds.Bottom - 16 - window.Height);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
