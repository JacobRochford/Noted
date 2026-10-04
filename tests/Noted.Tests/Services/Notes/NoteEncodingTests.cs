using System.IO;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEncodingTests
{
    [TestMethod]
    [DataRow(TextFileEncoding.Utf8, "C3A920E282AC", "21")]
    [DataRow(TextFileEncoding.Utf8Bom, "EFBBBFC3A920E282AC", "21")]
    [DataRow(TextFileEncoding.Utf16LE, "FFFEE9002000AC20", "2100")]
    [DataRow(TextFileEncoding.Utf16BE, "FEFF00E9002020AC", "0021")]
    [DataRow(TextFileEncoding.Windows1252, "E92080", "21")]
    [DataRow(TextFileEncoding.Utf32LE, "FFFE0000E900000020000000AC200000", "21000000")]
    [DataRow(TextFileEncoding.Utf32BE, "0000FEFF000000E900000020000020AC", "00000021")]
    public void OpeningAndSavingPreservesEncoding(TextFileEncoding encoding, string hex, string suffix)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        var original = Convert.FromHexString(hex);
        File.WriteAllBytes(path, original);
        var service = new NoteContentService(directory.Path);

        var loaded = service.LoadWithEncoding(path);
        Assert.AreEqual("é €", loaded.Content);
        Assert.AreEqual(encoding, loaded.Encoding);
        service.Save(path, loaded.Content);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));

        service.Save(path, loaded.Content + "!");
        CollectionAssert.AreEqual(Convert.FromHexString(hex + suffix), File.ReadAllBytes(path));
        Assert.AreEqual(encoding, service.LoadWithEncoding(path).Encoding);
    }

    [TestMethod]
    [DataRow(TextFileEncoding.Utf8, "C3A920E282AC")]
    [DataRow(TextFileEncoding.Utf8Bom, "EFBBBFC3A920E282AC")]
    [DataRow(TextFileEncoding.Utf16LE, "FFFEE9002000AC20")]
    [DataRow(TextFileEncoding.Utf16BE, "FEFF00E9002020AC")]
    [DataRow(TextFileEncoding.Windows1252, "E92080")]
    public void ExplicitConversionChangesBytesEvenWhenTextIsUnchanged(TextFileEncoding target, string hex)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        // Start with BE UTF-32 to exercise conversion to every menu choice.
        File.WriteAllBytes(path, Convert.FromHexString("0000FEFF000000E900000020000020AC"));
        var service = new NoteContentService(directory.Path);

        service.Save(path, "é €", target);

        CollectionAssert.AreEqual(Convert.FromHexString(hex), File.ReadAllBytes(path));
        Assert.AreEqual(target, service.LoadWithEncoding(path).Encoding);
    }

    [TestMethod]
    [DataRow(TextFileEncoding.Utf8, "")]
    [DataRow(TextFileEncoding.Utf8Bom, "EFBBBF")]
    [DataRow(TextFileEncoding.Utf16LE, "FFFE")]
    [DataRow(TextFileEncoding.Utf16BE, "FEFF")]
    public void EmptyFilesRetainTheirBom(TextFileEncoding encoding, string hex)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("empty.txt");
        File.WriteAllBytes(path, Convert.FromHexString(hex));
        var service = new NoteContentService(directory.Path);

        Assert.AreEqual(new TextFileContent(string.Empty, encoding), service.LoadWithEncoding(path));
        service.Save(path, string.Empty);
        CollectionAssert.AreEqual(Convert.FromHexString(hex), File.ReadAllBytes(path));
    }

    [TestMethod]
    public void BomlessAsciiAndEmptyFilesDefaultToUtf8()
    {
        Assert.AreEqual(new TextFileContent("hello", TextFileEncoding.Utf8),
            TextFileCodec.Decode(Convert.FromHexString("68656C6C6F")));
        Assert.AreEqual(new TextFileContent(string.Empty, TextFileEncoding.Utf8), TextFileCodec.Decode([]));
    }

    [TestMethod]
    public void BomlessValidUtf8WinsOverAmbiguousWindows1252Bytes()
    {
        Assert.AreEqual(new TextFileContent("é", TextFileEncoding.Utf8),
            TextFileCodec.Decode(Convert.FromHexString("C3A9")));
        Assert.AreEqual(new TextFileContent("“café” €", TextFileEncoding.Windows1252),
            TextFileCodec.Decode(Convert.FromHexString("93636166E9942080")));
    }

    [TestMethod]
    [DataRow(TextFileEncoding.Utf8, "E6BCA220F09F9880")]
    [DataRow(TextFileEncoding.Utf8Bom, "EFBBBFE6BCA220F09F9880")]
    [DataRow(TextFileEncoding.Utf16LE, "FFFE226F20003DD800DE")]
    [DataRow(TextFileEncoding.Utf16BE, "FEFF6F220020D83DDE00")]
    public void UnicodeContentIncludingSurrogatePairsSurvivesSaveAs(TextFileEncoding encoding, string hex)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("unicode.txt");
        var service = new NoteContentService(directory.Path);

        service.SaveAs(path, "漢 😀", encoding);

        CollectionAssert.AreEqual(Convert.FromHexString(hex), File.ReadAllBytes(path));
        Assert.AreEqual("漢 😀", service.Load(path));
    }

    [TestMethod]
    [DataRow("漢")]
    [DataRow("😀")]
    [DataRow("ā")]
    public void UnrepresentableWindows1252TextNeverChangesTheDestination(string text)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        var original = Convert.FromHexString("EFBBBF73616665");
        File.WriteAllBytes(path, original);
        var service = new NoteContentService(directory.Path);

        var error = Assert.ThrowsExactly<TextEncodingException>(() =>
            service.Save(path, text, TextFileEncoding.Windows1252));
        StringAssert.Contains(error.Message, "Windows-1252");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsFalse(Directory.Exists(directory.File("note-history")));
        Assert.ThrowsExactly<TextEncodingException>(() =>
            service.SaveAs(path, text, TextFileEncoding.Windows1252));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        var newPath = directory.File("new.txt");
        Assert.ThrowsExactly<TextEncodingException>(() =>
            service.SaveAs(newPath, text, TextFileEncoding.Windows1252));
        Assert.IsFalse(File.Exists(newPath));
    }

    [TestMethod]
    [DataRow("EFBBBFC3")]
    [DataRow("FFFE00")]
    [DataRow("FEFFD800")]
    [DataRow("FFFE000000001100")]
    public void InvalidBomMarkedUnicodeIsRejectedWithoutChangingBytes(string hex)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("invalid.txt");
        var bytes = Convert.FromHexString(hex);
        File.WriteAllBytes(path, bytes);
        var service = new NoteContentService(directory.Path);

        Assert.ThrowsExactly<TextEncodingException>(() => service.Load(path));
        Assert.ThrowsExactly<TextEncodingException>(() => service.Save(path, "replacement"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void InvalidUnicodeInEditorCannotBeSavedWithReplacementCharacters()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("invalid.txt");
        var service = new NoteContentService(directory.Path);

        Assert.ThrowsExactly<TextEncodingException>(() => service.SaveAs(path, "\uD800"));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void NewDocumentsAndSaveAsDefaultToUtf8WithoutBom()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("new.txt");
        var document = new OpenNoteDocument(path, "é", string.Empty, isDirty: true);
        var service = new NoteContentService(directory.Path);

        Assert.AreEqual(TextFileEncoding.Utf8, document.Encoding);
        Assert.AreEqual(TextFileEncoding.Utf8, document.SavedEncoding);
        service.SaveAs(path, document.Content);
        CollectionAssert.AreEqual(Convert.FromHexString("C3A9"), File.ReadAllBytes(path));
    }

    [TestMethod]
    public void NoteCreationKeepsTheExistingUtf8WithoutBomDefault()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        var created = files.CreateNote("new note").Note;
        Assert.IsNotNull(created);
        var path = Path.Combine(files.CurrentDirectory, created.FileName);
        var service = new NoteContentService(directory.Path);

        Assert.AreEqual(TextFileEncoding.Utf8, service.LoadWithEncoding(path).Encoding);
        Assert.AreEqual(created.InitialContent, service.Load(path));
        Assert.IsFalse(File.ReadAllBytes(path).AsSpan().StartsWith(Convert.FromHexString("EFBBBF")));
    }

    [TestMethod]
    public void SaveAsHonorsSelectedEncodingWhenOverwritingAnotherEncoding()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("destination.txt");
        File.WriteAllBytes(path, Convert.FromHexString("FFFE6F006C006400"));
        var service = new NoteContentService(directory.Path);

        service.SaveAs(path, "é €", TextFileEncoding.Windows1252);

        CollectionAssert.AreEqual(Convert.FromHexString("E92080"), File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Utf8BomCanBeRemovedAndAddedWithoutChangingText()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("bom.txt");
        File.WriteAllBytes(path, Convert.FromHexString("EFBBBFC3A9"));
        var service = new NoteContentService(directory.Path);

        service.Save(path, "é", TextFileEncoding.Utf8);
        CollectionAssert.AreEqual(Convert.FromHexString("C3A9"), File.ReadAllBytes(path));
        service.Save(path, "é", TextFileEncoding.Utf8Bom);
        CollectionAssert.AreEqual(Convert.FromHexString("EFBBBFC3A9"), File.ReadAllBytes(path));
    }

    [TestMethod]
    public void FirstSaveAsUsesSelectedEncodingAndSafelyRemovesInitialFile()
    {
        using var directory = new TemporaryTestDirectory();
        var initialPath = directory.File("initial.txt");
        var destination = directory.File("converted.txt");
        File.WriteAllText(initialPath, "initial");
        var service = new NoteContentService(directory.Path);

        service.SaveNewNoteAs(initialPath, destination, "é €", "initial", TextFileEncoding.Utf16BE);

        Assert.IsFalse(File.Exists(initialPath));
        CollectionAssert.AreEqual(Convert.FromHexString("FEFF00E9002020AC"), File.ReadAllBytes(destination));
    }

    [TestMethod]
    public void FailedFirstSaveAsConversionKeepsBothFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var initialPath = directory.File("initial.txt");
        var destination = directory.File("destination.txt");
        File.WriteAllText(initialPath, "initial");
        File.WriteAllText(destination, "destination");
        var service = new NoteContentService(directory.Path);

        Assert.ThrowsExactly<TextEncodingException>(() => service.SaveNewNoteAs(
            initialPath, destination, "漢", "initial", TextFileEncoding.Windows1252));

        Assert.AreEqual("initial", File.ReadAllText(initialPath));
        Assert.AreEqual("destination", File.ReadAllText(destination));
    }

    [TestMethod]
    public void SaveVerificationUsesSelectedBytesForAmbiguousWindows1252Text()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("ambiguous.txt");
        var service = new NoteContentService(directory.Path);

        service.SaveAs(path, "Ã©", TextFileEncoding.Windows1252);

        CollectionAssert.AreEqual(Convert.FromHexString("C3A9"), File.ReadAllBytes(path));
    }
}
