using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteDirectoryWatcherTests
{
    [TestMethod]
    public void WatcherReportsSupportedFileChanges()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        using var watcher = new NoteDirectoryWatcher();
        using var changed = new ManualResetEventSlim();
        watcher.Changed += (_, _) => changed.Set();
        watcher.Start(notesDirectory);

        File.WriteAllText(Path.Combine(notesDirectory, "note.txt"), "content");

        Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(5)), "The watcher did not report the file change.");
    }

    [TestMethod]
    public void WatcherReportsDirectoryChanges()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        using var watcher = new NoteDirectoryWatcher();
        using var changed = new ManualResetEventSlim();
        watcher.Changed += (_, _) => changed.Set();
        watcher.Start(notesDirectory);

        Directory.CreateDirectory(Path.Combine(notesDirectory, "folder"));

        Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(5)), "The watcher did not report the directory change.");
    }

    [TestMethod]
    public void WatcherErrorRequestsDirectoryRescan()
    {
        using var watcher = new NoteDirectoryWatcher();
        var rescanRequests = 0;
        watcher.Changed += (_, _) => rescanRequests++;

        watcher.RequestRescanAfterError(new InternalBufferOverflowException("Events were lost."));

        Assert.AreEqual(1, rescanRequests);
    }
}
