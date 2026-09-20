using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class HotkeyConfigurationTests
{
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
        CollectionAssert.AreEqual(new[] { "Ctrl+Shift+Space", "Alt+C", "Alt+D" }, native.Attempts);

        configuration.Dispose();
        configuration.Dispose();
        Assert.AreEqual(1, native.DisposeCalls);
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(0, configuration.Initialize().Count);
        Assert.AreEqual(1, creates);
    }

    [TestMethod]
    public void FallbackSkipsDuplicateCombinationsAndPersistsTheOwnedRegistration()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Ctrl+Shift+Space");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        var warnings = configuration.Initialize();

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "Active fallback: Alt+Shift+Space");
        Assert.AreEqual(1, native.Attempts.Count(attempt => attempt == "Ctrl+Shift+Space"));
        Assert.AreEqual(("Alt+Shift", "Space"), settings.LoadNotesHotkey());
        Assert.AreEqual("Alt+Shift+Space", configuration.GetHotkeyRegistration(HotkeyFeature.Notes)!.Combination);
    }

    [TestMethod]
    public void ExhaustedFallbackLeavesFeatureUnregisteredAndContinuesOtherFeatures()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var native = new ControlledHotkeyService();
        native.Unavailable.UnionWith(new[] { "Ctrl+Shift+Space", "Alt+Shift+Space", "Ctrl+Alt+Space", "Win+Alt+Space" });
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });

        var warnings = configuration.Initialize();

        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "No fallback is active.");
        Assert.IsNull(configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.IsNotNull(configuration.GetHotkeyRegistration(HotkeyFeature.Checklist));
        Assert.IsNotNull(configuration.GetHotkeyRegistration(HotkeyFeature.Dictionary));
    }

    [TestMethod]
    public void FallbackRemainsActiveWhenSavingFails()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesHotkey("Ctrl+Shift", "Space");
        var native = new ControlledHotkeyService();
        native.Unavailable.Add("Ctrl+Shift+Space");
        using var configuration = new HotkeyConfiguration(settings, () => native, _ => () => { });
        using var locked = new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var warnings = configuration.Initialize();

        StringAssert.Contains(warnings.Single(), "fallback is active for this session, but saving it failed");
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

        var result = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N");

        Assert.AreEqual(HotkeyChangeStatus.Failed, result.Status);
        Assert.AreSame(original, configuration.GetHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Ctrl+Shift", "Space"), settings.LoadNotesHotkey());
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

        var failed = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N");
        var blocked = configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "M");

        Assert.AreEqual(HotkeyChangeStatus.Failed, failed.Status);
        Assert.AreEqual(HotkeyChangeStatus.Ambiguous, blocked.Status);
        Assert.AreEqual(1, native.ReplacementCalls);
        Assert.AreSame(additional, configuration.GetAdditionalHotkeyRegistration(HotkeyFeature.Notes));
        Assert.AreEqual(("Ctrl+Shift", "Space"), settings.LoadNotesHotkey());
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

        Assert.AreEqual(HotkeyChangeStatus.Unchanged,
            configuration.Change(HotkeyFeature.Notes, new[] { "Alt" }, "N").Status);
        Assert.AreEqual(("Ctrl+Shift", "Space"), settings.LoadNotesHotkey());
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
}
