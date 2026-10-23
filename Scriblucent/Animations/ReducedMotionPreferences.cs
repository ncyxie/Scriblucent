using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Scriblucent.Animations;

/// <summary>
/// Applies the selected motion set while respecting Windows animation preferences.
/// </summary>
public static class ReducedMotionPreferences
{
    private const int SpiGetAnimation = 0x0048;

    private static bool _initialized;
    private static readonly object Sync = new();

    public static bool ShouldDisableMotion
    {
        get
        {
            if (!_initialized) Refresh();
            return !MotionPolicy.Enabled;
        }
    }

    /// <summary>
    /// Every surface respects the same application and Windows preference.
    /// </summary>
    public static bool ShouldUseInstantOverlayTransitions
    {
        get
        {
            return MotionPolicy.Quality == MotionQuality.Off;
        }
    }

    public static void Configure(MotionPreference preference)
    {
        lock (Sync)
        {
            MotionPolicy.Configure(preference, IsSystemReducedMotion());
            _initialized = true;
        }
    }

    public static bool Refresh()
    {
        lock (Sync)
        {
            MotionPolicy.Configure(MotionPolicy.Preference, IsSystemReducedMotion());
            _initialized = true;
            return !MotionPolicy.Enabled;
        }
    }

    private static bool IsSystemReducedMotion()
        => !System.Windows.SystemParameters.ClientAreaAnimation || !IsMinAnimateEnabled() || IsAnimationEffectsDisabled();

    private static bool IsMinAnimateEnabled()
    {
        var info = new AnimationInfo
        {
            CbSize = (uint)Marshal.SizeOf<AnimationInfo>()
        };

        if (!SystemParametersInfo(SpiGetAnimation, Marshal.SizeOf<AnimationInfo>(), ref info, 0))
            return true;

        return info.IMinAnimate != 0;
    }

    private static bool IsAnimationEffectsDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Accessibility",
                writable: false);

            if (key?.GetValue("AnimationEffects") is not int value)
                return false;

            return value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        int uiAction,
        int uiParam,
        ref AnimationInfo pvParam,
        int fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint CbSize;
        public int IMinAnimate;
    }
}
