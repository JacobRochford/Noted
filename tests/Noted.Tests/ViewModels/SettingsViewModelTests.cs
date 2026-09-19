using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public void ReloadReadsSettingsWithoutSavingOrInvokingIntegrationActions()
    {
        using var context = new Context();
        context.Settings.SaveNewNoteMode(NewNoteMode.Both);
        context.Settings.SaveFolderNavigationMode(FolderNavigationMode.Expand);
        context.Settings.SaveTimestampPlacement(NoteTimestampPlacement.Line);
        context.Settings.SaveTimestampLine(42);
        var bytes = File.ReadAllBytes(context.Directory.File("settings.json"));

        context.Model.Reload();

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(context.Directory.File("settings.json")));
        Assert.IsTrue(context.Model.NewNoteModeBoth);
        Assert.IsTrue(context.Model.FolderNavigationModeExpand);
        Assert.IsTrue(context.Model.TimestampPlacementLine);
        Assert.AreEqual("42", context.Model.TimestampLineText);
        Assert.AreEqual(0, context.Startup.Calls);
        Assert.AreEqual(0, context.AppliedThemes.Count);
        Assert.AreEqual(0, context.Actions.Count);
        Assert.AreEqual(0, context.Native.Attempts.Count);
    }

    [TestMethod]
    public void RadioAndCheckboxEditsPersistAndNotifyTheBrowser()
    {
        using var context = new Context();
        var changes = new List<string>();
        context.Model.PreferenceChanged += changes.Add;

        context.Model.NewNoteModeQuick = true;
        context.Model.NewNoteModePrompt = false;
        context.Model.FolderNavigationModeExpand = true;
        context.Model.TimestampPlacementBottom = true;
        context.Model.ShowModifiedSubtitle = false;
        context.Model.ConfirmNoteDeletion = false;

        Assert.AreEqual(NewNoteMode.Quick, context.Settings.LoadNewNoteMode());
        Assert.AreEqual(FolderNavigationMode.Expand, context.Settings.LoadFolderNavigationMode());
        Assert.AreEqual(NoteTimestampPlacement.Bottom, context.Settings.LoadTimestampPlacement());
        Assert.IsFalse(context.Settings.LoadShowModifiedSubtitle());
        Assert.IsFalse(context.Settings.LoadConfirmNoteDeletion());
        CollectionAssert.Contains(changes, nameof(SettingsViewModel.NewNoteMode));
        CollectionAssert.Contains(changes, nameof(SettingsViewModel.FolderNavigationMode));
    }

    [TestMethod]
    [DataRow("0", "1")]
    [DataRow("10001", "10000")]
    [DataRow("invalid", "7")]
    [DataRow("-2", "7")]
    [DataRow(" 2 ", "7")]
    [DataRow("23", "23")]
    public void TimestampLineUsesExistingParsingFallbackAndBounds(string input, string expected)
    {
        using var context = new Context();
        context.Settings.SaveTimestampLine(7);
        context.Model.TimestampLineText = input;

        context.Model.CommitTimestampLine();

        Assert.AreEqual(expected, context.Model.TimestampLineText);
        Assert.AreEqual(int.Parse(expected), context.Settings.LoadTimestampLine());
    }

    [TestMethod]
    public void InvalidAccentDoesNotPersistAndLosingFocusRestoresTheActiveColor()
    {
        using var context = new Context();
        var original = context.Settings.LoadAccentColor();
        context.Model.AccentText = "#zzzzzz";
        Assert.IsTrue(context.Model.AccentHasError);
        Assert.AreEqual(original, context.Settings.LoadAccentColor());
        Assert.AreEqual(0, context.AppliedThemes.Count);

        context.Model.NormalizeAccentText();

        Assert.AreEqual(original, context.Model.AccentText);
        Assert.IsFalse(context.Model.AccentHasError);
        Assert.AreEqual(0, context.AppliedThemes.Count);
        context.Model.AccentText = "#aabbcc";
        context.Model.ThemeMode = AppThemeMode.Dark;
        Assert.AreEqual("#AABBCC", context.Settings.LoadAccentColor());
        Assert.AreEqual(AppThemeMode.Dark, context.Settings.LoadAppThemeMode());
        Assert.AreEqual((AppThemeMode.Dark, "#AABBCC"), context.AppliedThemes.Last());
        context.Model.ResetAppearanceCommand.Execute(null);
        Assert.AreEqual((AppThemeMode.System, AppTheme.DefaultAccentColor), context.AppliedThemes.Last());
    }

    [TestMethod]
    public void OpacityInputsAreNormalizedBeforePersistence()
    {
        using var context = new Context();
        context.Model.DefaultOpacityPercent = -1;
        context.Model.GhostOpacityPercent = 200;
        Assert.AreEqual(5d, context.Model.DefaultOpacityPercent);
        Assert.AreEqual(0.05, context.Settings.LoadDefaultOpacity());
        Assert.AreEqual(1d, context.Settings.LoadGhostModeOpacity());
        context.Model.DefaultOpacityPercent = double.NaN;
        context.Model.GhostOpacityPercent = double.PositiveInfinity;
        Assert.AreEqual(88d, context.Model.DefaultOpacityPercent);
        Assert.AreEqual(25d, context.Model.GhostOpacityPercent);
        Assert.AreEqual("25%", context.Model.GhostOpacityLabel);
    }

    [TestMethod]
    public void StartupFailureUsesObservedStateOrRetainsPreviousDisplayedState()
    {
        using var context = new Context();
        var notices = new List<SettingsNotice>();
        context.Model.NoticeRaised += notices.Add;
        context.Startup.Result = (false, null, "Registry is unavailable.");
        context.Model.RunOnStartup = true;
        Assert.IsFalse(context.Model.RunOnStartup);
        Assert.AreEqual("Registry is unavailable.", notices.Single().Message);

        context.Startup.Result = (true, true, null);
        context.Model.RunOnStartup = true;
        Assert.IsTrue(context.Model.RunOnStartup);
        context.Startup.Result = (false, true, "Could not disable startup.");
        context.Model.RunOnStartup = false;
        Assert.IsTrue(context.Model.RunOnStartup);
        Assert.AreEqual(2, notices.Count);
        Assert.AreEqual(3, context.Startup.Calls);
    }

    [TestMethod]
    public void MissingPreferredDisplayFallsBackInPresentationWithoutOverwritingThePreference()
    {
        using var context = new Context();
        context.Settings.SavePreferredDisplayDeviceName("missing");
        context.Model.Reload();

        Assert.IsNull(context.Model.PreferredDisplay!.DeviceName);
        Assert.AreEqual("missing", context.Model.PreferredDisplayDeviceName);
        Assert.AreEqual("missing", context.Settings.LoadPreferredDisplayDeviceName());
        context.Model.PreferredDisplay = context.Model.DisplayOptions[1];
        Assert.AreEqual("DISPLAY1", context.Settings.LoadPreferredDisplayDeviceName());
    }

    [TestMethod]
    public void BackupCommandsRespectEnabledStateAndStatusCanSurviveInfoRefresh()
    {
        using var context = new Context();
        context.Model.UpdateBackupInfo(new(false, false, null, 0, null));
        Assert.IsFalse(context.Model.RestoreBackupCommand.CanExecute(null));
        context.Model.RestoreBackupCommand.Execute(null);
        Assert.AreEqual(0, context.Actions.Count);
        context.Model.CreateBackupCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { SettingsAction.CreateBackup }, context.Actions);

        context.Model.BackupStatus = "A save failed. Try again.";
        context.Model.BackupStatusResource = "NotedDangerBrush";
        context.Model.UpdateBackupInfo(new(true, true, DateTime.UtcNow, 3, null), false);
        Assert.AreEqual("A save failed. Try again.", context.Model.BackupStatus);
        Assert.AreEqual("NotedDangerBrush", context.Model.BackupStatusResource);
        Assert.AreEqual("Update Backup", context.Model.CreateBackupText);
        Assert.IsTrue(context.Model.RestoreBackupCommand.CanExecute(null));
        context.Model.CanCreateBackup = false;
        context.Model.CreateBackupCommand.Execute(null);
        Assert.AreEqual(1, context.Actions.Count);
    }

    [TestMethod]
    public void PersistenceErrorsAreDisplayedWithoutApplyingUnsavedAppearance()
    {
        using var context = new Context();
        var notices = new List<SettingsNotice>();
        context.Model.NoticeRaised += notices.Add;
        var original = context.Model.ThemeMode;
        using (var locked = new FileStream(context.Directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            context.Model.ThemeMode = AppThemeMode.Dark;
            Assert.AreEqual(original, context.Model.ThemeMode);
            Assert.AreEqual(0, context.AppliedThemes.Count);
            Assert.AreEqual("Noted - Settings Error", notices.Single().Title);
            StringAssert.Contains(notices.Single().Message, "try again");
        }
        context.Model.ThemeMode = AppThemeMode.Dark;
        Assert.AreEqual(AppThemeMode.Dark, context.Settings.LoadAppThemeMode());
    }

    [TestMethod]
    public void HotkeyEditorDisplaysAmbiguityAndKeepsFailedEditsOpen()
    {
        using var context = new Context();
        context.Hotkeys.Initialize();
        context.Model.BeginHotkeyEdit(HotkeyFeature.Notes);
        Assert.AreEqual("Ctrl+Shift+Space", context.Model.HotkeyPreview);
        context.Model.HotkeyCtrl = false;
        context.Model.HotkeyShift = false;
        var notices = new List<SettingsNotice>();
        context.Model.NoticeRaised += notices.Add;
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.IsTrue(context.Model.IsHotkeyEditorOpen);
        Assert.AreEqual("Invalid Hotkey Selection", notices.Last().Title);

        context.Model.HotkeyAlt = true;
        context.Model.HotkeyKey = "N";
        var original = context.Hotkeys.GetHotkeyRegistration(HotkeyFeature.Notes);
        context.Native.Replacement = new(false, false, original,
            new HotkeyRegistration(999, "Notes", "Alt", "N"), "Rollback failed.");
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.AreEqual("Multiple active", context.Model.NotesHotkey);
        Assert.AreEqual("Ctrl+Shift+Space and Alt+N", context.Model.NotesHotkeyTooltip);
        Assert.AreEqual("Multiple Hotkeys May Be Active", notices.Last().Title);
        Assert.IsTrue(context.Model.IsHotkeyEditorOpen);
        context.Model.CancelHotkeyCommand.Execute(null);
        Assert.IsFalse(context.Model.ApplyHotkeyCommand.CanExecute(null));
    }

    private sealed class Context : IDisposable
    {
        internal TemporaryTestDirectory Directory { get; } = new();
        internal AppSettingsService Settings { get; }
        internal NoteFileService Files { get; }
        internal ControlledHotkeyService Native { get; } = new();
        internal ControlledStartupService Startup { get; } = new();
        internal HotkeyConfiguration Hotkeys { get; }
        internal SettingsViewModel Model { get; }
        internal List<(AppThemeMode, string)> AppliedThemes { get; } = [];
        internal List<SettingsAction> Actions { get; } = [];

        internal Context()
        {
            Settings = new AppSettingsService(Directory.Path);
            Settings.SaveNotesDirectory(Directory.File("notes"));
            Files = new NoteFileService(Settings);
            Hotkeys = new HotkeyConfiguration(Settings, () => Native, _ => () => { });
            Model = new SettingsViewModel(Settings, Files, Startup, Hotkeys,
                (mode, accent) => AppliedThemes.Add((mode, accent)), Actions.Add,
                () => new[] { new DisplayInfo("DISPLAY1", 0, 0, 1920, 1080, true) });
        }

        public void Dispose()
        {
            Hotkeys.Dispose();
            Files.Dispose();
            Directory.Dispose();
        }
    }

    private sealed class ControlledStartupService : IRunOnStartupService
    {
        public bool IsRunOnStartupEnabled => false;
        internal (bool Success, bool? ActualEnabled, string? Error) Result { get; set; } = (true, true, null);
        internal int Calls { get; private set; }
        public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled)
        {
            Calls++;
            return Result;
        }
    }
}
