using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class GlobalHotkeysServiceTests
{
    [TestMethod]
    public void RegistrationUsesNoRepeatAndNormalizedDuplicatesNeverReachWindows() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        var nativeCalls = 0;
        uint registeredFlags = 0;
        var callbacks = 0;
        using var service = new GlobalHotkeysService(window, (_, modifiers, _) =>
        {
            nativeCalls++;
            registeredFlags = modifiers;
            return true;
        }, _ => true);
        try
        {
            var registration = service.Register("Noted", "shift+control", "return", () => callbacks++).Registration!;
            Assert.AreEqual("Ctrl+Shift+Enter", registration.Combination);
            Assert.AreEqual(0x4006u, registeredFlags);
            Assert.IsFalse(service.Register("Other", "Ctrl+Shift", "Enter", () => { }).Success);
            Assert.AreEqual(1, nativeCalls);
            Assert.IsTrue(service.DispatchHotkey(registration.Id));
            Assert.AreEqual(1, callbacks);
            Assert.IsTrue(service.Unregister(registration.Id).Success);
            Assert.IsFalse(service.DispatchHotkey(registration.Id));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void RejectedRegistrationAndReplacementRetainOriginalBinding() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        var reject = false;
        var releases = new List<int>();
        using var service = new GlobalHotkeysService(window, (_, _, _) => !reject, id =>
        {
            releases.Add(id);
            return true;
        });
        try
        {
            var original = service.Register("Noted", "Alt", "N", () => { }).Registration!;
            reject = true;
            var result = service.Replace("Noted", original, "Alt", "Q", () => { });
            Assert.IsFalse(result.Success);
            Assert.AreSame(original, result.ActiveRegistration);
            Assert.IsNull(result.AdditionalActiveRegistration);
            Assert.AreEqual(0, releases.Count);
            Assert.IsTrue(service.DispatchHotkey(original.Id));
            StringAssert.Contains(result.Description, "Windows rejected");
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void SuccessfulReplacementReleasesOldRegistrationAndDisposeReleasesNewOneOnce() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        var releases = new List<int>();
        using var service = new GlobalHotkeysService(window, (_, _, _) => true, id =>
        {
            releases.Add(id);
            return true;
        });
        try
        {
            var original = service.Register("Noted", "Alt", "N", () => { }).Registration!;
            var result = service.Replace("Noted", original, "Alt", "Q", () => { });
            Assert.IsTrue(result.Success);
            Assert.IsFalse(service.DispatchHotkey(original.Id));
            var replacement = result.ActiveRegistration!;
            Assert.IsTrue(service.DispatchHotkey(replacement.Id));
            service.Dispose();
            service.Dispose();
            CollectionAssert.AreEqual(new[] { original.Id, replacement.Id }, releases);
            Assert.IsFalse(service.DispatchHotkey(replacement.Id));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void FailedOldReleaseRollsBackNewRegistration() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        var releases = new List<int>();
        using var service = new GlobalHotkeysService(window, (_, _, _) => true, id =>
        {
            releases.Add(id);
            return releases.Count != 1;
        });
        try
        {
            var original = service.Register("Noted", "Alt", "N", () => { }).Registration!;
            var result = service.Replace("Noted", original, "Alt", "Q", () => { });
            Assert.IsFalse(result.Success);
            Assert.AreSame(original, result.ActiveRegistration);
            Assert.IsNull(result.AdditionalActiveRegistration);
            Assert.IsTrue(service.DispatchHotkey(original.Id));
            Assert.IsFalse(service.DispatchHotkey(releases[1]));
            Assert.IsTrue(service.Register("Other", "Alt", "Q", () => { }).Success);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void FailedRollbackTracksBothBindingsAndBlocksFurtherReplacement() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        new WindowInteropHelper(window).EnsureHandle();
        var failRelease = true;
        var nativeCalls = 0;
        using var service = new GlobalHotkeysService(window, (_, _, _) => { nativeCalls++; return true; }, _ => !failRelease);
        try
        {
            var original = service.Register("Noted", "Alt", "N", () => { }).Registration!;
            var result = service.Replace("Noted", original, "Alt", "Q", () => { });
            Assert.IsFalse(result.Success);
            Assert.AreSame(original, result.ActiveRegistration);
            Assert.IsNotNull(result.AdditionalActiveRegistration);
            Assert.IsTrue(service.DispatchHotkey(original.Id));
            Assert.IsTrue(service.DispatchHotkey(result.AdditionalActiveRegistration.Id));
            Assert.IsFalse(service.Replace("Noted", original, "Alt", "P", () => { }).Success);
            Assert.AreEqual(2, nativeCalls);
            failRelease = false;
            Assert.IsTrue(service.UnregisterAll().All(result => result.Success));
            Assert.IsFalse(service.DispatchHotkey(original.Id));
            Assert.IsFalse(service.DispatchHotkey(result.AdditionalActiveRegistration.Id));
        }
        finally { window.Close(); }
    });
}
