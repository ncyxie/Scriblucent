using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using System.Windows;

namespace Scriblucent;

public partial class App : Application
{
    private AppInstanceCoordinator? _instance;
    private bool _settingsInitialized;
    private readonly LaunchImportQueue _launchImports = new();
    private static readonly string[] ElevationTokenKeys =
    {
        "Elevation.Thumb", "Elevation.Popup"
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.SetCulture(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        string[] paths;
        try { paths = LaunchImportQueue.ParseArguments(e.Args); }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or NotSupportedException)
        {
            MessageBox.Show(LocalizationService.GetString("LaunchArgumentsInvalid"), "Scriblucent");
            Shutdown(1);
            return;
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _instance = new AppInstanceCoordinator(System.IO.Path.Combine(localAppData, "Scriblucent"));
        if (!_instance.IsOwner)
        {
            var forwarded = _instance.ForwardAsync(paths).GetAwaiter().GetResult();
            if (!forwarded)
                MessageBox.Show(LocalizationService.GetString("LaunchHandoffFailed"), "Scriblucent");
            Shutdown(forwarded ? 0 : 1);
            return;
        }
        _launchImports.Add(paths);
        var migration = LegacyDataMigration.Run(localAppData);
        if (migration == LegacyMigrationResult.Failed)
        {
            MessageBox.Show(LocalizationService.GetString("LegacyMigrationFailed"), "Scriblucent");
            Shutdown(1);
            return;
        }
        _instance.Listen(request => Dispatcher.BeginInvoke(new Action(() =>
        {
            _launchImports.Add(request);
            if (MainWindow is MainWindow main) main.ProcessLaunchImports(_launchImports);
        })));
        Scriblucent.Services.StartupProbe.Mark("onStartup");
        PopupScrollDragBehavior.Register();
        SmoothCaretBehavior.Register();

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

        // The Elevation.* DropShadowEffect tokens (Tokens.xaml) are static, theme-invariant
        // values that never change at runtime, so they can be shared as one instance instead of
        // cloned per consumer (they used to be x:Shared="False" specifically because an unfrozen
        // Freezable can only be owned by one property setter at a time — sharing one unfrozen
        // instance across multiple buttons/popups/dialogs would throw). Freezing first, before
        // any window is constructed, makes it safe to share.
        foreach (var elevationKey in ElevationTokenKeys)
        {
            if (Resources[elevationKey] is System.Windows.Freezable freezable && freezable.CanFreeze)
                freezable.Freeze();
        }

        // Contain UI-thread failures: log the full exception and keep the app alive. Without
        // Handled=true, ANY unhandled managed exception terminates the process, which turned
        // every transient editor glitch into a crash with no usable recovery.
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                CrashLog.Append("Unhandled", args.Exception);
                args.Handled = true;
            }
            catch
            {
                // Never let the handler itself take the process down.
                args.Handled = true;
            }
        };

        var settings = AppSettingsService.Load();
        _settingsInitialized = true;
        Scriblucent.Services.StartupProbe.Mark("settings");

        // Best-effort cleanup of old diagnostic and crash logs
        Task.Run(() => DiagnosticCleanup.CleanupOldLogs());

        ReducedMotionPreferences.Configure(settings.AnimationPreference ?? (settings.ReduceAnimations
            ? MotionPreference.Reduced : MotionPreference.Automatic));
        ReducedMotionPreferences.Refresh();
        Scriblucent.Rendering.SkiaBackdropBlur.BeginWarmup();
        LocalizationService.SetCulture(settings.Language);
        ThemeManager.SetTheme(settings.Theme);
        ThemeManager.FreezeCurrentThemeDictionary();
        Scriblucent.Services.StartupProbe.Mark("theme");
        GlobalScrollBarSettingsService.Apply(settings);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow(settings);
        Scriblucent.Services.StartupProbe.Mark("mainWindow");
        MainWindow = window;
        WindowThemeService.ApplyThemeWhenReady(window, ThemeManager.CurrentTheme);
        window.ContentRendered += (_, _) => Scriblucent.Services.StartupProbe.Mark("shown");
        window.ContentRendered += OnFirstContentRendered;
        void OnFirstContentRendered(object? sender, EventArgs args)
        {
            window.ContentRendered -= OnFirstContentRendered;
            if (migration == LegacyMigrationResult.Conflict)
                ModernMessageBox.Show(LocalizationService.GetString("LegacyMigrationConflict"), "Scriblucent",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            window.ProcessLaunchImports(_launchImports);
        }
        window.Show();

        // Startup probe: the first true idle after the library applied marks the moment the UI's
        // work (realization, animations, binds) has drained - the number the eyes measure.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(ProbeDashboardSettled));
    }

