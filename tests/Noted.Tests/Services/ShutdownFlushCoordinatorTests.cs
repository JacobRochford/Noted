using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ShutdownFlushCoordinatorTests
{
    [TestMethod]
    public void EmptyCoordinatorHasNoFailures()
    {
        Assert.AreEqual(0, new ShutdownFlushCoordinator().FlushAll().Count);
    }

    [TestMethod]
    public void FlushesInRegistrationOrderAndCollectsFailuresWithoutStopping()
    {
        var coordinator = new ShutdownFlushCoordinator();
        var calls = new List<string>();
        using var first = coordinator.Register("Editor", () =>
        {
            calls.Add("Editor");
            return PersistenceSaveResult.Failed("Disk is full.");
        });
        using var second = coordinator.Register("Checklist", () =>
        {
            calls.Add("Checklist");
            return PersistenceSaveResult.Succeeded("Recovered data.");
        });
        using var third = coordinator.Register("Scratchpad", () =>
        {
            calls.Add("Scratchpad");
            return new PersistenceSaveResult(false, " ", null);
        });

        var failures = coordinator.FlushAll();

        CollectionAssert.AreEqual(new[] { "Editor", "Checklist", "Scratchpad" }, calls);
        CollectionAssert.AreEqual(new[]
        {
            new ShutdownFlushFailure("Editor", "Disk is full."),
            new ShutdownFlushFailure("Scratchpad", "Pending data could not be saved.")
        }, failures.ToArray());
    }

    [TestMethod]
    public void RepeatedFlushRetriesActiveParticipantsAndDisposalIsIdempotent()
    {
        var coordinator = new ShutdownFlushCoordinator();
        var calls = 0;
        var registration = coordinator.Register("Editor", () => ++calls == 1
            ? PersistenceSaveResult.Failed("Try again.")
            : PersistenceSaveResult.Succeeded());

        Assert.AreEqual(1, coordinator.FlushAll().Count);
        Assert.AreEqual(0, coordinator.FlushAll().Count);
        registration.Dispose();
        registration.Dispose();
        Assert.AreEqual(0, coordinator.FlushAll().Count);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void DisposedParticipantInCurrentSnapshotIsSkippedAndNewParticipantWaitsUntilNextFlush()
    {
        var coordinator = new ShutdownFlushCoordinator();
        var calls = new List<string>();
        IDisposable? second = null;
        IDisposable? added = null;
        using var first = coordinator.Register("First", () =>
        {
            calls.Add("First");
            second!.Dispose();
            added ??= coordinator.Register("Added", () =>
            {
                calls.Add("Added");
                return PersistenceSaveResult.Succeeded();
            });
            return PersistenceSaveResult.Succeeded();
        });
        second = coordinator.Register("Second", () =>
        {
            Assert.Fail("A disposed participant must not be called.");
            return PersistenceSaveResult.Succeeded();
        });

        coordinator.FlushAll();
        CollectionAssert.AreEqual(new[] { "First" }, calls);
        coordinator.FlushAll();
        CollectionAssert.AreEqual(new[] { "First", "First", "Added" }, calls);
        added!.Dispose();
    }

    [TestMethod]
    public void ParticipantCanDisposeItselfDuringFlush()
    {
        var coordinator = new ShutdownFlushCoordinator();
        var calls = 0;
        IDisposable? registration = null;
        registration = coordinator.Register("Editor", () =>
        {
            calls++;
            registration!.Dispose();
            return PersistenceSaveResult.Succeeded();
        });

        coordinator.FlushAll();
        coordinator.FlushAll();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void UnexpectedExceptionPropagatesAndStopsTheCurrentFlush()
    {
        var coordinator = new ShutdownFlushCoordinator();
        var expected = new InvalidOperationException("Unexpected failure.");
        using var first = coordinator.Register("Editor", () => throw expected);
        using var second = coordinator.Register("Checklist", () =>
        {
            Assert.Fail("The existing coordinator does not catch participant exceptions.");
            return PersistenceSaveResult.Succeeded();
        });

        Assert.AreSame(expected, Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.FlushAll()));
    }

    [TestMethod]
    public void InvalidRegistrationArgumentsAreRejected()
    {
        var coordinator = new ShutdownFlushCoordinator();
        Assert.ThrowsExactly<ArgumentNullException>(() => coordinator.Register(null!, () => PersistenceSaveResult.Succeeded()));
        Assert.ThrowsExactly<ArgumentException>(() => coordinator.Register(" ", () => PersistenceSaveResult.Succeeded()));
        Assert.ThrowsExactly<ArgumentNullException>(() => coordinator.Register("Editor", null!));
    }
}
