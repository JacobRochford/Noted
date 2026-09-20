using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ExceptionHandlingTests
{
    [TestMethod]
    public void DiagnosticsRetainStackAndInnerExceptionsAndRotateTheLog()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("exceptions.log");
        File.WriteAllText(path, new string('x', 1024 * 1024));
        var cause = Assert.ThrowsExactly<IOException>(() => throw new IOException("underlying failure"));
        var failure = new SettingsPersistenceException("controlled message", cause);

        ExceptionDiagnostics.Write(failure, path, "test operation");

        var log = File.ReadAllText(path);
        StringAssert.Contains(log, "test operation");
        StringAssert.Contains(log, failure.ToString());
        StringAssert.Contains(log, nameof(DiagnosticsRetainStackAndInnerExceptionsAndRotateTheLog));
        Assert.IsTrue(File.Exists(path + ".previous"));
    }

    [TestMethod]
    public void UnavailableDiagnosticFileDoesNotReplaceTheOriginalFailure()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("blocked");
        Directory.CreateDirectory(path);

        ExceptionDiagnostics.Write(new InvalidOperationException("original failure"), path, "test");

        Assert.IsTrue(Directory.Exists(path));
    }

    [TestMethod]
    public void RevisionExhaustionDoesNotManufactureAnInnerException()
    {
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{\"Revision\":9223372036854775807}");
        var settings = new AppSettingsService(directory.Path);

        var failure = Assert.ThrowsExactly<SettingsPersistenceException>(() => settings.SaveCustomHeader("changed"));

        Assert.IsNull(failure.InnerException);
        Assert.IsNull(Assert.ThrowsExactly<SettingsPersistenceException>(() => settings.SaveCustomHeader("retry")).InnerException);
    }

    [TestMethod]
    public void SettingsClockProgrammingFailurePropagatesUnchanged()
    {
        using var directory = new TemporaryTestDirectory();
        var failure = new InvalidOperationException("clock bug");
        var settings = new AppSettingsService(directory.Path, new FailingClock(failure), TimeSpan.Zero, 2);

        var observed = Assert.ThrowsExactly<InvalidOperationException>(() => settings.SaveCustomHeader("changed"));

        Assert.AreSame(failure, observed);
        Assert.IsFalse(File.Exists(directory.File("settings.json")));
    }

    [TestMethod]
    public void NoteHistoryArgumentFailureIsNotConvertedIntoASaveWarning()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "before");
        var failure = new ArgumentException("clock bug");
        var service = new NoteContentService(directory.Path, new FailingClock(failure), 2);

        var observed = Assert.ThrowsExactly<ArgumentException>(() => service.Save(path, "after"));

        Assert.AreSame(failure, observed);
        Assert.AreEqual("before", File.ReadAllText(path));
    }

    [TestMethod]
    public void InvalidAndDuplicateNoteNamesAreResults()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var service = new NoteFileService(settings);
        var created = service.CreateNote("existing");
        Assert.IsNotNull(created.Note);

        foreach (var name in new[] { "CON", "existing" })
        {
            var result = service.CreateNote(name);
            Assert.IsNull(result.Note);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
        }
    }

    [TestMethod]
    public void LockedRenameReturnsAControlledMessage()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var service = new NoteFileService(settings);
        var created = service.CreateNote("original");
        Assert.IsNotNull(created.Note);
        var path = Path.Combine(service.CurrentDirectory, created.Note.FileName);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = service.RenameNote(created.Note.FileName, "renamed");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("The note could not be renamed. Check folder access and whether the file is in use.", result.Error);
    }

    [TestMethod]
    [DataRow("{\"SchemaVersion\":\"invalid\"}")]
    [DataRow("{\"Revision\":false}")]
    public void InvalidBackupJsonTypesRemainValidationFailures(string json)
    {
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("settings.json"), json);
        var service = new FullBackupService(directory.Path, directory.File("notes"));

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task UpdateNetworkFailureIsNonfatalButProgrammingFailurePropagates()
    {
        using var offlineClient = new HttpClient(new FailingHttpHandler(new HttpRequestException("offline")));
        await UpdateService.CheckForUpdatesAsync(offlineClient);

        var failure = new InvalidOperationException("update bug");
        using var brokenClient = new HttpClient(new FailingHttpHandler(failure));
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => UpdateService.CheckForUpdatesAsync(brokenClient));
        Assert.AreSame(failure, observed);
    }

    [TestMethod]
    public async Task InvalidUpdateJsonIsNonfatal()
    {
        using var client = new HttpClient(new InvalidJsonHttpHandler());
        await UpdateService.CheckForUpdatesAsync(client);
    }

    [TestMethod]
    public void BackgroundTaskFailureIsObservedAndPostedToTheOwningContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new CapturingContext();
        var failure = new InvalidOperationException("background bug");
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            App.ObserveBackgroundTask(Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.IsNotNull(context.Callback);
        var observed = Assert.ThrowsExactly<InvalidOperationException>(() => context.Callback(context.State));
        Assert.AreSame(failure, observed);
    }

    private sealed class FailingClock(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }

    private sealed class FailingHttpHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class InvalidJsonHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") });
    }

    private sealed class CapturingContext : SynchronizationContext
    {
        internal SendOrPostCallback? Callback { get; private set; }
        internal object? State { get; private set; }
        public override void Post(SendOrPostCallback callback, object? state) => (Callback, State) = (callback, state);
    }
}