    private static void ProbeDashboardSettled()
    {
        // Gate on the bulk load's completion (bulkReset) - an idle between the library applying
        // and the worker finishing is a false settle.
        if (Scriblucent.Services.StartupProbe.HasMarked("bulkReset")
            && Scriblucent.Services.StartupProbe.HasMarked("recentStrip"))
        {
            Scriblucent.Services.StartupProbe.Mark("dashboardSettled");
            return;
        }

        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(ProbeDashboardSettled));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        WindowThemeService.ApplyThemeWhenReady(window, ThemeManager.CurrentTheme);
        UiMotion.ConfigurePopupsForWindow(window);
    }

    private static NoteCardViewModel? ResolveCard(object sender)
        => (sender as FrameworkElement)?.DataContext as NoteCardViewModel;

    private static MainWindow? CurrentMainWindow
        => Application.Current.MainWindow as MainWindow;

    private void OnNoteCardMenuOpen(object sender, RoutedEventArgs e)
    {
        if (ResolveCard(sender) is { } note)
            CurrentMainWindow?.OpenNoteEditor(note);
    }

    private void OnNoteCardMenuSelectMultiple(object sender, RoutedEventArgs e)
    {
        if (ResolveCard(sender) is { } note
            && Application.Current.MainWindow is MainWindow { DataContext: MainViewModel mainVm })
        {
            mainVm.DashboardSelection.Begin(note.Document.Id);
        }
    }

    private void OnNoteCardMenuInfo(object sender, RoutedEventArgs e)
    {
        if (ResolveCard(sender) is { } note)
            CurrentMainWindow?.ShowNoteInfo(note);
    }

    private void OnNoteCardMenuAssignSchedule(object sender, RoutedEventArgs e)
    {
        if (ResolveCard(sender) is { } note)
            CurrentMainWindow?.OpenNoteSchedule(note);
    }

    private void OnNoteCardMenuReplaceFromFile(object sender, RoutedEventArgs e)
    {
        if (ResolveCard(sender) is not { } viewModel)
            return;

        var dlg = new Microsoft.Win32.OpenFileDialog();
        dlg.Filter = LocalizationService.GetString("OpenFileDialogFilter");
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var path = dlg.FileName;
                if (NoteFileService.IsNotePackagePath(path))
                {
                    var package = NoteFileService.ReadNotePackage(path);
                    viewModel.Document.Content = package.Content ?? string.Empty;
                    viewModel.Document.Images = package.Images ?? new List<Models.NoteImageAttachment>();
                    if (string.IsNullOrWhiteSpace(viewModel.Document.Title) && !string.IsNullOrWhiteSpace(package.Title))
                        viewModel.Document.Title = package.Title;
                }
                else if (string.Equals(System.IO.Path.GetExtension(path), ".rtf", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.Document.Content = NoteFileService.LoadEditorContentFromFile(path);
                }
                else
                {
                    viewModel.Document.Content = NoteFileService.LoadEditorContentFromFile(path);
                    if (string.IsNullOrWhiteSpace(viewModel.Document.Title))
                        viewModel.Document.Title = System.IO.Path.GetFileNameWithoutExtension(path);
                }

                CurrentMainWindow?.OpenNoteEditor(viewModel);
            }
            catch (Exception ex)
            {
                var message = ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.Message
                    : LocalizationService.GetString("FailedToOpenFile");
                ModernMessageBox.Show(message, LocalizationService.GetString("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance?.IsOwner != true || !_settingsInitialized)
        {
            _instance?.Dispose();
            base.OnExit(e);
            return;
        }

        // Give a still-in-flight/queued notes save a bounded chance to finish before the process
        // tears down - otherwise a relaunch soon after can race the old instance's still-writing
        // file and see a transient I/O error that gets misreported as "file is damaged" (see
        // MainViewModel.FlushPendingNotesSaveBeforeExit).
        if (MainWindow?.DataContext is MainViewModel mainVm)
            mainVm.FlushPendingNotesSaveBeforeExit(TimeSpan.FromSeconds(5));

        try { Scriblucent.Services.AI.AiBackendHost.Shutdown(); } catch { }

        base.OnExit(e);
        AppSettingsService.Flush();
        _instance.Dispose();
    }
}
