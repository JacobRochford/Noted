using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class HotkeyConfigurationTests
{
    [TestMethod]
    [DataRow("B", "Ctrl+Shift+B")]
    [DataRow("E", "Ctrl+Shift+E")]
    [DataRow("K", "Ctrl+Shift+K")]
    [DataRow("Q", "Ctrl+Shift+Q")]
    [DataRow("9", "Ctrl+Shift+9")]
    [DataRow("M", "Ctrl+Shift+M")]
    [DataRow("Up", "Alt+Shift+Up")]
    [DataRow("Down", "Alt+Shift+Down")]
    public void AutomaticFallbacksSkipEditorShortcutsWithoutChangingSavedPreferences(string key, string reserved)
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Alt", key);
        var original = File.ReadAllBytes(directory.File("settings.json"));
        var native = new ControlledHotkeyService();
        foreach (var candidateKey in new[] { key, "N" })
        foreach (var modifiers in new[] { "Alt", "Alt+Shift", "Ctrl+Alt", "Win+Alt", "Ctrl+Shift" })
            native.Unavailable.Add($"{modifiers}+{candidateKey}");
        native.Unavailable.Remove(reserved);
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        configuration.Initialize();

        Assert.IsFalse(native.Attempts.Contains(reserved));
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(directory.File("settings.json")));
    }

    [TestMethod]
    public void ExplicitEditorConflictsAndDisabledHotkeysKeepTheirSavedSettings()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveGlobalHotkey("NoteEditor", "Ctrl+Shift", "E", true);
        settings.SaveGlobalHotkey("MiniPad", "Ctrl", "B", false);
        var original = File.ReadAllBytes(directory.File("settings.json"));
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        configuration.Initialize();

        Assert.AreEqual("Ctrl+Shift+E", configuration.GetHotkeyRegistration(HotkeyFeature.NoteEditor)!.Combination);
        Assert.IsFalse(configuration.IsEnabled(HotkeyFeature.MiniPad));
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.MiniPad));
        Assert.IsFalse(native.Attempts.Contains("Ctrl+B"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(directory.File("settings.json")));
    }

    [TestMethod]
    public void CaptureCollisionUsesSessionFallbackWithoutReplacingNoteEditorShortcut()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+J");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        var warnings = configuration.Initialize();
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual("Alt+E", configuration.GetHotkeyRegistration(HotkeyFeature.NoteEditor)!.Combination);
        Assert.AreEqual("Ctrl+Alt+J", configuration.GetHotkeyRegistration(HotkeyFeature.JotCapture)!.Combination);
        Assert.AreEqual(("Alt", "J"), new AppSettingsService(directory.Path).LoadJotCaptureHotkey());
        Assert.AreEqual(1, native.Attempts.Count(attempt => attempt == "Alt+J"));
    }

    [TestMethod]
    public void CaptureShortcutCanBeChangedAndRestoredOnRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        using var configuration = new HotkeyConfiguration(settings, () => new ControlledHotkeyService(), _ => () => { });
        configuration.Initialize();
        Assert.AreEqual(HotkeyChangeStatus.Saved,
            configuration.Change(HotkeyFeature.JotCapture, ["Win", "Alt"], "Q").Status);
        using var restarted = new HotkeyConfiguration(new AppSettingsService(directory.Path),
            () => new ControlledHotkeyService(), _ => () => { });
        Assert.AreEqual(0, restarted.Initialize().Count);
        Assert.AreEqual("Win+Alt+Q", restarted.GetHotkeyRegistration(HotkeyFeature.JotCapture)!.Combination);
    }

    [TestMethod]
    public void InitializationIsLazyReentrantSafeAndIdempotentAndDisposalPreventsRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        var creates = 0;
        var configuration = new HotkeyConfiguration(settings, () => { creates++; return native; }, _ => () => { });
        Assert.AreEqual(0, creates);
        native.Registering = () => Assert.AreEqual(0, configuration.Initialize().Count);

        Assert.AreEqual(0, configuration.Initialize().Count);
        Assert.AreEqual(0, configuration.Initialize().Count);
        Assert.AreEqual(1, creates);
        CollectionAssert.AreEqual(new[] { "Alt+N", "Alt+C", "Alt+D", "Alt+M", "Alt+E", "Alt+J", "Alt+S", "Alt+Shift+J" }, native.Attempts);

        configuration.Dispose();
        configuration.Dispose();
        Assert.AreEqual(1, native.DisposeCalls);
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.MiniPad));
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.NoteEditor));
        Assert.AreEqual(0, configuration.Initialize().Count);
        Assert.AreEqual(1, creates);
    }

    [TestMethod]
    public void MiniPadFallbackDoesNotPersistAndConfiguredShortcutIsRetriedOnRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+M");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        var warnings = configuration.Initialize();

        StringAssert.Contains(warnings.Single(), "MiniPad hotkey Alt+M was unavailable.");
        Assert.AreEqual("Alt+Shift+M", configuration.GetHotkeyRegistration(HotkeyFeature.MiniPad)!.Combination);
        var reloaded = new AppSettingsService(directory.Path);
        Assert.AreEqual(("Alt", "M"), reloaded.LoadMiniPadHotkey());
        using var restarted = new HotkeyConfiguration(reloaded, () => new ControlledHotkeyService(), _ => () => { });
        Assert.AreEqual(0, restarted.Initialize().Count);
        Assert.AreEqual("Alt+M", restarted.GetHotkeyRegistration(HotkeyFeature.MiniPad)!.Combination);
    }

    [TestMethod]
    public void FallbackSkipsDuplicateCombinationsAndRetainsConfiguredPreference()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+N");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        var warnings = configuration.Initialize();

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "Active fallback for this session: Alt+Shift+N");
        Assert.AreEqual(1, native.Attempts.Count(attempt => attempt == "Alt+N"));
        Assert.AreEqual(("Alt", "N"), settings.LoadNotesHotkey());
        Assert.AreEqual("Alt+Shift+N", configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!.Combination);
    }

    [TestMethod]
    public void ExhaustedFallbackLeavesFeatureUnregisteredAndContinuesOtherFeatures()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.UnionWith(new[] { "Alt+N", "Alt+Shift+N", "Ctrl+Alt+N", "Win+Alt+N", "Ctrl+Shift+N" });
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        var warnings = configuration.Initialize();

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "No fallback is active.");
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsNotNull(configuration.GetHotkeyRegistration(HotkeyFeature.Checklist));
        Assert.IsNotNull(configuration.GetHotkeyRegistration(HotkeyFeature.Dictionary));
    }

    [TestMethod]
    public void FallbackDoesNotWriteEvenWhenSettingsAreLocked()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Ctrl+Shift", "Space");
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+N");
        native.Unavailable.Add("Ctrl+Shift+Space");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        using var locked = new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var warnings = configuration.Initialize();

        StringAssert.Contains(warnings.Single(), "Your configured shortcut was retained.");
        Assert.AreEqual("Alt+Shift+Space", configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!.Combination);
        Assert.AreEqual(("Ctrl+Shift", "Space"), settings.LoadNotesHotkey());
    }

    [TestMethod]
    public void FailedReplacementRetainsOwnedStateAndDoesNotPersist()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        var original = configuration.GetHotkeyRegistration(HotkeyFeature.Notes);
        native.Replacement = new(false, false, original, null, "Replacement failed; original is active.");

        var result = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "Q");

        Assert.AreEqual(HotkeyChangeStatus.Failed, result.Status);
        Assert.AreSame(original, configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Alt", "N"), settings.LoadNotesHotkey());
    }

    [TestMethod]
    public void DualRegistrationBlocksFurtherReplacementUntilRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        var original = configuration.GetHotkeyRegistration(HotkeyFeature.Notes);
        var additional = new HotkeyRegistration(999, "Notes", "Alt", "N");
        native.Replacement = new(false, false, original, additional, "Rollback failed.");

        var failed = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "Q");
        var blocked = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "M");

        Assert.AreEqual(HotkeyChangeStatus.Failed, failed.Status);
        Assert.AreEqual(HotkeyChangeStatus.Ambiguous, blocked.Status);
        Assert.AreEqual(1, native.ReplacementCalls);
        Assert.AreSame(additional, configuration.GetAdditionalHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Alt", "N"), settings.LoadNotesHotkey());
    }

    [TestMethod]
    public void SaveFailureRetainsNewActiveHotkeyAndSuccessfulChangesPersist()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Ctrl+Shift", "Space");
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        using (var locked = new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N");
            Assert.AreEqual(HotkeyChangeStatus.SaveFailed, result.Status);
            Assert.AreEqual("Alt+N", configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!.Combination);
            Assert.AreEqual(("Ctrl+Shift", "Space"), settings.LoadNotesHotkey());
        }

        var replacementCalls = native.ReplacementCalls;
        Assert.AreEqual(HotkeyChangeStatus.Saved,
            configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N").Status);
        Assert.AreEqual(replacementCalls, native.ReplacementCalls);
        Assert.AreEqual(("Alt", "N"), settings.LoadNotesHotkey());
        Assert.AreEqual(HotkeyChangeStatus.Saved,
            configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "M").Status);
        Assert.AreEqual(("Alt", "M"), settings.LoadNotesHotkey());
    }

    [TestMethod]
    public void InvalidOrUnavailableRequestsDoNotCallNativeReplacement()
    {
        using var directory = new TemporaryTestDirectory();
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(new AppSettingsService(directory.Path), () => native, _ => () => { });
        Assert.AreEqual(HotkeyChangeStatus.InvalidSelection, configuration.Change(HotkeyFeature.Notes, Array.Empty<string>(), "N").Status);
        Assert.AreEqual(HotkeyChangeStatus.Unavailable, configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N").Status);
        Assert.AreEqual(0, native.ReplacementCalls);
    }

    [TestMethod]
    public void DisableRetainsCombinationAndRestartSkipsRegistrationUntilEnabled()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Ctrl+Alt", "Q");
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        var original = configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!;
        Assert.AreEqual(HotkeyChangeStatus.Saved, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
        CollectionAssert.AreEqual(new[] { original.Id }, native.UnregisteredIds);
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Ctrl+Alt", "Q"), settings.LoadNotesHotkey());
        Assert.IsFalse(settings.LoadHotkeyEnabled("Notes"));

        var restartedNative = new ControlledHotkeyService();
        using var restarted = new HotkeyConfiguration(new AppSettingsService(directory.Path), () => restartedNative, _ => () => { });
        restarted.Initialize();
        Assert.IsFalse(restartedNative.Attempts.Contains("Ctrl+Alt+Q"));
        Assert.AreEqual(HotkeyChangeStatus.Saved, restarted.Change(HotkeyFeature.Notes, ["Ctrl", "Alt"], "Q").Status);
        Assert.AreEqual("Ctrl+Alt+Q", restarted.GetHotkeyRegistration(HotkeyFeature.Notes)!.Combination);
        Assert.IsTrue(new AppSettingsService(directory.Path).LoadHotkeyEnabled("Notes"));
    }

    [TestMethod]
    public void FailedDisableRetainsRegistrationEnabledPreferenceAndCallback()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService { Unregistration = new(false, "Unregister failed") };
        var calls = 0;
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => calls++);
        configuration.Initialize();
        var original = configuration.GetHotkeyRegistration(HotkeyFeature.Notes);
        Assert.AreEqual(HotkeyChangeStatus.Failed, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
        Assert.AreSame(original, configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsTrue(configuration.IsEnabled(HotkeyFeature.Notes));
        Assert.IsTrue(settings.LoadHotkeyEnabled("Notes"));
        native.Callbacks["Noted"]();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void DisableSaveFailureRetriesWithoutAnotherUnregister()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Alt", "Q");
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        using (var locked = new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.AreEqual(HotkeyChangeStatus.SaveFailed, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
            Assert.IsFalse(configuration.IsEnabled(HotkeyFeature.Notes));
            Assert.IsTrue(settings.LoadHotkeyEnabled("Notes"));
        }
        Assert.AreEqual(HotkeyChangeStatus.Saved, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
        Assert.AreEqual(1, native.UnregisteredIds.Count);
        Assert.IsFalse(new AppSettingsService(directory.Path).LoadHotkeyEnabled("Notes"));
        Assert.AreEqual(("Alt", "Q"), settings.LoadNotesHotkey());
    }

    [TestMethod]
    public void CallbacksAreSuspendedDuringShutdownAndResumeAfterCancellation()
    {
        using var directory = new TemporaryTestDirectory();
        var native = new ControlledHotkeyService();
        var calls = 0;
        using var configuration = new HotkeyConfiguration(new AppSettingsService(directory.Path), () => native, _ => () => calls++);
        configuration.Initialize();
        var callback = native.Callbacks["Noted"];
        callback();
        configuration.CallbacksSuspended = true;
        callback();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(HotkeyChangeStatus.Unavailable, configuration.Change(HotkeyFeature.Notes, ["Alt"], "Q").Status);
        configuration.CallbacksSuspended = false;
        callback();
        Assert.AreEqual(2, calls);
        configuration.Change(HotkeyFeature.Notes, [], null, false);
        callback();
        Assert.AreEqual(2, calls);
        configuration.Dispose();
        callback();
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void UnchangedActiveFallbackCanBeExplicitlySavedWithoutNativeReplacement()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+N");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        Assert.AreEqual(("Alt", "N"), configuration.LoadHotkey(HotkeyFeature.Notes));
        Assert.AreEqual(HotkeyChangeStatus.Saved, configuration.Change(HotkeyFeature.Notes, ["Alt", "Shift"], "N").Status);
        Assert.AreEqual(0, native.ReplacementCalls);
        Assert.AreEqual(("Alt+Shift", "N"), new AppSettingsService(directory.Path).LoadNotesHotkey());
    }

    [TestMethod]
    public void FailedEnableRetainsDisabledPreferenceAndConfiguredCombination()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveGlobalHotkey("Notes", "Alt", "Q", false);
        var native = new ControlledHotkeyService { Replacement = new(false, false, null, null, "Windows rejected") };
        var calls = 0;
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => calls++);
        configuration.Initialize();
        Assert.AreEqual(HotkeyChangeStatus.Failed, configuration.Change(HotkeyFeature.Notes, ["Alt"], "Q").Status);
        Assert.IsFalse(configuration.IsEnabled(HotkeyFeature.Notes));
        Assert.IsFalse(settings.LoadHotkeyEnabled("Notes"));
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Alt", "Q"), settings.LoadNotesHotkey());
        native.Callbacks["Noted"]();
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void StartupFallbackCannotClaimAnotherFeaturesConfiguredBinding()
    {
        using var directory = new TemporaryTestDirectory();
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Alt+J");
        using var configuration = new HotkeyConfiguration(new AppSettingsService(directory.Path), () => native, _ => () => { });
        configuration.Initialize();
        Assert.AreEqual("Ctrl+Alt+J", configuration.GetHotkeyRegistration(HotkeyFeature.JotCapture)!.Combination);
        Assert.AreEqual("Alt+Shift+J", configuration.GetHotkeyRegistration(HotkeyFeature.Jots)!.Combination);
        Assert.AreEqual(1, native.Attempts.Count(attempt => attempt == "Alt+Shift+J"));
    }

    [TestMethod]
    public void DisablingAfterRollbackFailureTracksPartialCleanupAndCanBeRetried()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        configuration.Initialize();
        var original = configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!;
        var additional = new HotkeyRegistration(999, "Noted", "Alt", "Q");
        native.Replacement = new(false, false, original, additional, "Rollback failed.");
        Assert.AreEqual(HotkeyChangeStatus.Failed, configuration.Change(HotkeyFeature.Notes, ["Alt"], "Q").Status);
        native.UnregistrationResults.Enqueue(new(true, ""));
        native.UnregistrationResults.Enqueue(new(false, "Still registered"));
        Assert.AreEqual(HotkeyChangeStatus.Failed, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
        Assert.AreSame(additional, configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsNull(configuration.GetAdditionalHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsTrue(settings.LoadHotkeyEnabled("Notes"));
        Assert.AreEqual(HotkeyChangeStatus.Saved, configuration.Change(HotkeyFeature.Notes, [], null, false).Status);
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsFalse(settings.LoadHotkeyEnabled("Notes"));
        Assert.AreEqual(("Alt", "N"), settings.LoadNotesHotkey());
        CollectionAssert.AreEqual(new[] { original.Id, additional.Id, additional.Id }, native.UnregisteredIds);
    }
}
