using System.IO;
using System.Text.Json;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteSaveConflictTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpectedVersionRejectsChangedTextOrEncodingWithoutWriting(bool encodingOnly)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.md");
        File.WriteAllText(path, "original");
        var service = new NoteContentService(directory.Path);
        var expected = service.LoadWithEncoding(path);
        var external = TextFileCodec.Encode(encodingOnly ? "original" : "external", TextFileEncoding.Utf16BE);
        File.WriteAllBytes(path, external);

        var error = Assert.ThrowsExactly<NoteSaveConflictException>(() =>
            service.Save(path, "editor edits", TextFileEncoding.Utf8, expected));

        Assert.IsNull(error.PreservedFilePath);
        CollectionAssert.AreEqual(external, File.ReadAllBytes(path));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
        Assert.IsFalse(Directory.Exists(directory.File("note-history")));
    }

    [TestMethod]
    public void ExternalVersionMatchingIntendedSaveNeedsNoWrite()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.md");
        File.WriteAllText(path, "already saved externally");
        var service = new NoteContentService(directory.Path);

        Assert.IsNull(service.Save(path, "already saved externally", TextFileEncoding.Utf8,
            new TextFileContent("original", TextFileEncoding.Utf8)));

        Assert.HasCount(1, Directory.GetFiles(directory.Path));
        Assert.IsFalse(Directory.Exists(directory.File("note-history")));
    }

    [TestMethod]
    [DataRow("65787465726E616C", true)]
    [DataRow("FFFEE9002000AC20", true)]
    [DataRow("FEFF00E9002020AC", true)]
    [DataRow("E92080", true)]
    [DataRow("EFBBBF80", false)]
    public void LateConflictPreservesExactBytesAndRemainsAvailableAfterRestart(string hex, bool decodable)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.md");
        File.WriteAllText(path, "original");
        var external = Convert.FromHexString(hex);
        var clock = new CallbackTimeProvider(() => File.WriteAllBytes(path, external));
        var service = new NoteContentService(directory.Path, clock, 20);

        var error = Assert.ThrowsExactly<NoteSaveConflictException>(() =>
            service.Save(path, "editor edits", TextFileEncoding.Utf8, new("original", TextFileEncoding.Utf8)));

        Assert.IsNotNull(error.PreservedFilePath);
        Assert.AreEqual(directory.Path, Path.GetDirectoryName(error.PreservedFilePath));
        Assert.IsTrue(NoteFileExtensions.IsSupported(error.PreservedFilePath));
        CollectionAssert.AreEqual(external, File.ReadAllBytes(error.PreservedFilePath));
        Assert.AreEqual("editor edits", File.ReadAllText(path));
        StringAssert.Contains(error.Message, error.PreservedFilePath);
        var restarted = new NoteContentService(directory.Path);
        Assert.AreEqual("editor edits", restarted.Load(path));
        if (decodable)
        {
            Assert.AreEqual(TextFileCodec.Decode(external), restarted.LoadWithEncoding(error.PreservedFilePath));
            CollectionAssert.Contains(HistoryContents(directory), TextFileCodec.Decode(external).Content);
        }
        CollectionAssert.AreEqual(external, File.ReadAllBytes(error.PreservedFilePath));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void HistoryFailureDuringConflictDoesNotDiscardTheDisplacedFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "original");
        var clock = new CallbackTimeProvider(() =>
        {
            File.WriteAllText(path, "external");
            File.WriteAllText(directory.File("note-history"), "blocks history");
        });
        var service = new NoteContentService(directory.Path, clock, 20);

        var error = Assert.ThrowsExactly<NoteSaveConflictException>(() => service.Save(path, "editor edits"));

        Assert.AreEqual("external", File.ReadAllText(error.PreservedFilePath!));
        Assert.AreEqual("editor edits", File.ReadAllText(path));
    }

    [TestMethod]
    public void NormalSaveWithUnavailableHistoryKeepsPreviousBytesInANote()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(directory.File("note-history"), "blocks history");

        var warning = new NoteContentService(directory.Path).Save(path, "editor edits");

        var preserved = Directory.GetFiles(directory.Path, "noted-conflict-*.txt").Single();
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, preserved);
        Assert.AreEqual("original", File.ReadAllText(preserved));
        Assert.AreEqual("editor edits", File.ReadAllText(path));
    }

    [TestMethod]
    public void TargetDeletedDuringSaveIsNotRecreated()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "original");
        var clock = new CallbackTimeProvider(() => File.Delete(path));
        var service = new NoteContentService(directory.Path, clock, 20);

        Assert.Throws<IOException>(() => service.Save(path, "editor edits"));

        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(0, Directory.GetFiles(directory.Path));
        CollectionAssert.Contains(HistoryContents(directory), "original");
    }

    [TestMethod]
    public void RetryAfterConflictKeepsThePreservedVersion()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "original");
        var clock = new CallbackTimeProvider(() => File.WriteAllText(path, "external"));
        var service = new NoteContentService(directory.Path, clock, 20);
        var expected = new TextFileContent("original", TextFileEncoding.Utf8);
        var error = Assert.ThrowsExactly<NoteSaveConflictException>(() => service.Save(path, "editor edits", expectedVersion: expected));

        Assert.IsNull(service.Save(path, "editor edits", expectedVersion: expected));
        service.Save(path, "later editor edits");

        Assert.AreEqual("external", File.ReadAllText(error.PreservedFilePath!));
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "noted-conflict-*.txt"));
    }

    [TestMethod]
    public void FirstSaveAsConflictKeepsInitialNoteAndBothDestinationVersions()
    {
        using var directory = new TemporaryTestDirectory();
        var initial = directory.File("initial.md");
        var destination = directory.File("chosen.md");
        File.WriteAllText(initial, "initial text");
        File.WriteAllText(destination, "destination before save");
        var clock = new CallbackTimeProvider(() => File.WriteAllText(destination, "external destination"));
        var service = new NoteContentService(directory.Path, clock, 20);

        var error = Assert.ThrowsExactly<NoteSaveConflictException>(() =>
            service.SaveNewNoteAs(initial, destination, "editor edits", "initial text"));

        Assert.AreEqual("initial text", File.ReadAllText(initial));
        Assert.AreEqual("editor edits", File.ReadAllText(destination));
        Assert.AreEqual("external destination", File.ReadAllText(error.PreservedFilePath!));
    }

    private static string[] HistoryContents(TemporaryTestDirectory directory) =>
        Directory.GetFiles(directory.File("note-history"), "*.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.GetProperty("Content").GetString()!;
            }).ToArray();
}
