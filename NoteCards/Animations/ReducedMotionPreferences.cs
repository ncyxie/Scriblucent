using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NoteCards.Animations;

/// <summary>
/// Resolves whether UI motion should be reduced from the in-app setting and Windows accessibility settings.
/// </summary>
public static class ReducedMotionPreferences
{
    private const int SpiGetAnimation = 0x0048;

    private static bool _appReduceAnimations;
    private static bool? _cached;
    private static readonly object Sync = new();

    public static bool ShouldReduceMotion
    {
        get
        {
            if (_cached.HasValue)
                return _cached.Value;

            return Refresh();
        }
    }

    /// <summary>
    /// Modal overlays keep blur and transitions unless Windows accessibility disables animations.
    /// The in-app "Reduce animations" setting only affects decorative UI motion.
    /// </summary>
    public static bool ShouldUseInstantOverlayTransitions
    {
        get
        {
            lock (Sync)
                return IsSystemReducedMotion();
        }
    }

    public static void Configure(bool appReduceAnimations)
    {
        lock (Sync)
        {
            _appReduceAnimations = appReduceAnimations;
            _cached = null;
        }
    }

    public static bool Refresh()
    {
        lock (Sync)
        {
            _cached = _appReduceAnimations || IsSystemReducedMotion();
            return _cached.Value;
        }
    }

    private static bool IsSystemReducedMotion()
        => !IsMinAnimateEnabled() || IsAnimationEffectsDisabled();

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