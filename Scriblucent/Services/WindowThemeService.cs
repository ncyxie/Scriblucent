using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scriblucent.Services;

internal static class WindowThemeService
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private const int DwmaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmaBorderColor = 34;
    private const int DwmaCaptionColor = 35;
    private const int DwmaTextColor = 36;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwUpdateNow = 0x0100;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    public static void Register(Window window)
    {
        ApplyThemeWhenReady(window, ThemeManager.CurrentTheme);
    }

    public static void ApplyThemeWhenReady(Window window, string theme, bool rebuildFrame = false)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyTheme(window, theme, rebuildFrame);
            return;
        }

        window.SourceInitialized -= OnWindowSourceInitialized;
        window.SourceInitialized += OnWindowSourceInitialized;
    }

    public static void ApplyTheme(Window window, string theme, bool rebuildFrame = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var useDarkMode = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var attributeSize = Marshal.SizeOf<int>();
        var isDarkMode = useDarkMode == 1;

        DwmSetWindowAttribute(handle, DwmaUseImmersiveDarkMode, ref useDarkMode, attributeSize);
        DwmSetWindowAttribute(handle, DwmaUseImmersiveDarkModeBefore20H1, ref useDarkMode, attributeSize);
        SetWindowTheme(handle, isDarkMode ? "DarkMode_Explorer" : "Explorer", null);

        ApplyCaptionColors(handle, window, isDarkMode);
        RefreshNonClientFrame(handle, window, rebuildFrame);
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.SourceInitialized -= OnWindowSourceInitialized;
        ApplyTheme(window, ThemeManager.CurrentTheme);
    }

    private static void ApplyCaptionColors(IntPtr handle, Window window, bool isDarkMode)
    {
        var captionColor = ResolveColor(window, isDarkMode ? "CardBackground" : "WindowBackground", isDarkMode ? Color.FromRgb(38, 38, 38) : Color.FromRgb(235, 235, 239));
        var borderColor = ResolveColor(window, "BorderColor", isDarkMode ? Color.FromRgb(58, 58, 58) : Color.FromRgb(217, 217, 223));
        var textColor = ResolveColor(window, "TextColor", isDarkMode ? Colors.White : Color.FromRgb(28, 28, 28));

        SetDwmColor(handle, DwmaCaptionColor, captionColor);
        SetDwmColor(handle, DwmaBorderColor, borderColor);
        SetDwmColor(handle, DwmaTextColor, textColor);
    }

    private static Color ResolveColor(Window window, string resourceKey, Color fallback)
    {
        return window.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : fallback;
    }

    private static void SetDwmColor(IntPtr handle, int attribute, Color color)
    {
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, Marshal.SizeOf<int>());
    }

    private static void RefreshNonClientFrame(IntPtr handle, Window window, bool rebuildFrame)
    {
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        if (rebuildFrame && window.WindowState == WindowState.Normal && GetWindowRect(handle, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            SetWindowPos(handle, IntPtr.Zero, rect.Left, rect.Top, width + 1, height, SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            SetWindowPos(handle, IntPtr.Zero, rect.Left, rect.Top, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame | RdwUpdateNow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
