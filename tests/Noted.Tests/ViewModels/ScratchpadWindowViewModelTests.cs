using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class ScratchpadWindowViewModelTests
{
    [TestMethod]
    public void LockedSettingsReturnLayoutSaveFailureAndRetryClearsTheError()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var initialState = new ScratchpadWindowState
        {
            Left = 10, Top = 20, Width = 500, Height = 300
        };
        settings.SaveScratchpadWindowState(initialState);
        var viewModel = new ScratchpadWindowViewModel(
            settings, new ScratchpadContentService(directory.Path));

        // Permit verification reads but deny replacement, which is a retryable write failure.
        using (var lockedFile = new FileStream(
            directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(viewModel.TrySaveWindowLayout(30, 40, 640, 480));
            Assert.IsTrue(viewModel.HasPersistenceError);
            StringAssert.StartsWith(viewModel.PersistenceError!, "Scratchpad settings could not be saved:");

            var settingsError = viewModel.PersistenceError;
            Assert.IsTrue(viewModel.TrySaveContent(@"{\rtf1\ansi content saved independently}"));
            Assert.AreEqual(settingsError, viewModel.PersistenceError,
                "Saving content must not clear the separate settings error.");
        }

        Assert.AreEqual(initialState,
            new AppSettingsService(directory.Path).LoadScratchpadWindowState());
        Assert.IsTrue(viewModel.TrySaveWindowLayout(30, 40, 640, 480),
            "Settings retry failure: " + viewModel.PersistenceError);
        Assert.IsFalse(viewModel.HasPersistenceError);
        Assert.IsNull(viewModel.PersistenceError);
        Assert.AreEqual(initialState with { Left = 30, Top = 40, Width = 640, Height = 480 },
            new AppSettingsService(directory.Path).LoadScratchpadWindowState());
    }

    [TestMethod]
    public void PreferenceSaveFailureStaysLocalAndRetryPreservesTheRequestedPreference()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var initialState = new ScratchpadWindowState();
        settings.SaveScratchpadWindowState(initialState);
        var viewModel = new ScratchpadWindowViewModel(
            settings, new ScratchpadContentService(directory.Path));
        var requestedWordWrap = !viewModel.WordWrapEnabled;

        // Permit verification reads but deny replacement, which is a retryable write failure.
        using (var lockedFile = new FileStream(
            directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            viewModel.WordWrapEnabled = requestedWordWrap;

            Assert.AreEqual(requestedWordWrap, viewModel.WordWrapEnabled);
            Assert.IsTrue(viewModel.HasPersistenceError);
            StringAssert.StartsWith(viewModel.PersistenceError!, "Scratchpad settings could not be saved:");
        }

        Assert.AreEqual(initialState.WordWrapEnabled,
            new AppSettingsService(directory.Path).LoadScratchpadWindowState().WordWrapEnabled);
        Assert.IsTrue(viewModel.TrySaveWindowLayout(30, 40, 640, 480),
            "Settings retry failure: " + viewModel.PersistenceError);
        Assert.IsFalse(viewModel.HasPersistenceError);
        Assert.IsNull(viewModel.PersistenceError);
        Assert.AreEqual(requestedWordWrap,
            new AppSettingsService(directory.Path).LoadScratchpadWindowState().WordWrapEnabled);
    }

    [TestMethod]
    public void LockedSettingsOnLoadUseDefaultWindowStateAndReportALocalError()
    {
        using var directory = new TemporaryTestDirectory();
        var initialState = new ScratchpadWindowState
        {
            Left = 10, Top = 20, Width = 500, Height = 300
        };
        new AppSettingsService(directory.Path).SaveScratchpadWindowState(initialState);
        ScratchpadWindowViewModel viewModel;

        using (var lockedFile = new FileStream(
            directory.File("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // A fresh service must read the locked file instead of using cached settings.
            viewModel = new ScratchpadWindowViewModel(
                new AppSettingsService(directory.Path), new ScratchpadContentService(directory.Path));

            Assert.AreEqual(new ScratchpadWindowState(), viewModel.InitialWindowState);
            Assert.IsTrue(viewModel.HasPersistenceError);
            StringAssert.StartsWith(viewModel.PersistenceError!, "Scratchpad settings could not be loaded:");
        }

        Assert.AreEqual(initialState,
            new AppSettingsService(directory.Path).LoadScratchpadWindowState());
        Assert.IsTrue(viewModel.TrySaveWindowLayout(30, 40, 640, 480),
            "Settings retry failure: " + viewModel.PersistenceError);
        Assert.IsFalse(viewModel.HasPersistenceError);
        Assert.IsNull(viewModel.PersistenceError);
        var savedState = new AppSettingsService(directory.Path).LoadScratchpadWindowState();
        Assert.AreEqual(30d, savedState.Left);
        Assert.AreEqual(40d, savedState.Top);
        Assert.AreEqual(640d, savedState.Width);
        Assert.AreEqual(480d, savedState.Height);
    }
}
