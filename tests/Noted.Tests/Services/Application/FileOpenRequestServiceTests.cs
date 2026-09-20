using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileOpenRequestServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task RequestsAreQueuedAndDeliveredOnceWhileRunning()
    {
        var pipeName = NewPipeName();
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new List<string>();
        await using var service = new FileOpenRequestService(opened.Add, action => queued.SetResult(action), pipeName);
        _ = service.Start();
        _ = service.Start();

        const string path = @"C:\external notes\example.md";
        await SendAsync(pipeName, path);
        var deliver = await queued.Task.WaitAsync(TestTimeout);
        Assert.HasCount(0, opened);
        deliver();
        CollectionAssert.AreEqual(new[] { path }, opened);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RequestsAlreadyQueuedAreDiscardedAfterTeardown(bool asynchronous)
    {
        var pipeName = NewPipeName();
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new List<string>();
        await using var service = new FileOpenRequestService(opened.Add, action => queued.SetResult(action), pipeName);
        _ = service.Start();
        await SendAsync(pipeName, "pending.txt");
        var deliver = await queued.Task.WaitAsync(TestTimeout);

        if (asynchronous)
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        else
            await Task.Run(service.Dispose).WaitAsync(TestTimeout);

        deliver();
        Assert.HasCount(0, opened);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TeardownWaitsForTheListenerAndReleasesThePipe(bool connectWithoutSending)
    {
        var pipeName = NewPipeName();
        await using var service = new FileOpenRequestService(_ => Assert.Fail("Unexpected delivery."), _ => { }, pipeName);
        _ = service.Start();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        if (connectWithoutSending)
            await client.ConnectAsync(5000);

        var firstStop = service.DisposeAsync().AsTask();
        var secondStop = service.DisposeAsync().AsTask();
        Assert.AreSame(firstStop, secondStop);
        await firstStop.WaitAsync(TestTimeout);

        // A completed teardown must release the single-instance pipe, even with an unfinished request.
        using var replacement = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Assert.ThrowsExactly<ObjectDisposedException>(() => { service.Start(); });
    }

    [TestMethod]
    public async Task DisposingBeforeStartIsSafeAndPreventsStartingLater()
    {
        var service = new FileOpenRequestService(_ => { }, _ => { }, NewPipeName());
        await service.DisposeAsync();
        service.Dispose();
        await service.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => { service.Start(); });
    }

    [TestMethod]
    public async Task SynchronousTeardownDoesNotRequireTheStartingSynchronizationContext()
    {
        await Task.Run(() =>
        {
            var previousContext = SynchronizationContext.Current;
            var context = new UnpumpedSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var pipeName = NewPipeName();
                using var service = new FileOpenRequestService(_ => { }, _ => { }, pipeName);
                _ = service.Start();
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(5000);
                service.Dispose();

                using var replacement = new NamedPipeServerStream(
                    pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Assert.AreEqual(0, context.PostCount);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }).WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task ListenerFailureIsObservableImmediatelyAndDuringTeardown()
    {
        var pipeName = NewPipeName();
        var dispatchAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Could not queue the request.");
        var service = new FileOpenRequestService(_ => { }, _ =>
        {
            dispatchAttempted.SetResult();
            throw failure;
        }, pipeName);

        try
        {
            var listener = service.Start();
            await SendAsync(pipeName, "pending.txt");
            await dispatchAttempted.Task.WaitAsync(TestTimeout);
            var immediate = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => listener.WaitAsync(TestTimeout));
            Assert.AreSame(failure, immediate);
            var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.DisposeAsync().AsTask().WaitAsync(TestTimeout));
            Assert.AreSame(failure, observed);
        }
        finally
        {
            try
            {
                await service.DisposeAsync();
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
            {
            }
        }
    }

    private static string NewPipeName() => $"Noted.Tests.FileOpen.{Guid.NewGuid():N}";

    private static async Task SendAsync(string pipeName, string path)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(path);
        await writer.FlushAsync();
    }

    private sealed class UnpumpedSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
            => Interlocked.Increment(ref _postCount);
    }
}
