using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistNotificationCustomizationTests
{
    private static readonly DateTimeOffset s_now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    [TestMethod]
    public void ApprovedDefaultsAndMalformedSettingsUseSafeValues()
    {
        var options = new ChecklistNotificationOptions();
        Assert.AreEqual(ChecklistAlertSize.FullScreen, options.Size);
        Assert.AreEqual(ChecklistAlertMonitor.Foreground, options.Monitor);
        Assert.IsTrue(options.PulseAccent); Assert.IsTrue(options.RepeatSound);
        Assert.AreEqual(10, options.RepeatSeconds); Assert.AreEqual(ChecklistAcknowledgment.Code, options.Acknowledgment);
        Assert.AreEqual(ChecklistUrgentSound.Alarm, options.UrgentSound);
        var normalized = (options with { HoldSeconds = -1, RepeatSeconds = 0, Acknowledgment = (ChecklistAcknowledgment)99 }).Normalize();
        Assert.AreEqual(1, normalized.HoldSeconds); Assert.AreEqual(2, normalized.RepeatSeconds);
        Assert.AreEqual(ChecklistAcknowledgment.Code, normalized.Acknowledgment);
    }
    [TestMethod]
    public void GlobalOptionsRoundTripAndBackupRetainsExternalSoundReference()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app"); var notes = directory.File("notes"); Directory.CreateDirectory(notes);
        var settings = new AppSettingsService(app);
        Assert.AreEqual(new ChecklistNotificationOptions(), settings.LoadChecklistNotificationOptions());
        var options = new ChecklistNotificationOptions { Size = ChecklistAlertSize.Percentage, ScreenPercent = 70,
            CustomSoundPath = directory.File("external.wav"), UrgentSound = ChecklistUrgentSound.Custom };
        settings.SaveChecklistNotificationOptions(options);
        Assert.AreEqual(options, new AppSettingsService(app).LoadChecklistNotificationOptions());
        var backup = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, backup.CreateOrUpdateUserBackup().Status);
        settings.SaveChecklistNotificationOptions(new());
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, backup.ScheduleUserBackupRestore().Status);
        Assert.AreEqual(FullBackupStatus.Restored, backup.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(options, new AppSettingsService(app).LoadChecklistNotificationOptions());
        Assert.IsFalse(File.Exists(options.CustomSoundPath));
    }
    [TestMethod]
    public void OccurrenceSnapshotSurvivesFailedSaveAndCannotBeWeakenedByDefaults() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var owner = Owner(store); var item = Item(); owner.Items.Add(item);
        var request = Request(item, new() { Acknowledgment = ChecklistAcknowledgment.Code });
        store.Fail = true; Assert.IsFalse(owner.TryCaptureUrgentOptions([request], out _));
        Assert.AreEqual(ChecklistAcknowledgment.Code, item.ReminderOccurrence!.Options!.Acknowledgment);
        store.Fail = false;
        Assert.IsTrue(owner.TryCaptureUrgentOptions([request with { Options = new() { Acknowledgment = ChecklistAcknowledgment.Button } }], out _));
        using var restored = Owner(store);
        Assert.AreEqual(ChecklistAcknowledgment.Code, restored.Items.Single().ReminderOccurrence!.Options!.Acknowledgment);
    });
    [TestMethod]
    public void CodeAcknowledgmentRequiresExactStableCodeAndExplicitConfirmation() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = Item(); owner.Items.Add(item);
        using var model = Model(owner); model.TryAccept([Request(item)]); var row = model.Entries.Single();
        var code = item.ReminderOccurrence!.ConfirmationCode;
        Assert.IsFalse(model.TryAcknowledge(row)); row.ConfirmationInput = "xxxx"; Assert.IsFalse(model.TryAcknowledge(row));
        row.ConfirmationInput = code; model.Refresh();
        var other = Item(); owner.Items.Add(other); model.TryAccept([Request(other)]);
        Assert.AreSame(row, model.Entries[0]); Assert.AreEqual(code, row.ConfirmationInput);
        Assert.IsNull(item.ReminderOccurrence.AcknowledgedAtUtc);
        Assert.IsTrue(model.TryAcknowledge(row)); Assert.IsFalse(item.IsChecked); Assert.AreEqual(1, model.Entries.Count);
    });
    [TestMethod]
    public void HoldRequiresContinuousDurationAndCancelsBeforeASecondAttempt() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = Item();
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new() { Acknowledgment = ChecklistAcknowledgment.Hold, HoldSeconds = 2 } };
        owner.Items.Add(item); var clock = new Clock(); using var model = new ChecklistUrgentRemindersViewModel(owner, Dispatcher.CurrentDispatcher, clock);
        model.TryAccept([Request(item)]); var row = model.Entries.Single();
        Assert.IsFalse(model.TryAcknowledge(row)); model.BeginHold(row); clock.Advance(1); Assert.IsFalse(model.FinishHold(row));
        model.BeginHold(row); clock.Advance(3); model.CancelHolds(); Assert.IsFalse(model.FinishHold(row));
        model.BeginHold(row); clock.Advance(2); Assert.IsTrue(model.FinishHold(row)); Assert.IsFalse(item.IsChecked);
    });
    [TestMethod]
    public void GroupAudioDoesNotBurstAndSilenceDoesNotAcknowledge() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var first = Item(); var second = Item(); owner.Items.Add(first); owner.Items.Add(second);
        using var model = Model(owner); model.TryAccept([Request(first), Request(second)]); var clock = new Clock(); int played = 0;
        using var audio = new ChecklistUrgentReminderAudio(model, Dispatcher.CurrentDispatcher, () => true, clock, _ => played++);
        audio.CheckNow(); Assert.AreEqual(1, played); clock.Advance(1); audio.CheckNow(); Assert.AreEqual(1, played);
        clock.Advance(9); audio.CheckNow(); Assert.AreEqual(2, played);
        foreach (var row in model.Entries.ToArray()) Assert.IsTrue(model.TrySilence(row));
        clock.Advance(30); audio.CheckNow(); Assert.AreEqual(2, played);
        Assert.AreEqual(2, model.Entries.Count); Assert.IsNull(first.ReminderOccurrence!.AcknowledgedAtUtc);
        var third = Item(); owner.Items.Add(third); model.TryAccept([Request(third)]); audio.CheckNow(); Assert.AreEqual(3, played);
    });
    [TestMethod]
    public void AudioDefersOnUnavailableDesktopAndHonorsOneShotMode() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = Item();
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new() { RepeatSound = false } }; owner.Items.Add(item);
        using var model = Model(owner); model.TryAccept([Request(item)]); var clock = new Clock(); bool allowed = false; int count = 0;
        using var audio = new ChecklistUrgentReminderAudio(model, Dispatcher.CurrentDispatcher, () => allowed, clock, _ => count++);
        audio.CheckNow(); Assert.AreEqual(0, count); allowed = true; audio.CheckNow(); Assert.AreEqual(1, count);
        clock.Advance(100); audio.CheckNow(); Assert.AreEqual(1, count);
        audio.Dispose(); audio.CheckNow(); Assert.AreEqual(1, count);
    });
    [TestMethod]
    public void BuiltInWavIsAvailableAndCustomValidationRejectsInvalidData() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var resource = Application.GetResourceStream(new Uri("/Noted;component/Resources/ReminderAlarm.wav", UriKind.Relative))!.Stream;
        var path = directory.File("sound.wav"); using (var output = File.Create(path)) resource.CopyTo(output);
        Assert.AreEqual(1d, ChecklistUrgentReminderAudio.ValidateWav(path));
        File.WriteAllText(path, "invalid WAV"); Assert.ThrowsExactly<IOException>(() => ChecklistUrgentReminderAudio.ValidateWav(path));
    });
    [TestMethod]
    public void ReminderDialogKeepsStandardDefaultAndOffersOverridesWithoutSaving() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistReminderDialog(null, null);
        try
        {
            Assert.AreEqual(ChecklistNotificationStyle.Standard, dialog.NotificationStyle); Assert.IsNull(dialog.NotificationOptions);
            ((ComboBox)dialog.FindName("StyleChoice")).SelectedIndex = 2;
            ((CheckBox)dialog.FindName("CustomOptions")).IsChecked = true;
            Assert.AreEqual(ChecklistNotificationStyle.Urgent, dialog.NotificationStyle);
            Assert.AreEqual(ChecklistAcknowledgment.Code, dialog.NotificationOptions!.Acknowledgment);
        }
        finally { dialog.Close(); }
    });
    [TestMethod]
    public void PreviewWindowCanCloseImmediatelyWithoutAcknowledgingSyntheticTask() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = Item(); owner.Items.Add(item);
        using var model = new ChecklistUrgentRemindersViewModel(owner, Dispatcher.CurrentDispatcher, new Clock(), isPreview: true);
        model.TryAccept([Request(item)]); var window = new ChecklistUrgentReminderWindow(model);
        window.Close();
        Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc); Assert.AreEqual(1, model.Entries.Count);
    });
    [TestMethod]
    public void PerReminderOverrideAndSnapshotRoundTripAndReschedulingClearsSnapshot() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var owner = Owner(store); var item = Item();
        item.NotificationOptions = new() { Acknowledgment = ChecklistAcknowledgment.Hold, Monitor = ChecklistAlertMonitor.All };
        owner.Items.Add(item); Assert.IsTrue(owner.TryCaptureUrgentOptions([Request(item, item.NotificationOptions)], out _));
        var data = JsonSerializer.Deserialize<ChecklistItemState[]>(JsonSerializer.Serialize(store.Items))!;
        using var restored = Owner(new Store { Items = data }); var loaded = restored.Items.Single();
        Assert.AreEqual(item.NotificationOptions, loaded.NotificationOptions);
        Assert.AreEqual(item.NotificationOptions, loaded.ReminderOccurrence!.Options);
        var old = loaded.ReminderOccurrence.Id; loaded.ReminderAtUtc = s_now.AddHours(1);
        Assert.AreNotEqual(old, loaded.ReminderOccurrence!.Id); Assert.IsNull(loaded.ReminderOccurrence.Options);
    });
    [TestMethod]
    public void AlertBoundsRespectSelectedMonitorAndDpiWithoutSpanningDisplays()
    {
        var monitor = new Rect(-1920, 0, 1920, 1080);
        Assert.AreEqual(monitor, ChecklistUrgentReminderWindow.CalculateBounds(monitor, 1.5, 1.5, new(), false));
        var percent = ChecklistUrgentReminderWindow.CalculateBounds(monitor, 1.5, 1.5,
            new() { Size = ChecklistAlertSize.Percentage, ScreenPercent = 50 }, false);
        Assert.AreEqual(960d, percent.Width); Assert.AreEqual(540d, percent.Height);
        var compact = ChecklistUrgentReminderWindow.CalculateBounds(monitor, 1.5, 1.5, new(), true);
        Assert.IsTrue(monitor.Contains(compact));
    }
    [TestMethod]
    public void HighContrastOverridesAreLocalAndRestoreThemeInheritance() => WpfTestHost.Invoke(() =>
    {
        var window = new Window();
        try
        {
            WindowAppearance.ApplyNotificationContrast(window, true);
            Assert.AreSame(SystemColors.WindowBrush, window.Resources["NotedSurfaceBrush"]);
            WindowAppearance.ApplyNotificationContrast(window, false);
            Assert.IsFalse(window.Resources.Contains("NotedSurfaceBrush"));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void DelayedPreviewCleanupRemovesOnlyItsOwnNotification() => WpfTestHost.Invoke(() =>
    {
        var owner = new Window(); uint next = 0;
        var visible = new HashSet<uint>(); var pendingRemoval = new List<Action>();
        uint Show(ChecklistNotificationStyle style, ChecklistNotificationOptions options) { var id = ++next; visible.Add(id); return id; }
        void Stop(uint id) => pendingRemoval.Add(() => visible.Remove(id));
        using var first = new ChecklistNotificationPreview(owner, Show, Stop);
        using var second = new ChecklistNotificationPreview(owner, Show, Stop);
        try
        {
            Assert.IsTrue(first.Show(ChecklistNotificationStyle.Standard, new(), out _));
            first.Dispose();
            Assert.IsTrue(second.Show(ChecklistNotificationStyle.Persistent, new(), out _));
            pendingRemoval[0]();
            Assert.IsFalse(visible.Contains(1)); Assert.IsTrue(visible.Contains(2));
            first.Dispose(); Assert.AreEqual(1, pendingRemoval.Count);
            second.Dispose(); pendingRemoval[1](); Assert.AreEqual(0, visible.Count);
        }
        finally { owner.Close(); }
    });

    [TestMethod]
    public void SilencingPlayingSourceStopsItsClipWhileOtherReminderRemains() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var first = Item(); var second = Item(); owner.Items.Add(first); owner.Items.Add(second);
        using var model = Model(owner); model.TryAccept([Request(first), Request(second)]);
        int stops = 0, plays = 0;
        using var audio = new ChecklistUrgentReminderAudio(model, Dispatcher.CurrentDispatcher, () => true,
            new Clock(), _ => plays++, () => stops++);
        audio.CheckNow(); var before = stops;
        Assert.IsTrue(model.TrySilence(model.Entries[0]));
        audio.CheckNow(); Assert.IsTrue(stops > before); Assert.AreEqual(1, plays);
        Assert.AreEqual(2, model.Entries.Count); Assert.IsFalse(model.Entries[1].IsSilenced);
    });

    [TestMethod]
    public void InconsistentWavTimingCannotBypassDurationValidation() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var resource = Application.GetResourceStream(new Uri("/Noted;component/Resources/ReminderAlarm.wav", UriKind.Relative))!.Stream;
        var path = directory.File("bad-timing.wav"); using (var output = File.Create(path)) resource.CopyTo(output);
        using (var output = File.OpenWrite(path)) { output.Position = 28; output.Write(BitConverter.GetBytes(1u)); }
        Assert.ThrowsExactly<IOException>(() => ChecklistUrgentReminderAudio.ValidateWav(path));
    });

    [TestMethod]
    public void ClosingPreviewCannotStopRealAlarmThatPreemptedIt() => WpfTestHost.Invoke(() =>
    {
        using var realOwner = Owner(new Store()); var realItem = Item(); realOwner.Items.Add(realItem);
        using var realModel = Model(realOwner); realModel.TryAccept([Request(realItem)]);
        using var previewOwner = Owner(new Store()); var previewItem = Item(); previewOwner.Items.Add(previewItem);
        using var previewModel = new ChecklistUrgentRemindersViewModel(previewOwner, Dispatcher.CurrentDispatcher, new Clock(), isPreview: true);
        previewModel.TryAccept([Request(previewItem)]);
        int realStops = 0, previewStops = 0;
        using var real = new ChecklistUrgentReminderAudio(realModel, Dispatcher.CurrentDispatcher, () => true, new Clock(), _ => { }, () => realStops++);
        using var preview = new ChecklistUrgentReminderAudio(previewModel, Dispatcher.CurrentDispatcher, () => true, new Clock(), _ => { }, () => previewStops++);
        preview.CheckNow(); real.CheckNow();
        Assert.AreEqual(1, previewStops);
        preview.Dispose(); Assert.AreEqual(0, realStops);
        Assert.IsNull(realItem.ReminderOccurrence!.SilencedAtUtc); Assert.IsNull(realItem.ReminderOccurrence.AcknowledgedAtUtc);
    });

    [TestMethod]
    public void PreviewSoundCannotReplaceActiveRealAlarm() => WpfTestHost.Invoke(() =>
    {
        using var realOwner = Owner(new Store()); var item = Item(); realOwner.Items.Add(item);
        using var realModel = Model(realOwner); realModel.TryAccept([Request(item)]);
        using var previewOwner = Owner(new Store()); var sample = Item(); previewOwner.Items.Add(sample);
        using var previewModel = new ChecklistUrgentRemindersViewModel(previewOwner, Dispatcher.CurrentDispatcher, new Clock(), isPreview: true);
        previewModel.TryAccept([Request(sample)]);
        int realStops = 0, previewPlays = 0;
        using var real = new ChecklistUrgentReminderAudio(realModel, Dispatcher.CurrentDispatcher, () => true, new Clock(), _ => { }, () => realStops++);
        using var preview = new ChecklistUrgentReminderAudio(previewModel, Dispatcher.CurrentDispatcher, () => true, new Clock(), _ => previewPlays++);
        real.CheckNow(); preview.CheckNow();
        Assert.AreEqual(0, previewPlays); Assert.AreEqual(0, realStops); Assert.IsNotNull(previewModel.StatusMessage);
    });

    private static ChecklistItem Item() => new() { Text = "task", NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = s_now.AddMinutes(-1) };
    private static ChecklistReminder Request(ChecklistItem item, ChecklistNotificationOptions? options = null) =>
        new(item.Id, item.ReminderAtUtc!.Value, item.Text, Style: ChecklistNotificationStyle.Urgent, Occurrence: item.ReminderOccurrence, Options: options);
    private static ChecklistWindowViewModel Owner(Store store) => new(store, Dispatcher.CurrentDispatcher, action => action(), false);
    private static ChecklistUrgentRemindersViewModel Model(ChecklistWindowViewModel owner) => new(owner, Dispatcher.CurrentDispatcher, new Clock());
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => s_now.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        internal bool Fail { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        { if (Fail) throw new IOException("test write failure"); Items = items.ToArray(); return new(null); }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
