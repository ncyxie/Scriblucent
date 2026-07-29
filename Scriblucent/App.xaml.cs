using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;
using System.Windows;

namespace Scriblucent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Multi-Core JIT: records which methods get JIT'd this run, then on every later run
        // background-JITs those same methods on idle cores while startup proceeds — so by the
        // time the user starts scrolling, the hot paths are more likely already past the slow
        // first-call "quick JIT" tier instead of paying that cost live in response to input.
        // Best-effort only: a failure here (e.g. no write access) must never block startup.
        try
        {
            var profileDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent");
            System.IO.Directory.CreateDirectory(profileDir);
            System.Runtime.ProfileOptimization.SetProfileRoot(profileDir);
            System.Runtime.ProfileOptimization.StartProfile("startup.profile");
        }
        catch
        {
            // Best-effort JIT warm-start only.
        }

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
