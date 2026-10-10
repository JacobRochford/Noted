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
    public void EveryAppearanceOptionPreservesAccentAndReloadsSavedSelection()
    {
        using var context = new Context();
        CollectionAssert.AreEqual(Enum.GetValues<AppThemeMode>(),
            context.Model.AppearanceOptions.Select(option => option.Mode).ToArray());
        CollectionAssert.AreEqual(new[] { "Use Windows setting", "Light", "Dark", "Midnight Blue", "OLED", "Warm" },
            context.Model.AppearanceOptions.Select(option => option.Label).ToArray());
        foreach (var option in context.Model.AppearanceOptions)
        {
            context.Model.AccentText = "#7A5CB8";
            context.Model.ThemeMode = option.Mode;
            Assert.AreEqual((option.Mode, "#7A5CB8"), context.AppliedThemes.Last());
            context.Model.AccentText = "#008080";
            Assert.AreEqual((option.Mode, "#008080"), context.AppliedThemes.Last());
            context.Model.Reload();
            Assert.AreEqual(option.Mode, context.Model.ThemeMode);
            Assert.AreEqual("#008080", context.Model.AccentText);
        }
    }

    [TestMethod]
    public void UtilityOptionsReloadWithoutSavingAndRecoverFromPersistenceFailure()
    {
        using var context = new Context();
        context.Settings.SaveHiddenUtilities(new[] { "Scratchpad" });
        var original = File.ReadAllBytes(context.Directory.File("settings.json"));
        context.Model.Reload();
        Assert.IsFalse(context.Model.UtilityVisibility.Utilities.Single(option => option.Id == "Scratchpad").IsVisible);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(context.Directory.File("settings.json")));

        var notices = new List<SettingsNotice>();
        context.Model.NoticeRaised += notices.Add;
        using (var lockedFile = new FileStream(context.Directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            context.Model.UtilityVisibility.Utilities[0].IsVisible = false;
        Assert.IsTrue(context.Model.UtilityVisibility.Utilities[0].IsVisible);
        Assert.AreEqual(1, notices.Count);
        context.Model.UtilityVisibility.Utilities[0].IsVisible = false;
        CollectionAssert.AreEquivalent(new[] { "Checklist", "Scratchpad" },
            new AppSettingsService(context.Directory.Path).LoadHiddenUtilities().ToArray());
    }

    [TestMethod]
    public void IndentationDefaultsReloadAndChangesPersistWithoutChangingEditorOverrides()
    {
        using var context = new Context();
        var local = new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 2 };
        context.Settings.SaveMiniPadWindowState(new() { Indentation = local });
        context.Model.Reload();
        Assert.AreEqual(IndentationMode.Spaces, context.Model.DefaultIndentationMode);
        Assert.AreEqual(4, context.Model.DefaultTabSize);
        context.Model.DefaultIndentationMode = IndentationMode.Tabs;
        context.Model.DefaultTabSize = 8;
        var expected = new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 8 };
        Assert.AreEqual(expected, context.Settings.LoadDefaultIndentation());
        Assert.AreEqual(local, context.Settings.LoadMiniPadWindowState().Indentation);
        var bytes = File.ReadAllBytes(context.Directory.File("settings.json"));
        context.Model.Reload();
        Assert.AreEqual(expected.Mode, context.Model.DefaultIndentationMode);
        Assert.AreEqual(expected.TabSize, context.Model.DefaultTabSize);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(context.Directory.File("settings.json")));
    }

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
        context.Model.NormalizePastedText = false;

        Assert.AreEqual(NewNoteMode.Quick, context.Settings.LoadNewNoteMode());
        Assert.AreEqual(FolderNavigationMode.Expand, context.Settings.LoadFolderNavigationMode());
        Assert.AreEqual(NoteTimestampPlacement.Bottom, context.Settings.LoadTimestampPlacement());
        Assert.IsFalse(context.Settings.LoadShowModifiedSubtitle());
        Assert.IsFalse(context.Settings.LoadConfirmNoteDeletion());
        Assert.IsFalse(context.Settings.LoadNormalizePastedText());
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
        Assert.AreEqual((AppThemeMode.Dark, "#15FFFF"), context.AppliedThemes.Last());
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
            context.Model.ThemeMode = AppThemeMode.OLED;
            Assert.AreEqual(original, context.Model.ThemeMode);
            Assert.AreEqual(0, context.AppliedThemes.Count);
            Assert.AreEqual("Noted - Settings Error", notices.Single().Title);
            StringAssert.Contains(notices.Single().Message, "try again");
        }
        context.Model.ThemeMode = AppThemeMode.OLED;
        Assert.AreEqual(AppThemeMode.OLED, context.Settings.LoadAppThemeMode());
    }

    [TestMethod]
    public void HotkeyEditorDisplaysAmbiguityAndKeepsFailedEditsOpen()
    {
        using var context = new Context();
        context.Hotkeys.Initialize();
        context.Model.BeginHotkeyEdit(HotkeyFeature.Notes);
        Assert.AreEqual("Alt+N", context.Model.HotkeyPreview);
        context.Model.HotkeyAlt = false;
        var notices = new List<SettingsNotice>();
        context.Model.NoticeRaised += notices.Add;
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.IsTrue(context.Model.IsHotkeyEditorOpen);
        Assert.AreEqual("Invalid Hotkey Selection", notices.Last().Title);

        context.Model.HotkeyAlt = true;
        context.Model.HotkeyKey = "Q";
        var original = context.Hotkeys.GetHotkeyRegistration(HotkeyFeature.Notes);
        context.Native.Replacement = new(false, false, original,
            new HotkeyRegistration(999, "Noted", "Alt", "Q"), "Rollback failed.");
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.AreEqual("Multiple active", context.Model.NotesHotkey);
        StringAssert.Contains(context.Model.NotesHotkeyTooltip, "Active: Alt+N. Also active: Alt+Q.");
        Assert.AreEqual("Multiple Hotkeys May Be Active", notices.Last().Title);
        Assert.IsTrue(context.Model.IsHotkeyEditorOpen);
        context.Model.CancelHotkeyCommand.Execute(null);
        Assert.IsFalse(context.Model.ApplyHotkeyCommand.CanExecute(null));
    }

    [TestMethod]
    public void MiniPadHotkeyCanBeEditedPersistedAndResetToDefault()
    {
        using var context = new Context();
        context.Hotkeys.Initialize();
        Assert.AreEqual("Alt+M", context.Model.MiniPadHotkey);
        context.Model.BeginHotkeyEdit(HotkeyFeature.MiniPad);
        Assert.AreEqual("MiniPad hotkey", context.Model.HotkeyEditorTitle);
        Assert.AreEqual("Alt+M", context.Model.HotkeyPreview);

        context.Model.HotkeyShift = true;
        context.Model.HotkeyKey = "P";
        context.Model.ApplyHotkeyCommand.Execute(null);

        Assert.IsFalse(context.Model.IsHotkeyEditorOpen);
        Assert.AreEqual("Alt+Shift+P", context.Model.MiniPadHotkey);
        Assert.AreEqual(("Alt+Shift", "P"), new AppSettingsService(context.Directory.Path).LoadMiniPadHotkey());
        context.Model.BeginHotkeyEdit(HotkeyFeature.MiniPad);
        context.Model.ResetHotkeyCommand.Execute(null);
        Assert.AreEqual("Alt+M", context.Model.HotkeyPreview);
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.AreEqual("Alt+M", context.Model.MiniPadHotkey);
        Assert.AreEqual(("Alt", "M"), new AppSettingsService(context.Directory.Path).LoadMiniPadHotkey());
    }

    [TestMethod]
    public void HotkeyEditorUsesConfiguredPreferenceWhenSessionFallbackIsActive()
    {
        using var context = new Context();
        context.Native.Unavailable.Add("Alt+N");
        context.Hotkeys.Initialize();
        Assert.AreEqual("Alt+Shift+N", context.Model.NotesHotkey);
        StringAssert.Contains(context.Model.NotesHotkeyTooltip, "Configured: Alt+N. Active: Alt+Shift+N.");
        context.Model.BeginHotkeyEdit(HotkeyFeature.Notes);
        Assert.AreEqual("Alt+N", context.Model.HotkeyPreview);
    }

    [TestMethod]
    public void DisablingThroughEditorPreservesConfiguredCombinationAndCanBeReenabled()
    {
        using var context = new Context();
        context.Hotkeys.Initialize();
        context.Model.BeginHotkeyEdit(HotkeyFeature.Scratchpad);
        context.Model.HotkeyEnabled = false;
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.AreEqual("Disabled", context.Model.ScratchpadHotkey);
        Assert.AreEqual(("Alt", "S"), context.Settings.LoadScratchpadHotkey());
        context.Model.BeginHotkeyEdit(HotkeyFeature.Scratchpad);
        Assert.IsFalse(context.Model.HotkeyEnabled);
        Assert.AreEqual("Alt+S", context.Model.HotkeyPreview);
        context.Model.HotkeyEnabled = true;
        context.Model.ApplyHotkeyCommand.Execute(null);
        Assert.AreEqual("Alt+S", context.Model.ScratchpadHotkey);
        Assert.IsTrue(context.Settings.LoadHotkeyEnabled("Scratchpad"));
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
