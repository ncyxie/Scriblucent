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
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwUpdateNow = 0x0100;

    /// <summary>DWMWA_BORDER_COLOR is supported starting with Windows 11 build 22000.</summary>
    private static bool SupportsDwmBorderColor
        => Environment.OSVersion.Version.Build >= 22000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    public static void Register(Window window)
    {
        // Same themed caption + DWM animation chrome everywhere. Feature windows use a
        // softer init (no STYLECHANGING fight; deferred ForceFrameChanged) so ShowDialog
        // stays responsive while still getting the built-in open animation.
        if (window is MainWindow)
            ShellWindowChrome.TryAttach(window, ShellChromeKind.NativeAnimated);
        else
            ShellWindowChrome.TryAttach(window, ShellChromeKind.Client);

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

        // SetWindowPos(SWP_FRAMECHANGED) / size thrash breaks DWM maximize animations on Win10
        // (window briefly clones toward a corner). Only soft-invalidate after a live theme switch.
        if (!rebuildFrame)
            return;

        if (window.WindowStyle == WindowStyle.None || window.AllowsTransparency)
            return;

        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame | RdwUpdateNow);
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
        // Main shell caption matches SidebarBackground; feature Client chrome matches FeatureFrameBackground.
        var frameKey = ShellWindowChrome.GetShellFrameBackgroundKey(window);
        var captionFallback = frameKey == "FeatureFrameBackground"
            ? (isDarkMode ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xDE, 0xDE, 0xE8))
            : (isDarkMode ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEC, 0xEC, 0xF1));
        var captionColor = ResolveColor(window, frameKey, captionFallback);
        var borderColor = ResolveColor(
            window,
            "BorderColor",
            isDarkMode ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE0, 0xE0, 0xE6));
        var textColor = ResolveColor(
            window,
            "TextColor",
            isDarkMode ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1C));

        SetDwmColor(handle, DwmaCaptionColor, captionColor);
        SetDwmColor(handle, DwmaTextColor, textColor);
        ShellWindowChrome.ApplyCaptionFrameBorder(window, isDarkMode);
        // Tint OS L/R/B only where DWMWA_BORDER_COLOR is supported (Win11+).
        if (SupportsDwmBorderColor)
            SetDwmColor(handle, DwmaBorderColor, borderColor);
    }

    private static Color ResolveColor(Window window, string resourceKey, Color fallback)
    {
        if (window.TryFindResource(resourceKey) is SolidColorBrush windowBrush)
            return windowBrush.Color;

        if (Application.Current?.TryFindResource(resourceKey) is SolidColorBrush appBrush)
            return appBrush.Color;

        return fallback;
    }

    private static void SetDwmColor(IntPtr handle, int attribute, Color color)
    {
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, Marshal.SizeOf<int>());
    }
}
