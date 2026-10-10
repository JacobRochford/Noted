using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MiniPadWindowTests
{
    [TestMethod]
    public void AppearanceKeysKeepHistoricalSingletonAndInstanceFormatsRegardlessOfTitle()
    {
        WithWindows((settings, directory) =>
        {
            var singleton = new MiniPadWindow(new MiniPadRecoveryService(directory.Path,
                Guid.Parse("a6cb4208-79c0-4c08-9d07-9cdf33f31337")), settings);
            var instance = new MiniPadWindow(new MiniPadRecoveryService(directory.Path,
                Guid.Parse("12345678-1234-5678-abcd-0123456789ab")), settings);
            try
            {
                singleton.Title = "Renamed singleton";
                instance.Title = "Renamed instance";
                Assert.AreEqual("MiniPadWindow", WindowAppearance.GetSettingsKey(singleton));
                Assert.AreEqual("MiniPadWindow:1234567812345678abcd0123456789ab", WindowAppearance.GetSettingsKey(instance));
            }
            finally
            {
                singleton.Close();
                instance.Close();
            }
        });
    }

    [TestMethod]
    public void NewCommandCapsInstancesAndShowingUsesMostRecentlyActivatedWindow()
    {
        WithWindows((settings, directory) =>
        {
            var created = 0;
            WindowManager.MiniPadProvider = () =>
            {
                created++;
                return new MiniPadWindow(new MiniPadRecoveryService(directory.Path, Guid.NewGuid()), settings);
            };

            WindowManager.ShowMiniPad();
            var first = WindowManager.MiniPad!;
            WindowManager.ShowMiniPad();
            Assert.AreEqual(1, created);
            for (var i = 1; i < WindowManager.MaximumMiniPads; i++) WindowManager.NewMiniPad();
            Assert.HasCount(5, WindowManager.MiniPads);
            Assert.AreEqual(5, WindowManager.MiniPads.Select(window => window.InstanceId).Distinct().Count());

            first.Activate();
            Assert.AreSame(first, WindowManager.MiniPad);
            WindowManager.NewMiniPad();
            Assert.AreEqual(5, created);
            WindowManager.HideAll();
            WindowManager.ShowAllMiniPads();
            Assert.IsTrue(WindowManager.MiniPads.All(window => window.IsWindowVisible));
            Assert.AreSame(first, WindowManager.MiniPad);
            foreach (var window in WindowManager.MiniPads.Where(window => window != first))
                window.HideWindow();
            first.Activate();
            first.HideWindow();
            WindowManager.ShowMiniPad();
            Assert.IsTrue(first.IsWindowVisible);
            Assert.AreSame(first, WindowManager.MiniPad);
            Assert.AreEqual(5, created);
        });
    }

    [TestMethod]
    public void ClosingMiddleInstancePreservesOtherIdentitiesContentAndState()
    {
        WithWindows((settings, directory) =>
        {
            var windows = Enumerable.Range(0, 3).Select(index =>
            {
                var window = new MiniPadWindow(new MiniPadRecoveryService(directory.Path, Guid.NewGuid()), settings);
                WindowManager.RegisterMiniPad(window);
                window.Show();
                window.Left = 120 + index * 40;
                window.Top = 130 + index * 40;
                window.Width = 330 + index * 20;
                ((TextBox)window.FindName("Editor")).Text = $"content {index}";
                Assert.IsTrue(window.TryFlushPendingContent(out _));
                window.PrepareForApplicationShutdown();
                window.CancelPreparedClose();
                return window;
            }).ToArray();
            var before = settings.LoadMiniPadWindows();

            windows[1].Close();

            Assert.HasCount(2, WindowManager.MiniPads);
            Assert.IsTrue(settings.LoadMiniPadWindows()[windows[1].InstanceId].IsClosed);
            foreach (var index in new[] { 0, 2 })
            {
                var window = windows[index];
                Assert.IsTrue(WindowManager.MiniPads.Contains(window));
                Assert.AreEqual(before[window.InstanceId], settings.LoadMiniPadWindows()[window.InstanceId]);
                Assert.AreEqual($"content {index}", new MiniPadRecoveryService(directory.Path, window.InstanceId).LoadDraft().Draft!.Content);
            }
            Assert.AreEqual("content 1", new MiniPadRecoveryService(directory.Path, windows[1].InstanceId).LoadDraft().Draft!.Content);
        });
    }

    [TestMethod]
    public void GroupVisibilityRestoresOnlyPreviouslyVisibleSurvivingInstances()
    {
        WithWindows((settings, directory) =>
        {
            var windows = Enumerable.Range(0, 3).Select(_ =>
            {
                var window = new MiniPadWindow(new MiniPadRecoveryService(directory.Path, Guid.NewGuid()), settings);
                WindowManager.RegisterMiniPad(window);
                window.Show();
                return window;
            }).ToArray();
            windows[2].HideWindow();

            WindowManager.HideAll();
            Assert.IsFalse(WindowManager.AnyWindowVisible());
            Assert.IsTrue(windows[0].IsHiddenTogether);
            windows[1].Close();
            WindowManager.ToggleNotedWindows();

            Assert.IsTrue(windows[0].IsWindowVisible);
            Assert.IsFalse(windows[2].IsWindowVisible);
            Assert.HasCount(2, WindowManager.MiniPads);
        });
    }

    [TestMethod]
    public void RestartPreservesInstanceStateAndDoesNotRestoreClosedInstances()
    {
        WithWindows((settings, directory) =>
        {
            var openId = Guid.NewGuid();
            var hiddenId = Guid.NewGuid();
            var closedId = Guid.NewGuid();
            foreach (var id in new[] { openId, hiddenId, closedId })
                new MiniPadRecoveryService(directory.Path, id).SaveDraft(id.ToString());
            settings.SaveMiniPadWindowState(openId, new MiniPadWindowState
            {
                Left = 140, Top = 160, Width = 390, Height = 310,
                ReopenOnStartup = true, WordWrapEnabled = false
            });
            settings.SaveMiniPadWindowState(hiddenId, new MiniPadWindowState());
            settings.SaveMiniPadWindowState(closedId, new MiniPadWindowState { IsClosed = true });

            var restartedSettings = new AppSettingsService(directory.Path);
            var issues = WindowManager.RestoreMiniPads(restartedSettings,
                (recovery, draft) => new MiniPadWindow(recovery, restartedSettings, draft));

            Assert.HasCount(0, issues);
            Assert.HasCount(2, WindowManager.MiniPads);
            var open = WindowManager.MiniPads.Single(window => window.InstanceId == openId);
            var hidden = WindowManager.MiniPads.Single(window => window.InstanceId == hiddenId);
            Assert.IsTrue(open.IsWindowVisible);
            Assert.IsFalse(hidden.IsWindowVisible);
            Assert.AreEqual(390, open.Width);
            Assert.AreEqual(310, open.Height);
            Assert.AreEqual(openId.ToString(), ((TextBox)open.FindName("Editor")).Text);
            Assert.AreEqual(TextWrapping.NoWrap, ((TextBox)open.FindName("Editor")).TextWrapping);
        });
    }

    [TestMethod]
    public void RestorationCapsWindowsWithoutDeletingExcessDrafts()
    {
        WithWindows((settings, directory) =>
        {
            var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in ids)
            {
                settings.SaveMiniPadWindowState(id, new MiniPadWindowState());
                new MiniPadRecoveryService(directory.Path, id).SaveDraft(id.ToString());
            }
            WindowManager.RestoreMiniPads(settings,
                (recovery, draft) => new MiniPadWindow(recovery, settings, draft));

            Assert.HasCount(5, WindowManager.MiniPads);
            foreach (var id in ids)
                Assert.AreEqual(id.ToString(), new MiniPadRecoveryService(directory.Path, id).LoadDraft().Draft!.Content);
        });
    }

    [TestMethod]
    public void ShutdownPreservesGroupHiddenVisibilityAndIndependentAppearanceKeys()
    {
        WithWindows((settings, directory) =>
        {
            var first = new MiniPadWindow(new MiniPadRecoveryService(directory.Path, Guid.NewGuid()), settings);
            var second = new MiniPadWindow(new MiniPadRecoveryService(directory.Path, Guid.NewGuid()), settings);
            WindowManager.RegisterMiniPad(first);
            WindowManager.RegisterMiniPad(second);
            first.Show();
            second.Show();
            second.HideWindow();
            settings.SaveWindowAlwaysVisible(first.AppearanceSettingsKey, true);
            settings.SaveWindowAlwaysVisible(second.AppearanceSettingsKey, false);
            WindowManager.HideAll();

            Assert.IsTrue(WindowManager.TryPrepareForShutdown(out var error), error);

            var state = new AppSettingsService(directory.Path);
            Assert.IsTrue(state.LoadMiniPadWindows()[first.InstanceId].ReopenOnStartup);
            Assert.IsFalse(state.LoadMiniPadWindows()[second.InstanceId].ReopenOnStartup);
            Assert.IsTrue(state.LoadWindowAlwaysVisible(first.AppearanceSettingsKey));
            Assert.IsFalse(state.LoadWindowAlwaysVisible(second.AppearanceSettingsKey));
            Assert.AreNotEqual(first.AppearanceSettingsKey, second.AppearanceSettingsKey);
        });
    }

    [TestMethod]
    public void LegacySingletonMigratesWithoutChangingItsDraftOrAppearanceKey()
    {
        WithWindows((settings, directory) =>
        {
            // Write the original format, before any settings have been loaded.
            File.WriteAllText(directory.File("settings.json"), JsonSerializer.Serialize(new
            {
                MiniPadWindowState = new MiniPadWindowState { WordWrapEnabled = false },
                AlwaysVisibleWindows = new[] { nameof(MiniPadWindow) }
            }));
            var recovery = new MiniPadRecoveryService(directory.Path);
            recovery.SaveDraft("legacy text");
            var path = directory.File("recovery", "micro-scratchpads", $"{recovery.Id:N}.json");
            var originalBytes = File.ReadAllBytes(path);

            WindowManager.RestoreMiniPads(settings,
                (service, draft) => new MiniPadWindow(service, settings, draft));
            var window = WindowManager.MiniPads.Single();
            window.PrepareForApplicationShutdown();

            Assert.AreEqual(MiniPadRecoveryService.LegacyDraftId, window.InstanceId);
            Assert.AreEqual(nameof(MiniPadWindow), window.AppearanceSettingsKey);
            Assert.IsTrue(settings.LoadWindowAlwaysVisible(window.AppearanceSettingsKey));
            Assert.AreEqual("legacy text", ((TextBox)window.FindName("Editor")).Text);
            Assert.IsFalse(new AppSettingsService(directory.Path).LoadMiniPadWindows()[window.InstanceId].WordWrapEnabled);
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
        });
    }

    private static void WithWindows(Action<AppSettingsService, TemporaryTestDirectory> action)
    {
        WpfTestHost.Invoke(() =>
        {
            using var directory = new TemporaryTestDirectory();
            WindowManager.ClearAll();
            try { action(new AppSettingsService(directory.Path), directory); }
            finally
            {
                foreach (var window in WindowManager.MiniPads)
                {
                    window.KeepDraftOnClose();
                    window.Close();
                }
                WindowManager.ClearAll();
            }
        });
    }
}
