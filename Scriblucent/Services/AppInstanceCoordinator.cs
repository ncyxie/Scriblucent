using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scriblucent.Services;

internal sealed class AppInstanceCoordinator : IDisposable
{
    private const int MaxMessageBytes = 65536;
    private readonly Mutex _ownership;
    private readonly Mutex? _installerMarker;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private bool _disposed;
    internal bool IsOwner { get; }

    internal AppInstanceCoordinator(string dataDirectory)
    {
        var identity = WindowsIdentity.GetCurrent().User!.Value;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity + "|" + Path.GetFullPath(dataDirectory).TrimEnd('\\').ToUpperInvariant())));
        _pipeName = "scriblucent-launch-" + key;
        _ownership = new Mutex(false, @"Global\Scriblucent.Library." + key);
        try { IsOwner = _ownership.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
        if (IsOwner) _installerMarker = new Mutex(false, "Scriblucent.Running");
    }

    internal void Listen(Action<string[]> onLaunch)
    {
        if (!IsOwner) throw new InvalidOperationException("Only the library owner can listen.");
        _ = ListenAsync(onLaunch);
    }

    private async Task ListenAsync(Action<string[]> onLaunch)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                var length = BitConverter.ToInt32(header);
                if (length is < 2 or > MaxMessageBytes) continue;
                var message = new byte[length];
                await pipe.ReadExactlyAsync(message, deadline.Token).ConfigureAwait(false);
                var paths = JsonSerializer.Deserialize<string[]>(message);
                if (paths is null || paths.Length > 128 || paths.Any(path => path is null)) continue;
                onLaunch(paths);
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }

    internal async Task<bool> ForwardAsync(string[] paths)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (message.Length > MaxMessageBytes || paths.Length > 128) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var ownerProcess))
                AllowSetForegroundWindow(ownerProcess);
            await pipe.WriteAsync(BitConverter.GetBytes(message.Length), timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _installerMarker?.Dispose();
        if (IsOwner) _ownership.ReleaseMutex();
        _ownership.Dispose();
        // Pending asynchronous pipe operations still observe the cancellation source.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
