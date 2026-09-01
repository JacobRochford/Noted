using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class AppSettingsServiceTests
{
    [TestMethod]
    public void FirstSaveCreatesSettingsFileThatCanBeLoaded()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        service.SaveCustomHeader("first");

        Assert.IsTrue(File.Exists(directory.File("settings.json")));
        Assert.AreEqual("first", CreateService(directory.Path).LoadCustomHeader());
    }

    [TestMethod]
    public void ReplacementPreservesPreviousSettingsInRollingBackup()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveCustomHeader("first");
        var previousSettings = File.ReadAllText(directory.File("settings.json"));

        service.SaveCustomHeader("second");

        Assert.AreEqual(previousSettings, File.ReadAllText(directory.File("settings.json.bak")));
        Assert.AreEqual("second", CreateService(directory.Path).LoadCustomHeader());
    }

    [TestMethod]
    public void DuplicateSaveDoesNotRotateBackupOrCreateAnotherHistoryFile()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveCustomHeader("same");

        service.SaveCustomHeader("same");

        Assert.IsFalse(File.Exists(directory.File("settings.json.bak")));
        Assert.AreEqual(1, SettingsHistoryFiles(directory.Path).Length);
    }

    [TestMethod]
    public void RenamedSettingsKeepTheirExistingJsonNames()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveNotesHotkey("Alt", "N");
        service.SaveMainHideButtonHidesAll(false);
        service.SaveReopenEditorTabsOnStartup(false);

        using (var settings = JsonDocument.Parse(
                   File.ReadAllText(directory.File("settings.json"))))
        {
            var root = settings.RootElement;
            Assert.AreEqual("Alt", root.GetProperty("HotkeyModifiers").GetString());
            Assert.AreEqual("N", root.GetProperty("HotkeyKey").GetString());
            Assert.IsFalse(root.GetProperty("HideButtonClosesAll").GetBoolean());
            Assert.IsFalse(root.GetProperty("RestoreEditorSession").GetBoolean());
            Assert.IsFalse(root.TryGetProperty("NotesHotkeyModifiers", out _));
            Assert.IsFalse(root.TryGetProperty("MainHideButtonHidesAll", out _));
            Assert.IsFalse(root.TryGetProperty("ReopenEditorTabsOnStartup", out _));
        }

        var reloaded = CreateService(directory.Path);
        Assert.AreEqual(("Alt", "N"), reloaded.LoadNotesHotkey());
        Assert.IsFalse(reloaded.LoadMainHideButtonHidesAll());
        Assert.IsFalse(reloaded.LoadReopenEditorTabsOnStartup());
    }

    [TestMethod]
    public void RecoverableOutOfRangeSettingIsNormalizedWithoutRollingBackSettings()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveCustomHeader("valid");
        service.SaveDefaultOpacity(2);

        var reloaded = CreateService(directory.Path);
        Assert.AreEqual("valid", reloaded.LoadCustomHeader());
        Assert.AreEqual(1, reloaded.LoadDefaultOpacity());
        Assert.IsNull(reloaded.RecoveryNotice);
    }

    [TestMethod]
    public void ValidSettingsFileWinsOverNewerRecoveryFiles()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("settings.json"), ValidJson("current settings"));
        File.WriteAllText(directory.File("settings.json.bak.pending-newer"), ValidJson("pending"));
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("backup"));

        var service = CreateService(directory.Path);

        Assert.AreEqual("current settings", service.LoadCustomHeader());
        Assert.IsNull(service.RecoveryNotice);
    }

    [TestMethod]
    public void MissingSettingsFileUsesNewestPendingRecoveryFileAcrossRecoveryTypes()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("backups"));
        var pending = directory.File("settings.json.bak.pending-good");
        var rolling = directory.File("settings.json.bak");
        var legacy = directory.File("settings.previous.json");
        var historyFile = directory.File("backups", "settings_20260101_000000_000.json");
        File.WriteAllText(pending, ValidJson("pending"));
        File.WriteAllText(rolling, ValidJson("rolling"));
        File.WriteAllText(legacy, ValidJson("legacy"));
        File.WriteAllText(historyFile, ValidJson("history"));
        SetUtcWriteTime(historyFile, 1);
        SetUtcWriteTime(legacy, 2);
        SetUtcWriteTime(rolling, 3);
        SetUtcWriteTime(pending, 4);
        var pendingOriginal = File.ReadAllText(pending);

        var service = CreateService(directory.Path);

        Assert.AreEqual("pending", service.LoadCustomHeader());
        Assert.AreEqual(pendingOriginal, File.ReadAllText(pending));
        StringAssert.Contains(service.RecoveryNotice, "settings.json.bak.pending-good");
    }

    [TestMethod]
    public void MissingSettingsFileUsesRollingBeforeLegacyAndHistory()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("backups"));
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("rolling"));
        File.WriteAllText(directory.File("settings.previous.json"), ValidJson("legacy"));
        File.WriteAllText(
            directory.File("backups", "settings_20260101_000000_000.json"),
            ValidJson("history"));
        SetUtcWriteTime(directory.File("backups", "settings_20260101_000000_000.json"), 1);
        SetUtcWriteTime(directory.File("settings.previous.json"), 2);
        SetUtcWriteTime(directory.File("settings.json.bak"), 3);

        Assert.AreEqual("rolling", CreateService(directory.Path).LoadCustomHeader());
    }

    [TestMethod]
    public void HighestRevisionWinsWhenItsFileTimestampIsOlder()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("backups"));
        var rolling = directory.File("settings.json.bak");
        var historyFile = directory.File("backups", "settings_20260101_000000_000.json");
        File.WriteAllText(rolling, VersionedJson("older state", revision: 4));
        File.WriteAllText(historyFile, VersionedJson("newer state", revision: 5));
        SetUtcWriteTime(historyFile, 1);
        SetUtcWriteTime(rolling, 2);

        var service = CreateService(directory.Path);

        Assert.AreEqual("newer state", service.LoadCustomHeader());
        StringAssert.Contains(service.RecoveryNotice, Path.GetFileName(historyFile));
    }

    [TestMethod]
    public void ConflictingFilesAtHighestRevisionBlockAutomaticRecovery()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("backups"));
        File.WriteAllText(directory.File("settings.json"), "{broken");
        File.WriteAllText(
            directory.File("settings.json.bak"),
            VersionedJson("rolling", revision: 5));
        File.WriteAllText(
            directory.File("backups", "settings_20260101_000000_000.json"),
            VersionedJson("history", revision: 5));
        var before = CaptureTree(directory.Path);

        AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());

        AssertTreeUnchanged(directory.Path, before);
    }

    [TestMethod]
    public void NewerSettingsSchemaBlocksAutomaticDowngrade()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.File("settings.json"),
            "{\"SchemaVersion\":2,\"Revision\":10,\"CustomHeader\":\"future\"}");
        File.WriteAllText(
            directory.File("settings.json.bak"),
            VersionedJson("older supported state", revision: 9));
        var before = CaptureTree(directory.Path);

        AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());

        AssertTreeUnchanged(directory.Path, before);
    }

    [TestMethod]
    public void InvalidSettingsFileRecoversFromNewestValidHistoryWithoutChangingFiles()
    {
        using var directory = new TestDirectory();
        var historyDirectory = directory.File("backups");
        Directory.CreateDirectory(historyDirectory);
        var settingsFilePath = directory.File("settings.json");
        var pending = directory.File("settings.json.bak.pending-invalid");
        var rolling = directory.File("settings.json.bak");
        var legacy = directory.File("settings.previous.json");
        var newestHistoryFile = directory.File("backups", "settings_20260105_000000_000.json");
        var validHistoryFile = directory.File("backups", "settings_20260104_000000_000.json");
        var olderHistoryFile = directory.File("backups", "settings_20260101_000000_000.json");
        File.WriteAllText(settingsFilePath, "{settings broken");
        File.WriteAllText(pending, "{pending broken");
        File.WriteAllText(rolling, ValidJson("older rolling"));
        File.WriteAllText(legacy, ValidJson("older legacy"));
        File.WriteAllText(newestHistoryFile, "{history broken");
        File.WriteAllText(validHistoryFile, ValidJson("newest valid history"));
        File.WriteAllText(olderHistoryFile, ValidJson("older history"));
        SetUtcWriteTime(olderHistoryFile, 1);
        SetUtcWriteTime(legacy, 2);
        SetUtcWriteTime(rolling, 3);
        SetUtcWriteTime(validHistoryFile, 4);
        SetUtcWriteTime(newestHistoryFile, 5);
        SetUtcWriteTime(pending, 6);
        var recoveryFileInventory = CaptureInventory(
            pending,
            rolling,
            legacy,
            newestHistoryFile,
            validHistoryFile,
            olderHistoryFile);

        var service = CreateService(directory.Path);

        Assert.AreEqual("newest valid history", service.LoadCustomHeader());
        StringAssert.Contains(service.RecoveryNotice, Path.GetFileName(validHistoryFile));
        AssertInventoryUnchanged(recoveryFileInventory);
        var preserved = Directory.GetFiles(directory.Path, "settings.corrupt_*.json");
        Assert.HasCount(1, preserved);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("{settings broken"),
            File.ReadAllBytes(preserved[0]));
    }

    [TestMethod]
    public void InvalidNewestPendingRecoveryFileIsSkippedForOlderValidPendingRecoveryFile()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{broken");
        var older = directory.File("settings.json.bak.pending-older");
        var newer = directory.File("settings.json.bak.pending-newer");
        File.WriteAllText(older, ValidJson("older valid"));
        File.WriteAllText(newer, "{also broken");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var service = CreateService(directory.Path);

        Assert.AreEqual("older valid", service.LoadCustomHeader());
        StringAssert.Contains(service.RecoveryNotice, "settings.json.bak.pending-older");
    }

    [TestMethod]
    public void InvalidSettingsFileIsPreservedExactlyAndRecoveryFileIsUnchanged()
    {
        using var directory = new TestDirectory();
        const string corruptJson = "{\"CustomHeader\":\"damaged\"";
        var recoveryFile = directory.File("settings.json.bak");
        var recoveryJson = ValidJson("recovered");
        File.WriteAllText(directory.File("settings.json"), corruptJson, Encoding.UTF8);
        File.WriteAllText(recoveryFile, recoveryJson, Encoding.UTF8);
        var corruptBytes = File.ReadAllBytes(directory.File("settings.json"));
        var recoveryBytes = File.ReadAllBytes(recoveryFile);

        var service = CreateService(directory.Path);

        Assert.AreEqual("recovered", service.LoadCustomHeader());
        CollectionAssert.AreEqual(recoveryBytes, File.ReadAllBytes(recoveryFile));
        var preserved = Directory.GetFiles(directory.Path, "settings.corrupt_*.json");
        Assert.HasCount(1, preserved);
        CollectionAssert.AreEqual(corruptBytes, File.ReadAllBytes(preserved[0]));
    }

    [TestMethod]
    public void MissingSettingsFileWithoutRecoveryFilesReturnsDefaultsWithoutCreatingAFile()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        Assert.IsNull(service.LoadCustomHeader());
        Assert.IsFalse(File.Exists(directory.File("settings.json")));
    }

    [TestMethod]
    public void UnreadableSettingsHistoryBlocksRecoveryWithoutChangingFiles()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{settings broken");
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("would recover"));
        File.WriteAllText(directory.File("backups"), "not a readable history directory");
        var before = CaptureTree(directory.Path);

        AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());

        AssertTreeUnchanged(directory.Path, before);
    }

    [TestMethod]
    public void InvalidRecoveryFileDoesNotCreateOrReplaceSettingsFile()
    {
        using var directory = new TestDirectory();
        var recoveryFile = directory.File("settings.json.bak");
        File.WriteAllText(recoveryFile, "{broken");
        var originalRecoveryFile = File.ReadAllText(recoveryFile);

        AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());

        Assert.IsFalse(File.Exists(directory.File("settings.json")));
        Assert.AreEqual(originalRecoveryFile, File.ReadAllText(recoveryFile));
    }

    [TestMethod]
    public void RepeatedFailedStartupsNeverChangeInvalidRecoveryFiles()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("backups"));
        File.WriteAllText(directory.File("settings.json"), "{settings broken");
        File.WriteAllText(directory.File("settings.json.bak.pending-invalid"), "{pending broken");
        File.WriteAllText(directory.File("settings.json.bak"), "{rolling broken");
        File.WriteAllText(directory.File("settings.previous.json"), "{legacy broken");
        File.WriteAllText(
            directory.File("backups", "settings_20260101_000000_000.json"),
            "{history broken");
        var before = CaptureTree(directory.Path);

        for (var attempt = 0; attempt < 3; attempt++)
            AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());

        AssertTreeUnchanged(directory.Path, before);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "settings.corrupt_*.json"));
    }

    [TestMethod]
    public void SuccessfulRecoveryIsIdempotentOnNextStartup()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{broken");
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("restored"));
        var first = CreateService(directory.Path);
        Assert.AreEqual("restored", first.LoadCustomHeader());
        var preservedCount = Directory.GetFiles(directory.Path, "settings.corrupt_*.json").Length;

        var second = CreateService(directory.Path);

        Assert.AreEqual("restored", second.LoadCustomHeader());
        Assert.IsNull(second.RecoveryNotice);
        Assert.AreEqual(
            preservedCount,
            Directory.GetFiles(directory.Path, "settings.corrupt_*.json").Length);
    }

    [TestMethod]
    public void SettingsHistoryIsLimitedByTime()
    {
        using var directory = new TestDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock, TimeSpan.FromHours(6));
        service.SaveCustomHeader("one");
        clock.Advance(TimeSpan.FromHours(1));
        service.SaveCustomHeader("two");
        Assert.AreEqual(1, SettingsHistoryFiles(directory.Path).Length);

        clock.Advance(TimeSpan.FromHours(6));
        service.SaveCustomHeader("three");

        Assert.AreEqual(2, SettingsHistoryFiles(directory.Path).Length);
    }

    [TestMethod]
    public void SettingsHistoryRecordsAStateThatReturnsAtANewerRevision()
    {
        using var directory = new TestDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock, TimeSpan.FromHours(6));
        service.SaveCustomHeader("one");
        clock.Advance(TimeSpan.FromHours(1));
        service.SaveCustomHeader("two");
        clock.Advance(TimeSpan.FromHours(6));

        service.SaveCustomHeader("one");

        Assert.AreEqual(2, SettingsHistoryFiles(directory.Path).Length);
        CollectionAssert.AreEquivalent(
            new[] { "one", "one" },
            SettingsHistoryFiles(directory.Path)
                .Select(ReadCustomHeader)
                .ToArray());
    }

    [TestMethod]
    public void SettingsHistoryKeepsConfiguredNumberOfFiles()
    {
        using var directory = new TestDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock, TimeSpan.Zero, maxSettingsHistoryFiles: 3);
        for (var i = 0; i < 5; i++)
        {
            service.SaveCustomHeader($"value {i}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(3, SettingsHistoryFiles(directory.Path).Length);
        CollectionAssert.AreEquivalent(
            new[] { "value 2", "value 3", "value 4" },
            SettingsHistoryFiles(directory.Path)
                .Select(ReadCustomHeader)
                .ToArray());
    }

    [TestMethod]
    public void SettingsHistoryFailureWarnsWithoutFailingSettingsSave()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveCustomHeader("before");
        Directory.Delete(directory.File("backups"), recursive: true);
        File.WriteAllText(directory.File("backups"), "blocks the directory");
        string? warning = null;
        service.PersistenceWarning += message => warning = message;

        service.SaveCustomHeader("saved");

        Assert.AreEqual("saved", CreateService(directory.Path).LoadCustomHeader());
        StringAssert.Contains(File.ReadAllText(directory.File("settings.json.bak")), "before");
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "settings history");
        Assert.AreEqual(warning, service.LastPersistenceWarning);
    }

    [TestMethod]
    public void ThrowingWarningSubscriberCannotTurnSuccessfulSaveIntoFailure()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);
        service.SaveCustomHeader("before");
        Directory.Delete(directory.File("backups"), recursive: true);
        File.WriteAllText(directory.File("backups"), "blocks the directory");
        service.PersistenceWarning += _ => throw new InvalidOperationException("dialog failed");

        service.SaveCustomHeader("saved despite subscriber");

        Assert.AreEqual(
            "saved despite subscriber",
            CreateService(directory.Path).LoadCustomHeader());
        Assert.IsNotNull(service.LastPersistenceWarning);
    }

    [TestMethod]
    public void LockedRollingBackupLeavesSettingsFileSavedAndPreviousSettingsPending()
    {
        using var directory = new TestDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock, TimeSpan.FromHours(6));
        service.SaveCustomHeader("one");
        service.SaveCustomHeader("two");
        var rollingPath = directory.File("settings.json.bak");
        var rollingOriginal = File.ReadAllText(rollingPath);
        string? warning = null;
        service.PersistenceWarning += message => warning = message;

        using (new FileStream(rollingPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.SaveCustomHeader("three");
        }

        Assert.AreEqual("three", CreateService(directory.Path).LoadCustomHeader());
        Assert.AreEqual(rollingOriginal, File.ReadAllText(rollingPath));
        var pending = Directory.GetFiles(directory.Path, "settings.json.bak.pending-*");
        Assert.HasCount(1, pending);
        StringAssert.Contains(File.ReadAllText(pending[0]), "two");
        Assert.IsNotNull(warning);
    }

    [TestMethod]
    public void LockedSettingsFileBlocksUpdateWithoutChangingFilesAndLaterRecovers()
    {
        using var directory = new TestDirectory();
        var settingsFilePath = directory.File("settings.json");
        var backupPath = directory.File("settings.json.bak");
        File.WriteAllText(settingsFilePath, "{broken");
        File.WriteAllText(backupPath, ValidJson("backup"));
        var before = CaptureTree(directory.Path);

        using (new FileStream(settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());
            AssertSettingsFailure(() => CreateService(directory.Path).LoadCustomHeader());
            AssertTreeUnchanged(directory.Path, before);
            Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
            Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.pending-*", SearchOption.AllDirectories));
            Assert.HasCount(0, Directory.GetFiles(directory.Path, "settings.corrupt_*.json"));
        }

        Assert.AreEqual("backup", CreateService(directory.Path).LoadCustomHeader());
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "settings.corrupt_*.json"));
    }

    [TestMethod]
    public void RecoverableTypedValuesAreNormalizedWithoutRollingBackSettings()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.File("settings.json"),
            "{\"CustomHeader\":\"newer\",\"DefaultOpacity\":2,\"NewNoteMode\":999,\"TimestampPlacement\":999,\"FolderNavigationMode\":999}");
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("valid backup"));

        var service = CreateService(directory.Path);

        Assert.AreEqual("newer", service.LoadCustomHeader());
        Assert.AreEqual(1, service.LoadDefaultOpacity());
        Assert.IsNull(service.RecoveryNotice);
    }

    [TestMethod]
    public void StructurallyInvalidSettingsFileRecoversFromValidRecoveryFile()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{\"DefaultOpacity\":\"opaque\"}");
        File.WriteAllText(directory.File("settings.json.bak"), ValidJson("valid backup"));

        Assert.AreEqual("valid backup", CreateService(directory.Path).LoadCustomHeader());
    }

    [TestMethod]
    public void WindowVisibilitySettingsPersistIndependentlyFromWindowContent()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        service.SaveNoteEditorWindowState(new NoteEditorWindowState
        {
            Width = 900,
            Height = 600,
            ReopenOnStartup = false
        });
        service.SaveMiniPadWindowState(new MiniPadWindowState
        {
            ReopenOnStartup = true
        });

        var reloaded = CreateService(directory.Path);
        var editorState = reloaded.LoadNoteEditorWindowState();
        var miniPadState = reloaded.LoadMiniPadWindowState();

        Assert.AreEqual(900, editorState.Width);
        Assert.AreEqual(600, editorState.Height);
        Assert.IsFalse(editorState.ReopenOnStartup);
        Assert.IsTrue(miniPadState.ReopenOnStartup);
    }

    [TestMethod]
    public void ChecklistPriorityColorsPersistWithWindowSettings()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        service.SaveChecklistWindowState(new ChecklistWindowState
        {
            PriorityColors = new ChecklistPriorityColors
            {
                HighColor = "#800000",
                MediumColor = "#FFD700",
                LowColor = "#008080"
            }
        });

        var colors = CreateService(directory.Path)
            .LoadChecklistWindowState()
            .PriorityColors;

        Assert.AreEqual("#800000", colors.HighColor);
        Assert.AreEqual("#FFD700", colors.MediumColor);
        Assert.AreEqual("#008080", colors.LowColor);
    }

    [TestMethod]
    public void ThemeSettingsUseTheNotedDefaults()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        Assert.AreEqual(AppThemeMode.System, service.LoadAppThemeMode());
        Assert.AreEqual(AppTheme.DefaultAccentColor, service.LoadAccentColor());
    }

    [TestMethod]
    public void ThemeSettingsPersistNormalizedAccentColor()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        service.SaveAppThemeMode(AppThemeMode.Dark);
        service.SaveAccentColor("3366aa");

        var reloaded = CreateService(directory.Path);
        Assert.AreEqual(AppThemeMode.Dark, reloaded.LoadAppThemeMode());
        Assert.AreEqual("#3366AA", reloaded.LoadAccentColor());
    }

    [TestMethod]
    public void InvalidStoredThemeSettingsUseTheNotedDefaults()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.File("settings.json"),
            "{\"NewNoteMode\":0,\"AppThemeMode\":99,\"AccentColor\":\"not-a-color\"}");

        var service = CreateService(directory.Path);

        Assert.AreEqual(AppThemeMode.System, service.LoadAppThemeMode());
        Assert.AreEqual(AppTheme.DefaultAccentColor, service.LoadAccentColor());
        Assert.IsNull(service.RecoveryNotice);
    }

    [TestMethod]
    public void InvalidAccentColorIsNotSaved()
    {
        using var directory = new TestDirectory();
        var service = CreateService(directory.Path);

        Assert.ThrowsExactly<ArgumentException>(() => service.SaveAccentColor("blue"));
        Assert.IsFalse(File.Exists(directory.File("settings.json")));
    }

    private static AppSettingsService CreateService(
        string path,
        TimeProvider? clock = null,
        TimeSpan? settingsHistoryInterval = null,
        int maxSettingsHistoryFiles = 10) =>
        new(
            path,
            clock ?? new ManualTimeProvider(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            settingsHistoryInterval ?? TimeSpan.FromHours(6),
            maxSettingsHistoryFiles);

    private static string ValidJson(string header) =>
        $$"""
        {
          "NewNoteMode": 0,
          "CustomHeader": {{System.Text.Json.JsonSerializer.Serialize(header)}}
        }
        """;

    private static string VersionedJson(string header, long revision) =>
        JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Revision = revision,
            SavedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMinutes(revision),
            NewNoteMode = 0,
            CustomHeader = header
        });

    private static string[] SettingsHistoryFiles(string root) =>
        Directory.Exists(System.IO.Path.Combine(root, "backups"))
            ? Directory.GetFiles(System.IO.Path.Combine(root, "backups"), "settings_*.json")
            : [];

    private static string ReadCustomHeader(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("CustomHeader").GetString()!;
    }

    private static void SetUtcWriteTime(string path, int day) =>
        File.SetLastWriteTimeUtc(
            path,
            new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    private static IReadOnlyDictionary<string, FileFingerprint> CaptureInventory(
        params string[] paths) =>
        paths.ToDictionary(
            path => path,
            CreateFingerprint,
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, FileFingerprint> CaptureTree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => System.IO.Path.GetRelativePath(root, path),
                CreateFingerprint,
                StringComparer.OrdinalIgnoreCase);

    private static FileFingerprint CreateFingerprint(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new FileFingerprint(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static void AssertInventoryUnchanged(
        IReadOnlyDictionary<string, FileFingerprint> expected)
    {
        foreach (var item in expected)
        {
            Assert.IsTrue(File.Exists(item.Key), $"Recovery file disappeared: {item.Key}");
            Assert.AreEqual(item.Value, CreateFingerprint(item.Key), item.Key);
        }
    }

    private static void AssertTreeUnchanged(
        string root,
        IReadOnlyDictionary<string, FileFingerprint> expected)
    {
        var actual = CaptureTree(root);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var item in expected)
            Assert.AreEqual(item.Value, actual[item.Key], item.Key);
    }

    private static void AssertSettingsFailure(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected a SettingsPersistenceException.");
        }
        catch (SettingsPersistenceException)
        {
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed record FileFingerprint(long Length, string Sha256);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Noted.SettingsTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(params string[] parts) =>
            parts.Aggregate(Path, System.IO.Path.Combine);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
