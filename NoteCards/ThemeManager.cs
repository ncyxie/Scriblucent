using Microsoft.Win32;
using NoteCards.Services;
using System.Windows;

namespace NoteCards
{
    public static class ThemeManager
    {
        public static event EventHandler? ThemeChanged;

        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";
        public const string SystemTheme = "System";

        private static string _themePreference = LightTheme;
        private static string _currentTheme = LightTheme;
        private static int _nativeThemeRefreshVersion;
        private static bool _systemEventsRegistered;

        public static string CurrentThemePreference => _themePreference;
        public static string CurrentTheme => _currentTheme;

        public static void SetTheme(string theme)
        {
            EnsureSystemEventsRegistered();

            var preference = NormalizeThemePreference(theme);
            var effectiveTheme = ResolveEffectiveTheme(preference);
            var preferenceChanged = !string.Equals(_themePreference, preference, StringComparison.OrdinalIgnoreCase);
            var effectiveThemeChanged = !string.Equals(_currentTheme, effectiveTheme, StringComparison.OrdinalIgnoreCase);

            _themePreference = preference;

            if (!effectiveThemeChanged)
            {
                if (preferenceChanged)
                    ThemeChanged?.Invoke(null, EventArgs.Empty);
                return;
            }

            _currentTheme = effectiveTheme;
            ApplyTheme(effectiveTheme);
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static string NormalizeThemePreference(string? theme)
        {
            if (string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase))
                return DarkTheme;
            if (string.Equals(theme, SystemTheme, StringComparison.OrdinalIgnoreCase))
                return SystemTheme;

            return LightTheme;
        }

        private static void ApplyTheme(string theme)
        {
            var dict = new ResourceDictionary();

            if (string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase))
                dict.Source = new Uri("pack://application:,,,/NoteCards;component/Themes/DarkTheme.xaml", UriKind.Absolute);
            else
                dict.Source = new Uri("pack://application:,,,/NoteCards;component/Themes/LightTheme.xaml", UriKind.Absolute);

            // Find and replace existing theme dictionary, preserving other dictionaries
            var themeDictIndex = -1;
            for (int i = 0; i < Application.Current.Resources.MergedDictionaries.Count; i++)
            {
                var md = Application.Current.Resources.MergedDictionaries[i];
                if (md.Source != null && (md.Source.OriginalString.Contains("DarkTheme") || md.Source.OriginalString.Contains("LightTheme")))
                {
                    themeDictIndex = i;
                    break;
                }
            }

            if (themeDictIndex >= 0)
            {
                Application.Current.Resources.MergedDictionaries[themeDictIndex] = dict;
            }
            else
            {
                Application.Current.Resources.MergedDictionaries.Insert(0, dict);
            }

            var refreshVersion = ++_nativeThemeRefreshVersion;
            ApplyNativeThemeToOpenWindows(_currentTheme, invalidateVisuals: true, rebuildFrame: true);
            RefreshNativeThemeAfterLayout(_currentTheme, refreshVersion);
        }

        private static string ResolveEffectiveTheme(string preference)
        {
            return string.Equals(preference, SystemTheme, StringComparison.OrdinalIgnoreCase)
                ? GetWindowsAppTheme()
                : preference;
        }

        private static string GetWindowsAppTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return value is int lightTheme && lightTheme == 0 ? DarkTheme : LightTheme;
            }
            catch
            {
                return LightTheme;
            }
        }

        private static void EnsureSystemEventsRegistered()
        {
            if (_systemEventsRegistered)
                return;

            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _systemEventsRegistered = true;
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!string.Equals(_themePreference, SystemTheme, StringComparison.OrdinalIgnoreCase))
                return;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (string.Equals(_themePreference, SystemTheme, StringComparison.OrdinalIgnoreCase))
                    SetTheme(SystemTheme);
            });
        }

        private static void ApplyNativeThemeToOpenWindows(string theme, bool invalidateVisuals, bool rebuildFrame)
        {
            foreach (Window window in Application.Current.Windows)
            {
                WindowThemeService.ApplyThemeWhenReady(window, theme, rebuildFrame);
                if (invalidateVisuals)
                    window.InvalidateVisual();
            }
        }

        private static void RefreshNativeThemeAfterLayout(string theme, int refreshVersion)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (refreshVersion == _nativeThemeRefreshVersion)
                {
                    ApplyNativeThemeToOpenWindows(theme, invalidateVisuals: false, rebuildFrame: true);
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}
