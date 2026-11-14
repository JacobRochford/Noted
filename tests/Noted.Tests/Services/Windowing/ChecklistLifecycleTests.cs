using System.Windows.Threading;
using System.Diagnostics;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistLifecycleTests
{
    [TestMethod]
    public void ClosedChecklistStillAutosavesAndFlushesDuringShutdown() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new CountingContentService(directory.Path);
        using var model = new ChecklistWindowViewModel(content, Dispatcher.CurrentDispatcher, action => action(), false);
        var flushes = new ShutdownFlushCoordinator();
        using var registration = flushes.Register("Checklist", () =>
            model.TryFlushPendingItems(out var error) ? PersistenceSaveResult.Succeeded() : PersistenceSaveResult.Failed(error!));
        var item = model.AddItem()!;
        item.Text = "before close";
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), model);
        window.Show();
        window.Close();
        var savedBeforeEdit = content.SaveCount;
        item.Text = "autosaved while closed";
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => frame.Continue = content.SaveCount == savedBeforeEdit && elapsed.Elapsed < TimeSpan.FromSeconds(3);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(content.SaveCount > savedBeforeEdit);
        Assert.AreEqual(item.Text, content.Store.LoadItems().Items.Single().Text);

        item.Text = "pending at shutdown";
        var sequence = new ApplicationShutdownSequence(flushes);
        var shutdown = sequence.Execute([], () => WindowPreparationResult.Succeeded(), () => false,
            () => throw new AssertFailedException("No restore was requested."),
            () => Assert.Fail("Shutdown should not be cancelled."), () => { });
        Assert.IsTrue(shutdown.CanShutdown);
        Assert.AreEqual("pending at shutdown", content.Store.LoadItems().Items.Single().Text);
    });

    [TestMethod]
    public void ClosingAndReopeningSharesDataAndResetsOnlyPresentation() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new CountingContentService(directory.Path);
        using var model = new ChecklistWindowViewModel(content, Dispatcher.CurrentDispatcher, action => action(), false);
        var item = model.AddItem()!;
        item.Text = "alpha";
        item.ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(1);
        var first = new ChecklistWindow(settings, model);
        try
        {
            Assert.IsTrue(model.TryCreateTab("Work", out var tab));
            model.MoveItemToTab(item, tab!.Id);
            model.SearchQuery = "alpha";
            model.SortMode = ChecklistSortMode.Alphabetical;
            model.IsSearchVisible = true;
            item.IsExpanded = true;
        }
        finally { first.Close(); }

        var savedTabId = settings.LoadChecklistWindowState().SelectedTabId;
        model.SelectTab(model.FindTab(ChecklistTab.AllId));
        model.GhostModeEnabled = true;
        Assert.AreEqual(savedTabId, settings.LoadChecklistWindowState().SelectedTabId);
        Assert.IsFalse(settings.LoadChecklistWindowState().GhostModeEnabled);
        item.Notes = "changed while closed";
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        Assert.AreEqual("changed while closed", content.Store.LoadItems().Items.Single().Notes);

        var reopened = new ChecklistWindow(settings, model);
        try
        {
            Assert.AreSame(model, reopened.DataContext);
            Assert.AreSame(item, model.Items.Single());
            Assert.AreEqual(1, content.LoadCount);
            Assert.AreEqual(savedTabId, model.SelectedTab?.Id);
            Assert.AreEqual("", model.SearchQuery);
            Assert.AreEqual(ChecklistSortMode.Manual, model.SortMode);
            Assert.IsFalse(model.IsSearchVisible);
            Assert.IsFalse(model.GhostModeEnabled);
            Assert.IsFalse(item.IsExpanded);
            Assert.IsNotNull(item.ReminderAtUtc);
        }
        finally { reopened.Close(); }

        item.Text = "still observed after both windows close";
        Assert.IsTrue(model.TryFlushPendingItems(out error), error);
        Assert.AreEqual(item.Text, content.Store.LoadItems().Items.Single().Text);
    });

    private sealed class CountingContentService(string directory) : IChecklistContentService
    {
        internal ChecklistContentService Store { get; } = new(directory);
        internal int LoadCount { get; private set; }
        internal int SaveCount { get; private set; }
        public ChecklistContentLoadResult LoadItems() { LoadCount++; return Store.LoadItems(); }
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            var result = Store.SaveItems(items);
            SaveCount++;
            return result;
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => Store.SaveTabs(tabs);
    }
}
