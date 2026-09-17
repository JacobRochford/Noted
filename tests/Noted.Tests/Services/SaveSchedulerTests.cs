using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class SaveSchedulerTests
{
    // Covers the shared content/recovery scheduler. Scratchpad's separate 500 ms
    // window-settings DispatcherTimer is intentionally outside this test scope.
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_timerTolerance = TimeSpan.FromMilliseconds(20);

    [TestMethod]
    public void ExplicitFlushSavesOnlyTheLatestSnapshotOnce()
    {
        RunOnDispatcher(dispatcher =>
        {
            var saved = new List<string>();
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), snapshot =>
                {
                    saved.Add(snapshot);
                    return PersistenceSaveResult.Succeeded();
                });

            Assert.IsTrue(scheduler.TryFlush(out var emptyError));
            Assert.IsNull(emptyError);
            scheduler.Schedule("first");
            scheduler.Schedule("latest");

            Assert.IsTrue(scheduler.TryFlush(out var error));
            Assert.IsNull(error);
            Assert.IsTrue(scheduler.TryFlush(out emptyError));
            Assert.IsNull(emptyError);
            CollectionAssert.AreEqual(new[] { "latest" }, saved);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void LaterEditRestartsTheQuietPeriodAndSavesTheLatestSnapshot()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var quietPeriod = TimeSpan.FromMilliseconds(300);
            var saved = new TaskCompletionSource<(string Snapshot, TimeSpan Elapsed)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sinceLastEdit = new Stopwatch();
            var saveCount = 0;
            using var scheduler = new SaveScheduler<string>(
                dispatcher, quietPeriod, TimeSpan.FromMinutes(1), snapshot =>
                {
                    saveCount++;
                    saved.TrySetResult((snapshot, sinceLastEdit.Elapsed));
                    return PersistenceSaveResult.Succeeded();
                });

            scheduler.Schedule("first");
            // Keep the owning thread occupied so the first tick cannot race this edit.
            Thread.Sleep(TimeSpan.FromMilliseconds(150));
            sinceLastEdit.Start();
            scheduler.Schedule("latest");
            Assert.AreEqual(0, saveCount);

            var result = await saved.Task.WaitAsync(s_timeout);

            Assert.AreEqual("latest", result.Snapshot);
            Assert.IsTrue(result.Elapsed >= quietPeriod - s_timerTolerance,
                $"Saved after {result.Elapsed.TotalMilliseconds:F0} ms; the quiet period must restart after the edit.");
            Assert.IsTrue(scheduler.TryFlush(out _));
            Assert.AreEqual(1, saveCount);
        });
    }

    [TestMethod]
    public void ContinuedEditsCannotPostponeSavingPastTheMaximumDelay()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var maximumDelay = TimeSpan.FromMilliseconds(250);
            var saved = new TaskCompletionSource<(int Snapshot, int Latest, TimeSpan Elapsed)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var elapsed = Stopwatch.StartNew();
            var latest = 1;
            using var scheduler = new SaveScheduler<int>(
                dispatcher, TimeSpan.FromSeconds(30), maximumDelay, snapshot =>
                {
                    saved.TrySetResult((snapshot, latest, elapsed.Elapsed));
                    return PersistenceSaveResult.Succeeded();
                });
            var edits = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            edits.Tick += (_, _) => scheduler.Schedule(++latest);

            scheduler.Schedule(latest);
            edits.Start();
            try
            {
                // The quiet period exceeds this timeout, and edits continue until a save.
                // Only the maximum-delay policy can complete the operation in time.
                var result = await saved.Task.WaitAsync(s_timeout);
                Assert.IsTrue(result.Latest > 1, "There must be edits after the initial snapshot.");
                Assert.AreEqual(result.Latest, result.Snapshot);
                Assert.IsTrue(result.Elapsed >= maximumDelay - s_timerTolerance,
                    "The maximum-delay timer must not save immediately.");
            }
            finally
            {
                edits.Stop();
            }
        });
    }

    [TestMethod]
    public void FailedFlushRetainsTheSnapshotAndRetriesWithoutAnotherEdit()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var attempts = new List<string>();
            var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(300), snapshot =>
                {
                    attempts.Add(snapshot);
                    if (attempts.Count == 1)
                        return PersistenceSaveResult.Failed("File is locked.", "Backup unavailable.");

                    retried.TrySetResult();
                    return PersistenceSaveResult.Succeeded();
                });

            scheduler.Schedule("unsaved content");
            Assert.IsFalse(scheduler.TryFlush(out var error));
            Assert.AreEqual("File is locked.", error);
            Assert.AreEqual(error, scheduler.LastError);
            Assert.AreEqual("Backup unavailable.", scheduler.LastWarning);

            await retried.Task.WaitAsync(s_timeout);

            CollectionAssert.AreEqual(new[] { "unsaved content", "unsaved content" }, attempts);
            Assert.IsNull(scheduler.LastError);
            Assert.IsNull(scheduler.LastWarning);
            Assert.IsTrue(scheduler.TryFlush(out error));
            Assert.IsNull(error);
            Assert.AreEqual(2, attempts.Count);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RevisionScheduledDuringSaveSurvivesTheOlderSaveResult(bool firstSaveSucceeds)
    {
        RunOnDispatcher(async dispatcher =>
        {
            var attempts = new List<string>();
            var newerSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SaveScheduler<string>? scheduler = null;
            using (scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(300), snapshot =>
                {
                    attempts.Add(snapshot);
                    if (attempts.Count == 1)
                    {
                        scheduler!.Schedule("newer revision");
                        return firstSaveSucceeds
                            ? PersistenceSaveResult.Succeeded()
                            : PersistenceSaveResult.Failed("Older revision failed.");
                    }

                    newerSaved.TrySetResult();
                    return PersistenceSaveResult.Succeeded();
                }))
            {
                scheduler.Schedule("older revision");
                Assert.AreEqual(firstSaveSucceeds, scheduler.TryFlush(out var error));
                Assert.AreEqual(firstSaveSucceeds ? null : "Older revision failed.", error);

                await newerSaved.Task.WaitAsync(s_timeout);

                CollectionAssert.AreEqual(new[] { "older revision", "newer revision" }, attempts);
                Assert.IsNull(scheduler.LastError);
                Assert.IsTrue(scheduler.TryFlush(out _));
                Assert.AreEqual(2, attempts.Count);
            }
        });
    }

    [TestMethod]
    public void SuccessfulSaveWithWarningClearsPendingWorkAndLaterSuccessClearsTheWarning()
    {
        RunOnDispatcher(dispatcher =>
        {
            var saveCount = 0;
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), _ =>
                {
                    saveCount++;
                    return PersistenceSaveResult.Succeeded(saveCount == 1 ? "Backup unavailable." : null);
                });

            scheduler.Schedule("first");
            Assert.IsTrue(scheduler.TryFlush(out var error));
            Assert.IsNull(error);
            Assert.IsNull(scheduler.LastError);
            Assert.AreEqual("Backup unavailable.", scheduler.LastWarning);
            Assert.IsTrue(scheduler.TryFlush(out _));
            Assert.AreEqual(1, saveCount);

            scheduler.Schedule("second");
            Assert.IsTrue(scheduler.TryFlush(out _));
            Assert.IsNull(scheduler.LastWarning);
            Assert.AreEqual(2, saveCount);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void CancelPendingClearsFailureStateAndStopsRetryButAllowsNewWork()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var attempts = new List<string>();
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), snapshot =>
                {
                    attempts.Add(snapshot);
                    if (attempts.Count == 1)
                        return PersistenceSaveResult.Failed("File is locked.", "Backup unavailable.");

                    saved.TrySetResult();
                    return PersistenceSaveResult.Succeeded();
                });

            scheduler.Schedule("canceled");
            Assert.IsFalse(scheduler.TryFlush(out _));
            scheduler.CancelPending();

            Assert.IsNull(scheduler.LastError);
            Assert.IsNull(scheduler.LastWarning);
            Assert.IsTrue(scheduler.TryFlush(out var error));
            Assert.IsNull(error);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            CollectionAssert.AreEqual(new[] { "canceled" }, attempts);

            scheduler.Schedule("new work");
            await saved.Task.WaitAsync(s_timeout);
            CollectionAssert.AreEqual(new[] { "canceled", "new work" }, attempts);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisposalStopsPendingSavesAndRejectsFurtherUse(bool failBeforeDisposal)
    {
        RunOnDispatcher(async dispatcher =>
        {
            var saveCount = 0;
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), _ =>
                {
                    saveCount++;
                    return PersistenceSaveResult.Failed("File is locked.");
                });

            scheduler.Schedule("pending");
            if (failBeforeDisposal)
                Assert.IsFalse(scheduler.TryFlush(out _));
            scheduler.Dispose();
            scheduler.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => scheduler.Schedule("too late"));
            Assert.ThrowsExactly<ObjectDisposedException>(() => scheduler.TryFlush(out _));
            Assert.ThrowsExactly<ObjectDisposedException>(() => scheduler.CancelPending());
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.AreEqual(failBeforeDisposal ? 1 : 0, saveCount);
        });
    }

    [TestMethod]
    public void ZeroQuietPeriodStillSavesThroughTheDispatcher()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scheduler = new SaveScheduler<string>(
                dispatcher, TimeSpan.Zero, TimeSpan.FromSeconds(1), snapshot =>
                {
                    saved.TrySetResult(snapshot);
                    return PersistenceSaveResult.Succeeded();
                });

            scheduler.Schedule("pending");
            Assert.IsFalse(saved.Task.IsCompleted, "Schedule must not save inline, even with no quiet period.");
            Assert.AreEqual("pending", await saved.Task.WaitAsync(s_timeout));
        });
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
