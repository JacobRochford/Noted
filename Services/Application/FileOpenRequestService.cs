using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Noted.Services;

internal sealed class FileOpenRequestService : IDisposable, IAsyncDisposable
{
    private const string PipeName = "Noted.FileOpen";
    private readonly Action<string> _openFile;
    private readonly Action<Action> _dispatch;
    private readonly string _pipeName;
    private readonly object _lifecycleLock = new();
    private volatile bool _stopping;
    private Task? _disposeTask;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    internal FileOpenRequestService(
        Action<string> openFile,
        Action<Action> dispatch,
        string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        ArgumentNullException.ThrowIfNull(dispatch);
        _openFile = openFile;
        _dispatch = dispatch;
        _pipeName = pipeName ?? PipeName;
    }

    internal Task Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            // The listener must never depend on the UI thread that waits for teardown.
            return _listener ??= Task.Run(ListenAsync);
        }
    }

    internal static bool TrySend(string filePath, int timeoutMilliseconds = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);
            client.Connect(timeoutMilliseconds);
            // Pass Explorer's foreground permission to the running instance before
            // it receives the request and activates the editor. Still open the file
            // if Windows declines the foreground request.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
                _ = AllowSetForegroundWindow(serverProcessId);
            using var writer = new StreamWriter(client, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            writer.WriteLine(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            ExceptionDiagnostics.Record(ex);
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            string? filePath;
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                filePath = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ExceptionDiagnostics.Record(ex);
                // wait briefly before retrying the pipe
                await Task.Delay(250).ConfigureAwait(false);
                continue;
            }

            if (!_stopping && !string.IsNullOrWhiteSpace(filePath))
            {
                _dispatch(() =>
                {
                    // shutdown may start before this runs on the UI thread
                    if (!_stopping)
                        _openFile(filePath);
                });
            }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                _stopping = true;
                _disposeTask = StopListenerAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopListenerAsync()
    {
        try
        {
            _cancellation.Cancel();
            if (_listener is not null)
                await _listener.ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
