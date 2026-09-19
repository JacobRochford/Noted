using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class ChecklistWindowViewModelTests
{
    [TestMethod]
    public void SuccessfulItemSaveDoesNotClearTabFailure()
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService { TabError = new IOException("tab failure") };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsFalse(viewModel.TryCreateTab("Work", out _));
            var tabError = viewModel.PersistenceError;
            Assert.IsNotNull(tabError);

            Assert.IsNotNull(viewModel.AddItem());
            Assert.IsTrue(viewModel.TryFlushPendingItems(out var error));
            Assert.IsNull(error);
            Assert.AreEqual(tabError, viewModel.PersistenceError);
            Assert.IsTrue(viewModel.HasPersistenceError);

            service.TabError = null;
            Assert.IsTrue(viewModel.TryCreateTab("Work", out _));
            Assert.IsNull(viewModel.PersistenceError);
            Assert.IsFalse(viewModel.HasPersistenceError);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void SuccessfulTabSaveDoesNotClearItemFailure()
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService { ItemError = new IOException("item failure") };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsNotNull(viewModel.AddItem());
            Assert.IsFalse(viewModel.TryFlushPendingItems(out var itemError));
            Assert.IsNotNull(itemError);

            Assert.IsTrue(viewModel.TryCreateTab("Work", out _));
            Assert.AreEqual(itemError, viewModel.PersistenceError);

            service.ItemError = null;
            Assert.IsTrue(viewModel.TryFlushPendingItems(out _));
            Assert.IsNull(viewModel.PersistenceError);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ItemFailureTakesPrecedenceRegardlessOfSaveOrder(bool itemsFirst)
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService
            {
                ItemError = new IOException("item failure"),
                TabError = new IOException("tab failure")
            };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsNotNull(viewModel.AddItem());
            string? itemError;
            if (itemsFirst)
            {
                Assert.IsFalse(viewModel.TryFlushPendingItems(out itemError));
                Assert.IsFalse(viewModel.TryCreateTab("Work", out _));
            }
            else
            {
                Assert.IsFalse(viewModel.TryCreateTab("Work", out _));
                Assert.IsFalse(viewModel.TryFlushPendingItems(out itemError));
            }

            Assert.IsNotNull(itemError);
            Assert.AreEqual(itemError, viewModel.PersistenceError);
            service.ItemError = null;
            Assert.IsTrue(viewModel.TryFlushPendingItems(out _));
            Assert.IsNotNull(viewModel.PersistenceError);
            Assert.AreEqual("Checklist tabs could not be saved.", viewModel.PersistenceError);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void FailuresTakePrecedenceOverWarnings(bool itemFails, bool itemsFirst)
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService
            {
                ItemError = itemFails ? new IOException("item failure") : null,
                TabError = itemFails ? null : new IOException("tab failure"),
                ItemWarning = "item warning",
                TabWarning = "tab warning"
            };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsNotNull(viewModel.AddItem());
            if (itemsFirst)
            {
                Assert.AreEqual(!itemFails, viewModel.TryFlushPendingItems(out _));
                Assert.AreEqual(itemFails, viewModel.TryCreateTab("Work", out _));
            }
            else
            {
                Assert.AreEqual(itemFails, viewModel.TryCreateTab("Work", out _));
                Assert.AreEqual(!itemFails, viewModel.TryFlushPendingItems(out _));
            }

            Assert.IsNotNull(viewModel.PersistenceError);
            Assert.AreEqual(itemFails ? "Checklist content could not be saved." : "Checklist tabs could not be saved.", viewModel.PersistenceError);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void WarningsRemainIndependentAndUseItemFirstPrecedence()
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService
            {
                ItemWarning = "item warning",
                TabWarning = "tab warning"
            };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsNotNull(viewModel.AddItem());
            Assert.IsTrue(viewModel.TryFlushPendingItems(out _));
            Assert.IsTrue(viewModel.TryCreateTab("Work", out _));
            Assert.AreEqual("item warning", viewModel.PersistenceError);

            service.ItemWarning = null;
            viewModel.Items[0].Text = "Updated item";
            Assert.IsTrue(viewModel.TryFlushPendingItems(out _));
            Assert.AreEqual("tab warning", viewModel.PersistenceError);

            service.TabWarning = null;
            Assert.IsTrue(viewModel.TryCreateTab("Personal", out _));
            Assert.IsNull(viewModel.PersistenceError);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SuccessfulSaveStillClearsTheInitialLoadNotice(bool saveItems)
    {
        RunOnDispatcher(dispatcher =>
        {
            var service = new ControlledChecklistContentService
            {
                LoadIssues = [new ChecklistContentIssue("checklist.json", "load notice")]
            };
            using var viewModel = CreateViewModel(service, dispatcher);
            Assert.IsNotNull(viewModel.PersistenceError);
            StringAssert.Contains(viewModel.PersistenceError, "load notice");
            Assert.IsTrue(viewModel.RecoveryIssuesFoundThisRun);

            if (saveItems)
            {
                Assert.IsNotNull(viewModel.AddItem());
                Assert.IsTrue(viewModel.TryFlushPendingItems(out _));
            }
            else
            {
                Assert.IsTrue(viewModel.TryCreateTab("Work", out _));
            }

            Assert.IsNull(viewModel.PersistenceError);
            Assert.IsTrue(viewModel.RecoveryIssuesFoundThisRun);
            return Task.CompletedTask;
        });
    }

    private static ChecklistWindowViewModel CreateViewModel(
        IChecklistContentService service, Dispatcher dispatcher)
        => new(service, dispatcher, action => action(), ghostModeEnabled: false);

    private sealed class ControlledChecklistContentService : IChecklistContentService
    {
        public IOException? ItemError { get; set; }
        public IOException? TabError { get; set; }
        public string? ItemWarning { get; set; }
        public string? TabWarning { get; set; }
        public IReadOnlyList<ChecklistContentIssue> LoadIssues { get; set; } = [];

        public ChecklistContentLoadResult LoadItems() => new([], [], LoadIssues);

        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (ItemError is not null)
                throw ItemError;
            return new(ItemWarning);
        }

        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs)
        {
            if (TabError is not null)
                throw TabError;
            return new(TabWarning);
        }
    }

    private static void RunOnDispatcher(Func<Dispatcher, Task> test)
    {
        Exception? failure = null;
        Dispatcher? testDispatcher = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Volatile.Write(ref testDispatcher, dispatcher);
            dispatcher.UnhandledException += (_, args) =>
            {
                failure = args.Exception;
                args.Handled = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await test(dispatcher);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };

        // DispatcherTimer needs a message pump, but no STA thread, Application or Window.
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            Volatile.Read(ref testDispatcher)?.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(1));
            Assert.Fail("The scheduler test dispatcher did not finish within 10 seconds.");
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
