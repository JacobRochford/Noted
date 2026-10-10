using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueuedFileChangeCannotRestartRefreshAfterDisposal(bool startTimerBeforeDisposal) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        var callbacks = new Queue<Action>();
        using var model = new MainWindowViewModel(files, settings, callbacks.Enqueue);
        Assert.IsTrue(Directory.Exists(files.NotesDirectory));
        var loads = 0;
        model.NotesLoaded += (_, _) => loads++;

        if (startTimerBeforeDisposal)
        {
            RaiseFilesChanged(files);
            callbacks.Dequeue()();
            Assert.IsTrue(GetTimer(model)!.IsEnabled);
        }

        // leave a callback queued until after disposal.
        RaiseFilesChanged(files);
        Assert.HasCount(1, callbacks);
        var timer = GetTimer(model);
        model.Dispose();
        files.Dispose();
        directory.Dispose();
        Assert.IsFalse(Directory.Exists(files.NotesDirectory));

        callbacks.Dequeue()();

        Assert.IsNull(GetTimer(model), "An already queued callback must not recreate the disposed timer.");
        if (timer is not null)
            Assert.IsFalse(timer.IsEnabled);
        Assert.AreEqual(0, loads);
        model.Dispose();
    });

    [TestMethod]
    public void DisposeCancelsPendingRefreshAndUnsubscribesFromFileChanges() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        using var model = new MainWindowViewModel(files, settings, action => action());
        RaiseFilesChanged(files);
        var timer = GetTimer(model);
        Assert.IsNotNull(timer);
        Assert.IsTrue(timer.IsEnabled);

        model.Dispose();
        RaiseFilesChanged(files);

        Assert.IsFalse(timer.IsEnabled);
        Assert.IsNull(GetTimer(model));
    });

    [TestMethod]
    public void RefreshAfterNotesDirectoryDisappearsReturnsAnEmptyListing() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        File.WriteAllText(directory.File("notes", "note.txt"), "content");
        using var model = new MainWindowViewModel(files, settings, action => action());
        model.LoadNotes();
        Assert.HasCount(1, model.Notes);
        files.Dispose();
        directory.Dispose();

        model.LoadNotes();

        Assert.IsEmpty(model.Notes);
        Assert.IsFalse(Directory.Exists(files.NotesDirectory));
    });

    private static DispatcherTimer? GetTimer(MainWindowViewModel model) =>
        (DispatcherTimer?)typeof(MainWindowViewModel)
            .GetField("_debounceTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);

    private static void RaiseFilesChanged(NoteFileService files) =>
        typeof(NoteFileService).GetMethod("OnFilesChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(files, [files, EventArgs.Empty]);
}
