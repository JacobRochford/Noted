using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Windows.Threading;
using Microsoft.Windows.AppNotifications.Builder;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistPersistentNotificationTests
{
    private IntPtr _activationContext;
    private nuint _activationCookie;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActivationContext
    {
        internal uint Size;
        internal uint Flags;
        internal string Source;
        internal ushort Architecture;
        internal ushort Language;
        internal string? AssemblyDirectory;
        internal IntPtr ResourceName;
        internal string? ApplicationName;
        internal IntPtr Module;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateActCtxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateActCtx(ref ActivationContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivateActCtx(IntPtr context, out nuint cookie);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeactivateActCtx(uint flags, nuint cookie);
    [DllImport("kernel32.dll")]
    private static extern void ReleaseActCtx(IntPtr context);

    [TestInitialize]
    public void ActivateApplicationManifest()
    {
        // testhost.exe has no SDK manifest; use the unchanged SDK-generated application resource.
        var context = new ActivationContext
        {
            Size = (uint)Marshal.SizeOf<ActivationContext>(),
            Flags = 0x8,
            Source = Path.Combine(AppContext.BaseDirectory, "Noted.exe"),
            ResourceName = new IntPtr(1)
        };
        _activationContext = CreateActCtx(ref context);
        if (_activationContext == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!ActivateActCtx(_activationContext, out _activationCookie))
        {
            var error = Marshal.GetLastWin32Error();
            ReleaseActCtx(_activationContext);
            _activationContext = IntPtr.Zero;
            throw new Win32Exception(error);
        }
    }

    [TestCleanup]
    public void DeactivateApplicationManifest()
    {
        if (_activationContext == IntPtr.Zero || _activationContext == new IntPtr(-1)) return;
        if (_activationCookie != 0) DeactivateActCtx(0, _activationCookie);
        ReleaseActCtx(_activationContext);
    }

    private static readonly DateTimeOffset s_now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void StandardPayloadRemainsIdenticalToTheExistingBuilder()
    {
        var content = ChecklistNotificationContent.Create([Reminder(ChecklistNotificationStyle.Standard)]);
        var original = new AppNotificationBuilder().AddText(content.Title).AddText(content.Text);
        foreach (var argument in content.Arguments) original.AddArgument(argument.Key, argument.Value);
        var baseline = original.BuildNotification().Payload;
        Assert.AreEqual(baseline, content.BuildNotification().Payload);
    }

    [TestMethod]
    [DataRow(ChecklistNotificationStyle.Standard)]
    [DataRow(ChecklistNotificationStyle.Persistent)]
    public void RepeatToastsUseOneWindowsIdentityAndKeepTheirSlotInActivation(ChecklistNotificationStyle style)
    {
        var id = Guid.NewGuid();
        var first = Reminder(style) with { RepeatDelivery = new(id, s_now) };
        var next = first with { RepeatDelivery = new(id, s_now.AddMinutes(10)) };
        var original = ChecklistNotificationContent.Create([first]).BuildNotification();
        var replacement = ChecklistNotificationContent.Create([next]).BuildNotification();

        Assert.AreEqual(original.Tag, replacement.Tag);
        Assert.AreEqual(original.Group, replacement.Group);
        Assert.IsFalse(replacement.SuppressDisplay, $"Replacement for {style} should allow a banner.");
        var activation = ChecklistNotifications.ReadRepeatActivation(replacement);
        Assert.AreEqual(new ChecklistReminderActivation(next.TaskId, null, next.RepeatDelivery), activation, replacement.Payload);
        Assert.AreEqual(activation, ChecklistReminderActivation.Deserialize(activation!.Serialize()));
        Assert.ThrowsExactly<ArgumentException>(() => ChecklistNotificationContent.Create([first, next]));
        Assert.IsFalse(ChecklistReminderActivation.TryParse(new Dictionary<string, string>
        {
            ["action"] = "checklistReminder", ["taskId"] = first.TaskId.ToString(), ["seriesId"] = id.ToString()
        }, out _));
    }

    [TestMethod]
    public void PersistentReplacementPreservesTheScenarioAndActionWithoutRestartingAnOutstandingAlarm()
    {
        var reminder = Reminder(ChecklistNotificationStyle.Persistent) with { RepeatDelivery = new(Guid.NewGuid(), s_now) };
        var content = ChecklistNotificationContent.Create([reminder], ChecklistWindowsNotificationSound.RepeatingAlarm);
        var ringing = ChecklistNotifications.SuppressReplacementAlarm(content, hasOutstanding: false).BuildNotification();
        var replacement = ChecklistNotifications.SuppressReplacementAlarm(content, hasOutstanding: true).BuildNotification();
        var original = XElement.Parse(ringing.Payload);
        var toast = XElement.Parse(replacement.Payload);
        Assert.AreEqual("true", (string?)original.Element("audio")?.Attribute("loop"));
        Assert.AreEqual("true", (string?)toast.Element("audio")?.Attribute("silent"));
        Assert.AreEqual("reminder", (string?)toast.Attribute("scenario"));
        Assert.AreEqual(original.Element("actions")?.ToString(), toast.Element("actions")?.ToString());
        Assert.AreEqual(ringing.Tag, replacement.Tag);
        Assert.AreEqual(ringing.Group, replacement.Group);
        Assert.AreEqual(ChecklistNotifications.ReadRepeatActivation(ringing), ChecklistNotifications.ReadRepeatActivation(replacement));
    }

    [TestMethod]
    public void PersistentPayloadHasReminderScenarioAndTaskSpecificOpenButton()
    {
        var reminder = Reminder(ChecklistNotificationStyle.Persistent);
        var content = ChecklistNotificationContent.Create([reminder]);
        var toast = XElement.Parse(content.BuildNotification().Payload);
        Assert.AreEqual("reminder", (string?)toast.Attribute("scenario"));
        var button = toast.Element("actions")!.Elements("action").Single();
        Assert.AreEqual("Open task", (string?)button.Attribute("content"));
        Assert.AreEqual((string?)toast.Attribute("launch"), (string?)button.Attribute("arguments"));
        Assert.IsTrue(ChecklistReminderActivation.TryParse(content.Arguments, out var activation));
        Assert.AreEqual(reminder.TaskId, activation!.TaskId);
        Assert.AreEqual(activation, ChecklistReminderActivation.Deserialize(activation.Serialize()));
        Assert.AreEqual("ms-winsoundevent:Notification.Reminder", (string?)toast.Element("audio")!.Attribute("src"));
        Assert.AreNotEqual("true", (string?)toast.Element("audio")!.Attribute("loop"));
        Assert.IsFalse(toast.Descendants("action").Any(action => (string?)action.Attribute("activationType") == "system"));
    }

    [TestMethod]
    public void GroupedPersistentPayloadKeepsExistingBatchActivationAndEscapesTaskText()
    {
        var reminders = new[] { Reminder(ChecklistNotificationStyle.Persistent), Reminder(ChecklistNotificationStyle.Persistent) };
        var content = ChecklistNotificationContent.Create(reminders);
        var toast = XElement.Parse(content.BuildNotification().Payload);
        var button = toast.Element("actions")!.Elements("action").Single();
        Assert.AreEqual("Open Tasks", (string?)button.Attribute("content"));
        Assert.AreEqual((string?)toast.Attribute("launch"), (string?)button.Attribute("arguments"));
        Assert.IsTrue(ChecklistReminderActivation.TryParse(content.Arguments, out var activation));
        Assert.AreEqual(s_now, activation!.BatchUtc); Assert.IsNull(activation.TaskId);
        var individual = ChecklistNotificationContent.Create([reminders[0] with { Text = "<task> & details" }]);
        var escaped = XElement.Parse(individual.BuildNotification().Payload);
        Assert.AreEqual("<task> & details", escaped.Descendants("text").Last().Value);
    }

    [TestMethod]
    [DataRow(0, "ms-winsoundevent:Notification.Default")]
    [DataRow(2, "ms-winsoundevent:Notification.Reminder")]
    [DataRow(3, "ms-winsoundevent:Notification.Looping.Alarm")]
    public void PersistentBuiltInSoundModesUseSupportedXml(int soundValue, string source)
    {
        var sound = (ChecklistWindowsNotificationSound)soundValue;
        var content = ChecklistNotificationContent.Create([Reminder(ChecklistNotificationStyle.Persistent)], sound);
        var toast = XElement.Parse(content.BuildNotification().Payload);
        var audio = toast.Element("audio")!;
        Assert.AreEqual(source, (string?)audio.Attribute("src"));
        if (sound == ChecklistWindowsNotificationSound.RepeatingAlarm)
        {
            Assert.AreEqual("true", (string?)audio.Attribute("loop"));
            Assert.AreEqual("long", (string?)toast.Attribute("duration"));
        }
        else Assert.AreNotEqual("true", (string?)audio.Attribute("loop"));
    }

    [TestMethod]
    public void SilentPersistentModeDoesNotRequestAudio()
    {
        var content = ChecklistNotificationContent.Create([Reminder(ChecklistNotificationStyle.Persistent)], ChecklistWindowsNotificationSound.Silent);
        var audio = XElement.Parse(content.BuildNotification().Payload).Element("audio")!;
        Assert.AreEqual("true", (string?)audio.Attribute("silent"));
        Assert.IsNull(audio.Attribute("src")); Assert.IsNull(audio.Attribute("loop"));
    }

    [TestMethod]
    public void UnsupportedAndMixedRequestsFailRatherThanPartiallySubmit()
    {
        int sent = 0;
        using var adapter = new ChecklistNotifications(_ => { }, _ => { sent++; return true; });
        Assert.IsFalse(adapter.TrySubmit([]));
        Assert.IsFalse(adapter.TrySubmit([Reminder(ChecklistNotificationStyle.Urgent)]));
        Assert.IsFalse(adapter.TrySubmit([Reminder((ChecklistNotificationStyle)99)]));
        Assert.IsFalse(adapter.TrySubmit([Reminder(ChecklistNotificationStyle.Standard), Reminder(ChecklistNotificationStyle.Persistent)]));
        Assert.IsFalse(adapter.TrySubmit([Reminder(ChecklistNotificationStyle.Persistent)], (ChecklistWindowsNotificationSound)99));
        Assert.AreEqual(0, sent);
        Assert.IsTrue(adapter.TrySubmit([Reminder(ChecklistNotificationStyle.Persistent)]));
        Assert.AreEqual(1, sent);
    }

    [TestMethod]
    public void PersistentAdapterFailuresAreNotSuccessfulSubmissions()
    {
        var request = new[] { Reminder(ChecklistNotificationStyle.Persistent) };
        using var rejected = new ChecklistNotifications(_ => { }, _ => false);
        using var throwing = new ChecklistNotifications(_ => { }, _ => throw new IOException("Windows submission failed"));
        Assert.IsFalse(rejected.TrySubmit(request)); Assert.IsFalse(throwing.TrySubmit(request));
    }

    [TestMethod]
    public void MixedStyleGroupsHaveDistinctNavigationStampsAndUrgentStaysPending() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Model(store);
        foreach (var style in new[] { ChecklistNotificationStyle.Standard, ChecklistNotificationStyle.Persistent, ChecklistNotificationStyle.Urgent })
        {
            model.Items.Add(new ChecklistItem { Text = "one", NotificationStyle = style, ReminderAtUtc = s_now });
            model.Items.Add(new ChecklistItem { Text = "two", NotificationStyle = style, ReminderAtUtc = s_now });
        }
        var contents = new List<ChecklistNotificationContent>();
        using var adapter = new ChecklistNotifications(_ => { }, content => { contents.Add(content); return true; });
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher,
            request => adapter.TrySubmit(request), new Clock());
        Assert.AreEqual(2, contents.Count);
        var stamps = new HashSet<DateTimeOffset>();
        foreach (var content in contents)
        {
            Assert.IsTrue(ChecklistReminderActivation.TryParse(content.Arguments, out var activation));
            Assert.IsTrue(stamps.Add(activation!.BatchUtc!.Value));
            var affected = model.Items.Where(item => item.ReminderHandledAtUtc == activation.BatchUtc).ToArray();
            Assert.AreEqual(2, affected.Length);
            Assert.IsTrue(affected.All(item => item.NotificationStyle == content.Style));
        }
        Assert.IsTrue(model.Items.Where(item => item.NotificationStyle == ChecklistNotificationStyle.Urgent)
            .All(item => item.ReminderHandledAtUtc is null && item.ReminderOccurrence!.AcknowledgedAtUtc is null));
        scheduler.CheckNow(); Assert.AreEqual(2, contents.Count);
        using var restartedModel = Model(store);
        using var restarted = new ChecklistReminderScheduler(restartedModel, Dispatcher.CurrentDispatcher,
            request => { Assert.Fail("Accepted Windows reminders must not repeat after restart."); return false; }, new Clock());
    });

    [TestMethod]
    public void FailedPersistentDeliveryRetriesWithoutRepeatingStandard() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Model(store); var clock = new Clock();
        model.Items.Add(new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Standard, ReminderAtUtc = s_now });
        model.Items.Add(new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Persistent, ReminderAtUtc = s_now });
        int standard = 0, persistent = 0; bool accept = false;
        using var adapter = new ChecklistNotifications(_ => { }, content =>
        {
            if (content.Style == ChecklistNotificationStyle.Standard) { standard++; return true; }
            persistent++; return accept;
        });
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher,
            request => adapter.TrySubmit(request), clock);
        Assert.AreEqual(1, standard); Assert.AreEqual(1, persistent);
        Assert.IsNull(model.Items[1].ReminderHandledAtUtc);
        scheduler.CheckNow(); Assert.AreEqual(1, persistent);
        accept = true; clock.Advance(); scheduler.CheckNow();
        Assert.AreEqual(1, standard); Assert.AreEqual(2, persistent);
        Assert.IsNotNull(model.Items[1].ReminderHandledAtUtc);
        Assert.IsNull(model.Items[1].ReminderOccurrence);
    });

    [TestMethod]
    [DataRow(ChecklistNotificationStyle.Standard)]
    [DataRow(ChecklistNotificationStyle.Persistent)]
    public void WindowsPreviewCannotBecomeARealTaskActivation(ChecklistNotificationStyle style)
    {
        var content = ChecklistNotificationContent.CreatePreview(style, new());
        var toast = XElement.Parse(content.BuildNotification().Payload);
        Assert.IsFalse(ChecklistReminderActivation.TryParse(content.Arguments, out _));
        Assert.IsFalse(content.Arguments.ContainsKey("taskId"));
        Assert.IsTrue(toast.Descendants("text").Any(text => text.Value.StartsWith("[Preview]")));
        if (style == ChecklistNotificationStyle.Persistent)
            Assert.AreEqual((string?)toast.Attribute("launch"), (string?)toast.Descendants("action").Single().Attribute("arguments"));
    }

    [TestMethod]
    public void ExplicitStandardSoundOverrideDoesNotChangeDefaultBehavior()
    {
        var defaultToast = XElement.Parse(ChecklistNotificationContent.Create([Reminder(ChecklistNotificationStyle.Standard)]).BuildNotification().Payload);
        Assert.IsNull(defaultToast.Element("audio"));
        var silent = XElement.Parse(ChecklistNotificationContent.Create([Reminder(ChecklistNotificationStyle.Standard)], ChecklistWindowsNotificationSound.Silent).BuildNotification().Payload);
        Assert.AreEqual("true", (string?)silent.Element("audio")!.Attribute("silent"));
    }

    [TestMethod]
    public void PreviewSessionsHaveDistinctNativeIdentities()
    {
        var first = ChecklistNotifications.BuildPreviewNotification(ChecklistNotificationStyle.Persistent, new());
        var second = ChecklistNotifications.BuildPreviewNotification(ChecklistNotificationStyle.Persistent, new());
        Assert.AreNotEqual(first.Tag, second.Tag);
        Assert.AreEqual(first.Group, second.Group);
        Assert.AreEqual(16, first.Tag.Length);
    }

    private static ChecklistReminder Reminder(ChecklistNotificationStyle style) =>
        new(Guid.NewGuid(), s_now, "task", s_now, style);
    private static ChecklistWindowViewModel Model(IChecklistContentService store) =>
        new(store, Dispatcher.CurrentDispatcher, action => action(), false);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => s_now.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance() => _ticks += TimeSpan.FromSeconds(31).Ticks;
    }
    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items) { Items = items.ToArray(); return new(null); }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
