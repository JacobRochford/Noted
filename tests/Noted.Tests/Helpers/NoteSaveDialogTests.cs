using System.IO;
using Noted.Helpers;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteSaveDialogTests
{
    [TestMethod]
    public void EverySupportedTypeCanBeSelectedOrTypedWithTheFilterKeptConsistent() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new NoteContentService(directory.Path);
        for (var index = 0; index < NoteFileExtensions.SupportedExtensions.Count; index++)
        {
            var extension = NoteFileExtensions.SupportedExtensions[index];
            var dialog = NoteSaveDialog.Create(directory.File("2026-11-05_10-20-30.txt"));
            Assert.IsFalse(dialog.OverwritePrompt);
            Assert.AreEqual("2026-11-05_10-20-30", dialog.FileName);
            dialog.FilterIndex = index + 1;
            dialog.FileName = directory.File("selected");
            var selected = NoteSaveDialog.ConfirmDestination(dialog, service, _ => throw new AssertFailedException("No overwrite expected."));
            Assert.AreEqual(directory.File("selected" + extension), selected!.FilePath);
            Assert.IsNull(selected.ExistingContent);

            dialog.FilterIndex = 1;
            dialog.FileName = directory.File("typed" + extension.ToUpperInvariant());
            var typed = NoteSaveDialog.ConfirmDestination(dialog, service, _ => throw new AssertFailedException("No overwrite expected."));
            Assert.AreEqual(directory.File("typed" + extension.ToUpperInvariant()), typed!.FilePath);
            Assert.AreEqual(index + 1, dialog.FilterIndex);
        }
    });

    [TestMethod]
    [DataRow("unsupported.exe")]
    [DataRow("CON.txt")]
    [DataRow("NUL.json")]
    [DataRow("invalid?.md")]
    [DataRow("name.txt ")]
    [DataRow("name.")]
    public void InvalidNamesAreRejectedWithoutCreatingFiles(string name) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var dialog = NoteSaveDialog.Create(directory.File("initial.txt"));
        dialog.FileName = directory.File(name);

        Assert.ThrowsExactly<ArgumentException>(() => NoteSaveDialog.ConfirmDestination(
            dialog, new NoteContentService(directory.Path), _ => true));

        Assert.HasCount(0, Directory.GetFiles(directory.Path));
    });

    [TestMethod]
    public void FileAppearingWhileThePickerIsOpenRequiresConfirmation() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("destination.md");
        var dialog = NoteSaveDialog.Create(directory.File("initial.txt"));
        dialog.FileName = path;
        Assert.IsFalse(File.Exists(path));
        File.WriteAllText(path, "created while choosing");
        var prompts = 0;

        var destination = NoteSaveDialog.ConfirmDestination(dialog, new NoteContentService(directory.Path), requested =>
        {
            Assert.AreEqual(path, requested);
            prompts++;
            return false;
        });

        Assert.IsNull(destination);
        Assert.AreEqual(1, prompts);
        Assert.AreEqual("created while choosing", File.ReadAllText(path));
    });
}
