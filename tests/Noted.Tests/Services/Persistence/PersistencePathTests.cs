using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class PersistencePathTests
{
    // keep expected hashes fixed so changes to the Windows path algorithm are caught.
    [TestMethod]
    [DataRow(@"C:\Notes\Mixed Case.md", "CCB96E09CDCD18D752F569F08A5F18E4683A28CDC925308AFB65DEF8CD9D383C")]
    [DataRow(@"c:\notes\mixed case.md", "CCB96E09CDCD18D752F569F08A5F18E4683A28CDC925308AFB65DEF8CD9D383C")]
    [DataRow(@"C:\Notes\..\Notes\Mixed Case.md", "CCB96E09CDCD18D752F569F08A5F18E4683A28CDC925308AFB65DEF8CD9D383C")]
    [DataRow(@"C:\Notes\Résumé 東京.txt", "86A2F108A976335F24A867A5BF876F7EC8171440D9160C83197C35A62FA2940C")]
    [DataRow(@"\\server\share\Notes\Todo.md", "38CBE5C2BAC5AC6638BE326A6F20E1287477E7147D288550F333B94D1AE22581")]
    public void HistoricalNotePathsKeepTheirHashAndRecoveryFilename(string path, string historicalHash)
    {
        Assert.AreEqual(historicalHash, NotePathIdentity.GetHash(path));
        using var directory = new TemporaryTestDirectory();
        new NoteRecoveryService(directory.Path).SaveDraft(path, "historical content");
        var savedPath = directory.File("recovery", historicalHash + ".json");
        Assert.IsTrue(File.Exists(savedPath));
        Assert.AreEqual("historical content", new NoteRecoveryService(directory.Path).LoadDraft(path).Draft?.Content);
        Assert.AreEqual(18L, BackupContentCatalog.ValidateNoteDraft(File.ReadAllBytes(savedPath), savedPath).ContentLength);
    }

    [TestMethod]
    public void HistoricalRecoveryFixtureAndCorruptEvidenceStayInOriginalDirectory()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("recovery"));
        var primary = directory.File("recovery", "CCB96E09CDCD18D752F569F08A5F18E4683A28CDC925308AFB65DEF8CD9D383C.json");
        File.WriteAllText(primary, "broken JSON");
        File.WriteAllText(primary + ".bak", """
            {"FilePath":"C:\\Notes\\Mixed Case.md","Content":"saved draft","UpdatedUtc":"2026-09-01T12:00:00+00:00"}
            """);
        var result = new NoteRecoveryService(directory.Path).LoadDraft(@"C:\Notes\Mixed Case.md");
        Assert.AreEqual("saved draft", result.Draft?.Content);
        Assert.IsTrue(Directory.EnumerateFiles(directory.File("recovery"), "*.corrupt*").Any());
        Assert.IsTrue(File.ReadAllText(primary + ".bak").Contains("saved draft", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ServiceWritesKeepHistoricalStorageLayout()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveShowNotesHeader(true);
        settings.SaveShowNotesHeader(false);
        new ChecklistContentService(directory.Path).SaveItems([]);
        new ChecklistContentService(directory.Path).SaveTabs([]);
        new DictionaryContentService(directory.Path).SaveItems([]);
        new JotsContentService(directory.Path).SaveItems([]);
        new JotCaptureDraftService(directory.Path).Save(new JotCaptureDraft());
        new ScratchpadContentService(directory.Path).SaveContent("{\\rtf1 draft}");
        new NoteEditorSessionService(directory.Path).Save(new NoteEditorSession());
        foreach (var file in new[] { "settings.json", "settings.json.bak", "checklist.json", "checklist-tabs.json",
                     "dictionary.json", "jots.json", "jot-capture-draft.json", "scratchpad.rtf", "session/editor-workspace.json" })
            Assert.IsTrue(File.Exists(directory.File(file)), file);
        Assert.IsTrue(Directory.Exists(directory.File("backups")));
        Assert.AreEqual(directory.File("recovery", "minipads"), MiniPadStorage.GetRecoveryDirectory(directory.Path));
        Assert.AreEqual(directory.File("recovery", "micro-scratchpads"), MiniPadStorage.GetLegacyRecoveryDirectory(directory.Path));
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noted"),
            PersistencePaths.DefaultAppDataDirectory);
    }
}
