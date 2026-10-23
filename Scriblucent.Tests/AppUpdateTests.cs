using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class AppUpdateTests
{
    private static GitHubUpdateResult Available(string version = "v1.0.0-beta.4") => new()
    {
        CurrentVersion = "1.0.0-beta.3", HasComparableVersions = true, IsUpdateAvailable = true,
        LatestRelease = new() { Name = version, TagName = version, HtmlUrl = GitHubUpdateService.ReleasesUrl }
    };

    private static AppUpdateCoordinator Coordinator(AppSettings settings,
        Func<CancellationToken, Task<GitHubUpdateResult>> check,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<AppSettings>? save = null)
        => new(() => settings, save ?? (_ => { }), check, action => { action(); return Task.CompletedTask; },
            delay ?? ((_, token) => Task.CompletedTask), _ => { });

    [Fact]
    public void Existing_settings_default_to_startup_checks_and_respect_saved_opt_out()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.CheckForUpdatesOnStartup);
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"CheckForUpdatesOnStartup\":false}")!;
        Assert.False(settings.CheckForUpdatesOnStartup);
        Assert.False(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.CheckForUpdatesOnStartup);
    }

    [Fact]
    public async Task Disabled_startup_never_requests_updates_but_manual_check_still_works()
    {
        int calls = 0;
        using var coordinator = Coordinator(new() { CheckForUpdatesOnStartup = false }, _ => { calls++; return Task.FromResult(Available()); });
        await coordinator.StartAfterDashboardReadyAsync();
        Assert.Equal(0, calls);
        await coordinator.CheckManuallyAsync();
        Assert.Equal(1, calls);
        Assert.True(coordinator.HasAvailableUpdate);
        Assert.False(coordinator.HasPendingNotice);
    }

    [Fact]
    public async Task Startup_runs_once_and_acknowledgement_survives_restart_without_hiding_badge()
    {
        int calls = 0;
        var settings = new AppSettings();
        AppSettings? saved = null;
        using (var coordinator = Coordinator(settings, _ => { calls++; return Task.FromResult(Available()); }, save: value => saved = value))
        {
            await coordinator.StartAfterDashboardReadyAsync();
            await coordinator.StartAfterDashboardReadyAsync();
            Assert.Equal(1, calls);
            Assert.True(coordinator.HasPendingNotice);
            coordinator.AcknowledgeAvailableUpdate();
            Assert.False(coordinator.HasPendingNotice);
            Assert.True(coordinator.HasAvailableUpdate);
        }
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
        using (var coordinator = Coordinator(restored, _ => Task.FromResult(Available("1.0.0-beta.4"))))
        {
            await coordinator.StartAfterDashboardReadyAsync();
            Assert.True(coordinator.HasAvailableUpdate);
            Assert.False(coordinator.HasPendingNotice);
        }
        using (var coordinator = Coordinator(restored, _ => Task.FromResult(Available("v1.0.0"))))
        {
            await coordinator.StartAfterDashboardReadyAsync();
            Assert.True(coordinator.HasPendingNotice);
        }
    }

    [Fact]
    public async Task Opt_out_cancels_scheduled_check_and_is_saved()
    {
        int calls = 0;
        var settings = new AppSettings();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(settings, _ => { calls++; return Task.FromResult(Available()); },
            (_, token) => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); });
        var startup = coordinator.StartAfterDashboardReadyAsync();
        await entered.Task;
        coordinator.SetAutomaticChecksEnabled(false);
        await startup.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, calls);
        Assert.False(settings.CheckForUpdatesOnStartup);
    }

    [Fact]
    public async Task Manual_check_during_startup_delay_prevents_a_second_request_and_notice()
    {
        int calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(new(), _ => { calls++; return Task.FromResult(Available()); },
            (_, token) => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); });
        var startup = coordinator.StartAfterDashboardReadyAsync();
        await entered.Task;
        await coordinator.CheckManuallyAsync();
        await startup.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls);
        Assert.False(coordinator.HasPendingNotice);
    }

    [Fact]
    public async Task Manual_and_startup_share_in_flight_request_even_when_automatic_check_is_disabled()
    {
        int calls = 0;
        CancellationToken requestToken = default;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(new(), token =>
        {
            calls++; requestToken = token; entered.SetResult(); return response.Task.WaitAsync(token);
        });
        var startup = coordinator.StartAfterDashboardReadyAsync();
        await entered.Task;
        var manual = coordinator.CheckManuallyAsync();
        coordinator.SetAutomaticChecksEnabled(false);
        Assert.False(requestToken.IsCancellationRequested);
        response.SetResult(Available());
        await Task.WhenAll(startup, manual).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls);
        Assert.True(coordinator.HasAvailableUpdate);
        Assert.False(coordinator.HasPendingNotice);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Opt_out_or_shutdown_cancels_an_automatic_request(bool shutdown)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(new(), async token =>
        {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Available();
        });
        var startup = coordinator.StartAfterDashboardReadyAsync();
        await entered.Task;
        if (shutdown) coordinator.Dispose(); else coordinator.SetAutomaticChecksEnabled(false);
        await startup.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(coordinator.HasAvailableUpdate);
        Assert.False(coordinator.HasPendingNotice);
    }

    [Theory]
    [InlineData("1.0.0-beta.3", "v1.0.0-beta.4", true)]
    [InlineData("1.0.0-beta.9", "v1.0.0-beta.10", true)]
    [InlineData("1.0.0-beta.3", "v1.0.0", true)]
    [InlineData("1.0.0", "v1.0.0-beta.4", false)]
    [InlineData("1.0.0", "v1.0.0", false)]
    [InlineData("1.1.0", "v1.0.0", false)]
    [InlineData("1.0.0", "v1.1.0+build.1", true)]
    [InlineData("1.0.0", "not-a-version", false)]
    public async Task Release_versions_are_compared_over_real_http(string current, string release, bool available)
    {
        await using var server = new ReleaseServer(200, JsonSerializer.Serialize(new[]
        {
            new { tag_name = release, html_url = GitHubUpdateService.ReleasesUrl, draft = false },
            new { tag_name = "v99.0.0", html_url = GitHubUpdateService.ReleasesUrl, draft = true }
        }));
        using var client = new HttpClient();
        var result = await new GitHubUpdateService(client, current, server.Url).CheckForUpdatesAsync();
        Assert.Equal(available, result.IsUpdateAvailable);
        Assert.Equal(release, result.LatestRelease!.TagName);
        Assert.Equal("/repos/ncyxie/Scriblucent/releases?per_page=20", server.RequestPath);
    }

    [Theory]
    [InlineData(200, "[]")]
    [InlineData(200, "not JSON")]
    [InlineData(403, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(429, "{\"message\":\"Too many requests\"}")]
    [InlineData(500, "<html>Upstream error</html>")]
    public async Task Background_http_failures_and_empty_releases_remain_silent(int status, string body)
    {
        await using var server = new ReleaseServer(status, body);
        using var client = new HttpClient();
        var service = new GitHubUpdateService(client, "1.0.0", server.Url);
        using var coordinator = Coordinator(new(), service.CheckForUpdatesAsync);
        await coordinator.StartAfterDashboardReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.HasAvailableUpdate);
        Assert.False(coordinator.HasPendingNotice);
        if (status != 200) await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.CheckManuallyAsync());
    }

    [Fact]
    public async Task Hanging_http_request_times_out_without_blocking_startup_caller()
    {
        await using var server = new ReleaseServer(200, "[]", TimeSpan.FromSeconds(5));
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new GitHubUpdateService(client, "1.0.0", server.Url);
        using var coordinator = Coordinator(new(), service.CheckForUpdatesAsync);
        var startup = coordinator.StartAfterDashboardReadyAsync();
        Assert.False(startup.IsCompleted);
        await startup.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(coordinator.HasAvailableUpdate);
    }

    [Fact]
    public async Task Unreachable_endpoint_is_silent_automatically_but_explicit_checks_report_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/releases");
        listener.Stop();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var service = new GitHubUpdateService(client, "1.0.0", url);
        using var coordinator = Coordinator(new(), service.CheckForUpdatesAsync);
        await coordinator.StartAfterDashboardReadyAsync();
        Assert.False(coordinator.HasAvailableUpdate);
        var failure = await Record.ExceptionAsync(() => coordinator.CheckManuallyAsync());
        Assert.True(failure is HttpRequestException or TaskCanceledException, failure?.ToString());
    }

    private sealed class ReleaseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        public Uri Url { get; }
        public string? RequestPath { get; private set; }

        public ReleaseServer(int status, string body, TimeSpan delay = default)
        {
            _listener.Start();
            Url = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/repos/ncyxie/Scriblucent/releases?per_page=20");
            _serve = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    RequestPath = (await reader.ReadLineAsync(_stop.Token))?.Split(' ')[1];
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, _stop.Token);
                    byte[] content = Encoding.UTF8.GetBytes(body);
                    byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _serve; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            _stop.Dispose();
        }
    }
}
