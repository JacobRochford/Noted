using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupPersistenceContractTests
{
    // use fixed saved values so changes to production definitions can't hide format changes.
    private sealed record HistoricalFile(string Path, string Content, string Category, int? Count, long? ContentLength);
    private static readonly HistoricalFile[] s_files =
    [
        new("settings.json", "{\"SchemaVersion\":1,\"Revision\":7}", "Settings", null, null),
        new("checklist.json", "[{},{}]", "Checklist", 2, null),
        new("checklist-tabs.json", "[{}]", "Checklist", 1, null),
        new("dictionary.json", "[{}]", "Dictionary", 1, null),
        new("jots.json", "[{},{},{}]", "Jots", 3, null),
        new("scratchpad.rtf", "{\\rtf1 draft}", "Scratchpad", null, 13),
        new("session/editor-workspace.json", "{\"Tabs\":[]}", "Open notes", null, null)
    ];

    [TestMethod]
    public void FixedFilesKeepHistoricalPathsCategoriesMetadataAndProtectionFlags()
    {
        using var directory = new TemporaryTestDirectory();
        WriteSource(directory);
        var entries = new BackupContentCatalog(directory.File("app"), directory.File("notes"), directory.File("app", "full-snapshots")).Collect();
        Assert.HasCount(7, entries);
        Assert.HasCount(7, BackupContentDefinitions.FixedFiles);
        foreach (var historical in s_files)
        {
            var logicalPath = "app/" + historical.Path;
            var entry = entries.Single(x => x.LogicalPath == logicalPath);
            Assert.AreEqual(Path.GetFullPath(directory.File("app", historical.Path)), entry.SourcePath);
            Assert.AreEqual(historical.Count, entry.ItemCount);
            Assert.AreEqual(historical.ContentLength, entry.ContentLength);
            Assert.AreEqual(historical.Category, BackupFormat.GetFileCategory(logicalPath.ToUpperInvariant()));
            var definition = BackupContentDefinitions.Find(logicalPath)!;
            Assert.AreEqual(historical.Path != "session/editor-workspace.json", definition.ProtectMissing);
            Assert.AreEqual(historical.Count.HasValue, definition.ProtectEmptyCollection);
        }
        Assert.AreEqual("Application data", BackupFormat.GetFileCategory("app/future-file.json"));
    }

    [TestMethod]
    [DataRow("settings.json", true)]
    [DataRow("checklist.json", true)]
    [DataRow("checklist-tabs.json", true)]
    [DataRow("dictionary.json", true)]
    [DataRow("jots.json", true)]
    [DataRow("scratchpad.rtf", true)]
    [DataRow("session/editor-workspace.json", false)]
    public void MissingFixedFileRetainsHistoricalProtection(string file, bool protectedFile)
    {
        using var directory = new TemporaryTestDirectory();
        WriteSource(directory);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        File.Delete(directory.File("app", file));
        var result = new FullBackupService(directory.File("app"), directory.File("notes")).CreateRecentBackup();
        Assert.AreEqual(protectedFile ? FullBackupStatus.Blocked : FullBackupStatus.Created, result.Status, result.Message);
    }

    [TestMethod]
    [DataRow("checklist.json")]
    [DataRow("checklist-tabs.json")]
    [DataRow("dictionary.json")]
    [DataRow("jots.json")]
    public void EmptyCollectionsRetainHistoricalProtection(string file)
    {
        using var directory = new TemporaryTestDirectory();
        WriteSource(directory);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(directory.File("app", file), "[]");
        Assert.AreEqual(FullBackupStatus.Blocked,
            new FullBackupService(directory.File("app"), directory.File("notes")).CreateRecentBackup().Status);
    }

    [TestMethod]
    [DataRow("Protected", 0)]
    [DataRow("Latest", 1)]
    [DataRow("BeforeRestore", 2)]
    public void HistoricalBackupEnumRepresentationsRoundTrip(string value, int numericValue)
    {
        var parsed = JsonSerializer.Deserialize<FullBackupType>('"' + value + '"', BackupFormat.JsonOptions);
        Assert.AreEqual(numericValue, (int)parsed);
        Assert.AreEqual('"' + value + '"', JsonSerializer.Serialize(parsed, BackupFormat.JsonOptions));
    }

    private static void WriteSource(TemporaryTestDirectory directory)
    {
        Directory.CreateDirectory(directory.File("notes"));
        foreach (var file in s_files)
        {
            var path = directory.File("app", file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }
        File.WriteAllText(directory.File("app", "jot-capture-draft.json"), "[{\"Text\":\"excluded\"}]");
    }
}
