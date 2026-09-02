using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Noted.Services;

internal sealed class FileOpenRequestService : IDisposable
{
    private const string PipeName = "Noted.FileOpen";
    private readonly Action<string> _openFile;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    internal FileOpenRequestService(Action<string> openFile)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        _openFile = openFile;
    }

    internal void Start()
    {
        _listener ??= ListenAsync();
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
            using var writer = new StreamWriter(client, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            writer.WriteLine(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cancellation.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var filePath = await reader.ReadLineAsync(_cancellation.Token);
                if (!string.IsNullOrWhiteSpace(filePath))
                    _openFile(filePath);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
