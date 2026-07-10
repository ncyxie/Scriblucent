using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;
using System.Windows;

namespace Scriblucent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var logPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "scriblucent-crash.log");
                System.IO.File.AppendAllText(
                    logPath,
                    $"[{DateTime.Now:O}] {args.Exception}\n\n");
            }
            catch
            {
                // Best-effort crash logging only.
            }
        };

        AppSettingsService.MigrateLegacyDataFolder();

        var settings = AppSettingsService.Load();
        ReducedMotionPreferences.Configure(settings.ReduceAnimations);
        ReducedMotionPreferences.Refresh();
        LocalizationService.SetCulture(settings.Language);
        ThemeManager.SetTheme(settings.Theme);
        GlobalScrollBarSettingsService.Apply(settings);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow(settings);
        MainWindow = window;
        WindowThemeService.ApplyThemeWhenReady(window, ThemeManager.CurrentTheme);
        window.Show();
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        WindowThemeService.ApplyThemeWhenReady(window, ThemeManager.CurrentTheme);
        UiMotion.ConfigurePopupsForWindow(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BundledModelHostService.Instance.Stop();
        base.OnExit(e);
    }
}
