using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace Scriblucent.Animations;

public enum MotionPreference { Full = 0, Reduced = 1, Off = 2 }
public enum MotionQuality { Expressive, Lightweight, Reduced, Off }
public enum MotionChannel { Presence, Interaction, Navigation, Layout, Showcase, Selection, CommandLight, CommandPress, InteractionPaint }

/// <summary>One vocabulary for motion; spatial feedback is never required to use the app.</summary>
internal static class MotionTokens
{
    internal const int InteractionIn = 160, InteractionOut = 180;
    internal const int Presence = 280, Exit = 120, Navigation = 180;
    internal const int Layout = 220, Popup = 160, Modal = 300;
    internal const int Stagger = 24, MaxStagger = 96, MaxItems = 8;
    internal const double Rise = 14, CardScale = .94;
}

public static class MotionPolicy
{
    private static MotionPreference _preference;
    private static bool _systemOff;
    private static WeakReference<Window>? _activeWindow;
    private static MotionQuality _notifiedQuality;
    private static bool _notifiedLightweight;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> ObservedWindows = new();
    public static event Action? Changed;
    public static MotionPreference Preference => _preference;
    public static MotionQuality Quality => _systemOff || _preference == MotionPreference.Off
        ? MotionQuality.Off : _preference == MotionPreference.Reduced ? MotionQuality.Reduced
        : LightweightRendering ? MotionQuality.Lightweight : MotionQuality.Expressive;
    public static bool Enabled => Quality != MotionQuality.Off;
    public static bool Spatial => Enabled;
    // Reduced keeps the same core motion, paint and timing. Only secondary choreography
    // uses this budget; hardware limits remain independent of the selected motion set.
    internal static bool ExpressiveExtras => Enabled && _preference == MotionPreference.Full;
    public static bool Celebration => ExpressiveExtras;
    internal static int ParticleBudget => !Celebration ? 0 : LightweightRendering ? 16 : 36;
    internal static bool DecorativeMotion => ExpressiveExtras && !LightweightRendering;
    internal static int PresenceItemBudget => ExpressiveExtras ? MotionTokens.MaxItems : 4;
    internal static int StaggerDelay(int requested) => ExpressiveExtras ? requested : 0;
    internal static event Action? DecorationBudgetChanged;
    public static bool BackdropBlur => !SystemParameters.HighContrast && _transparencyEnabled;
    internal static bool LightweightRendering => (RenderCapability.Tier >> 16) < 2 || SystemParameters.IsRemoteSession
        || RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly;
    internal static event Action? BackdropChanged;
    private static bool _transparencyEnabled = ReadTransparencyPreference();
    private static bool ReadTransparencyPreference()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch { return true; }
    }
    public static int FadeDuration(int requested) => Enabled ? requested : 0;

    static MotionPolicy()
    {
        SystemParameters.StaticPropertyChanged += SystemChanged;
        RenderCapability.TierChanged += (_, _) => NotifyQualityChanged();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ReducedMotionPreferences.Refresh();
                _transparencyEnabled = ReadTransparencyPreference();
                BackdropChanged?.Invoke();
            });
        };
    }
    private static void SystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) BackdropChanged?.Invoke();
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.MinimizeAnimation))
            ReducedMotionPreferences.Refresh();
    }
    internal static void Configure(MotionPreference preference, bool systemOff)
    {
        _preference = preference;
        _systemOff = systemOff;
        NotifyQualityChanged();
    }
    private static void NotifyQualityChanged()
    {
        if (_notifiedQuality == Quality && _notifiedLightweight == LightweightRendering) return;
        var previous = _notifiedQuality;
        _notifiedQuality = Quality;
        _notifiedLightweight = LightweightRendering;
        if (previous == Quality || (previous is MotionQuality.Expressive or MotionQuality.Lightweight
            && Quality is MotionQuality.Expressive or MotionQuality.Lightweight))
            DecorationBudgetChanged?.Invoke();
        else Changed?.Invoke();
    }
    // Callback timing is diagnostic, not a measurement of presented frames.
    internal static void Sample(TimeSpan frameTime) { }
    internal static void ResetSampling(bool discardWindow = false) { }

    internal static void ObserveActiveWindow()
    {
        if (_activeWindow is not null && _activeWindow.TryGetTarget(out var current) && current.IsActive) return;
        if (Application.Current is not { } app) return;
        foreach (Window window in app.Windows)
        {
            if (!window.IsActive) continue;
            _activeWindow = new(window);
            if (!ObservedWindows.TryGetValue(window, out _))
            {
                ObservedWindows.Add(window, new object());
                window.Activated += (_, _) => { _activeWindow = new(window); ResetSampling(); };
                window.Deactivated += (_, _) => ResetSampling(true);
                if (PresentationSource.FromVisual(window) is HwndSource source)
                    source.AddHook(ResizeHook);
            }
            break;
        }
    }
    private static IntPtr ResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0231) { ResetSampling(true); }
        else if (msg == 0x0232) { ResetSampling(true); }
        return IntPtr.Zero;
    }
}
