using System.ComponentModel;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Shares update checks and acknowledgement state between startup and Settings.</summary>
public sealed class AppUpdateCoordinator : INotifyPropertyChanged, IDisposable
{
    private sealed class Request(CancellationTokenSource cancellation, bool automatic)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Automatic { get; } = automatic;
        public bool HasManualWaiter { get; set; } = !automatic;
        public Task<GitHubUpdateResult> Task { get; set; } = null!;
    }

    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<CancellationToken, Task<GitHubUpdateResult>> _check;
    private readonly Func<Action, Task> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Exception> _reportFailure;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _startup = new();
    private Request? _request;
    private bool _startupStarted;
    private bool _disposed;
    private bool _noticeRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GitHubUpdateResult? LastResult { get; private set; }
    public bool HasAvailableUpdate => LastResult is { IsUpdateAvailable: true, HasComparableVersions: true, LatestRelease: not null };
    public string AvailableVersion => HasAvailableUpdate ? LastResult!.LatestRelease!.TagName : string.Empty;
    public bool AutomaticChecksEnabled => _settings().CheckForUpdatesOnStartup;
    public bool HasPendingNotice => _noticeRequested && AutomaticChecksEnabled && HasAvailableUpdate
        && !string.Equals(VersionKey(AvailableVersion), VersionKey(_settings().DismissedUpdateVersion), StringComparison.OrdinalIgnoreCase);

    internal AppUpdateCoordinator(Func<AppSettings> settings, Action<AppSettings> save,
        Func<CancellationToken, Task<GitHubUpdateResult>> check, Func<Action, Task> dispatch,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<Exception>? reportFailure = null)
    {
        _settings = settings;
        _save = save;
        _check = check;
        _dispatch = dispatch;
        _delay = delay ?? Task.Delay;
        _reportFailure = reportFailure ?? (ex => CrashLog.Append("StartupUpdateCheck", ex));
    }

    internal static AppUpdateCoordinator Create(System.Windows.Threading.Dispatcher dispatcher)
    {
        var service = new GitHubUpdateService();
        return new(AppSettingsService.Load, AppSettingsService.Save, service.CheckForUpdatesAsync,
            action => dispatcher.HasShutdownStarted ? Task.CompletedTask
                : dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Background).Task);
    }

    /// <summary>Called after the initial dashboard settles, never on its loading path.</summary>
    public Task StartAfterDashboardReadyAsync()
    {
        lock (_sync)
        {
            if (_disposed || _startupStarted) return Task.CompletedTask;
            _startupStarted = true;
        }
        return RunStartupAsync();
    }

    private async Task RunStartupAsync()
    {
        if (!AutomaticChecksEnabled) return;
        try
        {
            await _delay(TimeSpan.FromSeconds(2), _startup.Token).ConfigureAwait(false);
            Task<GitHubUpdateResult>? task = null;
            await _dispatch(() =>
            {
                if (!_disposed && !_startup.IsCancellationRequested && AutomaticChecksEnabled)
                    task = GetRequest(automatic: true);
            }).ConfigureAwait(false);
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Automatic checks are advisory. Offline/failed checks must not interrupt work.
            _reportFailure(ex);
        }
    }

    public Task<GitHubUpdateResult> CheckManuallyAsync()
    {
        // An explicit check supersedes the scheduled check for this launch.
        _startup.Cancel();
        return GetRequest(automatic: false);
    }

    private Task<GitHubUpdateResult> GetRequest(bool automatic)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_request is { } existing && !existing.Task.IsCompleted && !existing.Cancellation.IsCancellationRequested)
            {
                existing.HasManualWaiter |= !automatic;
                return existing.Task;
            }
            var request = new Request(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), automatic);
            _request = request;
            request.Task = Task.Run(() => FetchAsync(request));
            return request.Task;
        }
    }

    private async Task<GitHubUpdateResult> FetchAsync(Request request)
    {
        try
        {
            var result = await _check(request.Cancellation.Token).ConfigureAwait(false);
            await _dispatch(() =>
            {
                if (_disposed || request.Cancellation.IsCancellationRequested) return;
                LastResult = result;
                _noticeRequested = request.Automatic && !request.HasManualWaiter && !_startup.IsCancellationRequested;
                NotifyState();
            }).ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_request, request)) _request = null;
                request.Cancellation.Dispose();
            }
        }
    }

    public void SetAutomaticChecksEnabled(bool enabled)
    {
        var settings = _settings();
        settings.CheckForUpdatesOnStartup = enabled;
        _save(settings);
        if (!enabled)
        {
            _startup.Cancel();
            _noticeRequested = false;
            lock (_sync)
                if (_request is { HasManualWaiter: false } request) request.Cancellation.Cancel();
        }
        NotifyState();
    }

    public void AcknowledgeAvailableUpdate()
    {
        if (!HasAvailableUpdate) return;
        var settings = _settings();
        settings.DismissedUpdateVersion = VersionKey(AvailableVersion);
        _save(settings);
        _noticeRequested = false;
        NotifyState();
    }

    private static string VersionKey(string? value) => (value ?? string.Empty).Trim().TrimStart('v', 'V').Split('+')[0];

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(LastResult), nameof(HasAvailableUpdate), nameof(AvailableVersion), nameof(AutomaticChecksEnabled), nameof(HasPendingNotice) })
            PropertyChanged?.Invoke(this, new(property));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _startup.Cancel();
            _lifetime.Cancel();
        }
    }
}
