using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class ChecklistScheduleEditorTests
{
    [TestMethod]
    public void MissingDueDateAllowsCalendarSelection() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistReminderDialog(null, null);
        Show(dialog, () =>
        {
            var due = (DatePicker)dialog.FindName("DueDate");
            var time = (ChecklistTimePicker)dialog.FindName("DueTime");
            ((CheckBox)time.FindName("UseTime")).IsChecked = true;
            Apply(dialog);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)dialog.FindName("ErrorText")).Visibility);
            var calendarButton = Noted.Helpers.VisualTreeHelpers.FindDescendant<Button>(due, "PART_Button")!;
            calendarButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(due.IsDropDownOpen);
            due.SelectedDate = DateTime.Today.AddDays(2);
            due.IsDropDownOpen = false;
            var hour = (ComboBox)time.FindName("HourChoice");
            var minute = (ComboBox)time.FindName("MinuteChoice");
            var period = (ComboBox)time.FindName("PeriodChoice");
            Assert.IsTrue(hour.Focus());
            hour.SelectedIndex = 1;
            minute.SelectedIndex = 0;
            Assert.IsTrue(period.Focus());
            period.SelectedIndex = 1;
            Assert.IsTrue(hour.Focus());
            Assert.AreEqual(TimeSpan.FromHours(14), time.SelectedTime);
            ((CheckBox)dialog.FindName("EnableReminder")).IsChecked = true;
            Assert.AreEqual(Visibility.Collapsed, ((StackPanel)dialog.FindName("ReminderFields")).Visibility);
            Apply(dialog);
        });
        Assert.IsTrue(dialog.ReminderAtDueTime);
        Assert.AreEqual(TimeSpan.FromHours(14), dialog.ScheduledDueTime);
        Assert.AreEqual(dialog.ScheduledDueDate!.Value.AddHours(14), dialog.ReminderAtUtc!.Value.LocalDateTime);
    });

    [TestMethod]
    public void DateOnlyScheduleKeepsRemindersOffByDefault() => WpfTestHost.Invoke(() =>
    {
        var due = DateTime.Today.AddDays(2);
        var dialog = new ChecklistReminderDialog(null, due);
        Show(dialog, () => Apply(dialog));
        Assert.AreEqual(due, dialog.ScheduledDueDate);
        Assert.IsNull(dialog.ScheduledDueTime);
        Assert.IsNull(dialog.ReminderAtUtc);
        Assert.IsFalse(dialog.ReminderAtDueTime);
    });

    [TestMethod]
    public void DueDateEditsKeepExistingOverdueReminder() => WpfTestHost.Invoke(() =>
    {
        var reminder = DateTimeOffset.UtcNow.AddMinutes(-10);
        var dialog = new ChecklistReminderDialog(reminder, null);
        Show(dialog, () =>
        {
            ((DatePicker)dialog.FindName("DueDate")).SelectedDate = DateTime.Today.AddDays(3);
            Apply(dialog);
        });
        Assert.AreEqual(reminder, dialog.ReminderAtUtc);
        Assert.IsFalse(dialog.ReminderAtDueTime);
    });

    [TestMethod]
    public void TimePickerUsesRegional24HourClockWithoutAmPm() => WpfTestHost.Invoke(() =>
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var picker = new ChecklistTimePicker { SelectedTime = TimeSpan.FromHours(13) };
            var hours = (ComboBox)picker.FindName("HourChoice");
            Assert.AreEqual(24, hours.Items.Count);
            Assert.AreEqual(13, hours.SelectedIndex);
            Assert.AreEqual(Visibility.Collapsed, ((ComboBox)picker.FindName("PeriodChoice")).Visibility);
            hours.SelectedIndex = 8;
            Assert.AreEqual(TimeSpan.FromHours(8), picker.SelectedTime);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    });

    [TestMethod]
    public void QuickDatesShowResolvedDatesAndDoNotEnableTimeOrNotifications() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistReminderDialog(null, null);
        Show(dialog, () =>
        {
            var time = (ChecklistTimePicker)dialog.FindName("DueTime");
            Assert.AreEqual(Visibility.Collapsed, ((StackPanel)time.FindName("ClockFields")).Visibility);
            var tomorrow = (Button)dialog.FindName("QuickTomorrow");
            tomorrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(DateTime.Today.AddDays(1), ((DatePicker)dialog.FindName("DueDate")).SelectedDate);
            StringAssert.Contains((string)tomorrow.Content, DateTime.Today.AddDays(1).ToString("d"));
            Assert.AreNotEqual(true, ((CheckBox)dialog.FindName("EnableReminder")).IsChecked);
            Assert.IsNull(time.SelectedTime);
            Apply(dialog);
        });
        Assert.IsNull(dialog.ReminderAtUtc);
    });

    [TestMethod]
    public void OwnedEditorsReuseTheSameDraftAndLeaveOtherWindowsEnabled() => WithTasks((window, model) =>
    {
        var item = model.AddItem()!;
        var another = model.AddItem()!;
        var otherWindow = new Window();
        otherWindow.Show();
        try
        {
            var editor = Open(window, item);
            ((DatePicker)editor.FindName("DueDate")).SelectedDate = DateTime.Today.AddDays(2);
            window.EditReminder(item);
            Assert.AreSame(editor, window.OwnedWindows.OfType<ChecklistReminderDialog>().Single());
            Assert.AreSame(window, editor.Owner);
            Assert.IsFalse(editor.Topmost);
            Assert.IsFalse(window.Topmost);
            var editorHandle = new WindowInteropHelper(editor).Handle;
            Assert.AreEqual(0, WindowInterop.GetWindowLong(editorHandle, WindowInterop.GWL_EXSTYLE) & WindowInterop.WS_EX_TOPMOST);
            Assert.IsTrue(WindowInterop.IsWindowEnabled(new WindowInteropHelper(window).Handle));
            Assert.IsTrue(WindowInterop.IsWindowEnabled(new WindowInteropHelper(otherWindow).Handle));
            Assert.IsFalse(window.TryActivateOwnedWindow());
            var second = Open(window, another);
            Click(editor, "CancelButton");
            Assert.IsNull(item.DueDate);
            Assert.IsTrue(second.IsVisible);
            Assert.IsFalse(window.Topmost);
            second.Close();
            Assert.IsTrue(window.Topmost);
            Assert.AreNotSame(editor, Open(window, item));
        }
        finally { otherWindow.Close(); }
    });

    [TestMethod]
    public void InitialPlacementUsesTheAvailableSideOrCentersAndClamps()
    {
        var work = new Rect(-1440, 40, 1440, 860);
        var dialog = new Size(600, 420);

        Assert.AreEqual(new Point(-1022, 80), ChecklistEditorWindow.CalculateInitialPosition(
            new Rect(-1400, 80, 360, 500), dialog, work, 144));
        Assert.AreEqual(new Point(-1038, 480), ChecklistEditorWindow.CalculateInitialPosition(
            new Rect(-420, 700, 370, 350), dialog, work, 144));
        Assert.AreEqual(new Point(-1020, 40), ChecklistEditorWindow.CalculateInitialPosition(
            new Rect(-1120, 0, 800, 500), dialog, work, 144));
    }

    [TestMethod]
    public void BothEditorsOpenNearTheirLaunchingWindowAndReuseKeepsTheirPosition() => WithTasks((window, model) =>
    {
        var ownerHandle = new WindowInteropHelper(window).Handle;
        var work = WindowInterop.GetMonitorWorkArea(WindowInterop.MonitorFromWindow(ownerHandle, 2));
        var item = model.AddItem()!;
        MoveWindow(window, work.Left + 80, work.Top + 60);

        var schedule = Open(window, item);
        VerifyInitialPlacement(schedule, window);
        ((CheckBox)schedule.FindName("EnableReminder")).IsChecked = true;
        MoveWindow(schedule, work.Left + 130, work.Top + 100);
        var movedSchedule = WindowInterop.GetWindowBounds(new WindowInteropHelper(schedule).Handle);
        window.EditReminder(item);
        WaitForLayout(window);
        Assert.AreEqual(movedSchedule, WindowInterop.GetWindowBounds(new WindowInteropHelper(schedule).Handle));

        Click(schedule, "OptionsButton");
        var options = schedule.OwnedWindows.OfType<ChecklistNotificationOptionsDialog>().Single();
        VerifyInitialPlacement(options, schedule);
        options.SizeToContent = SizeToContent.Manual;
        options.Width = Math.Max(options.MinWidth, options.ActualWidth - 24);
        MoveWindow(options, work.Left + 60, work.Top + 120);
        var adjustedOptions = WindowInterop.GetWindowBounds(new WindowInteropHelper(options).Handle);
        window.EditReminder(item);
        WaitForLayout(window);
        Assert.AreEqual(adjustedOptions, WindowInterop.GetWindowBounds(new WindowInteropHelper(options).Handle));
    });

    private static void VerifyInitialPlacement(Window dialog, Window owner)
    {
        WaitForLayout(dialog);
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var work = WindowInterop.GetMonitorWorkArea(WindowInterop.MonitorFromWindow(ownerHandle, 2));
        var bounds = WindowInterop.GetWindowBounds(new WindowInteropHelper(dialog).Handle);
        var expected = ChecklistEditorWindow.CalculateInitialPosition(WindowInterop.GetWindowBounds(ownerHandle),
            bounds.Size, work, WindowInterop.GetDpiForWindow(ownerHandle));
        Assert.AreEqual(new Point((int)expected.X, (int)expected.Y), bounds.Location);
        Assert.IsTrue(work.Contains(bounds));
    }

    private static void MoveWindow(Window window, double left, double top)
    {
        Assert.IsTrue(WindowInterop.SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
            (int)left, (int)top, 0, 0, WindowInterop.SWP_NOZORDER | WindowInterop.SWP_NOACTIVATE | WindowInterop.SWP_NOSIZE));
        WaitForLayout(window);
    }

    private static void WaitForLayout(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [TestMethod]
    public void PerTaskOptionsApplyToTheDraftAndOwnerCloseDiscardsIt() => WithTasks((window, model) =>
    {
        var item = model.AddItem()!;
        var editor = Open(window, item);
        ((CheckBox)editor.FindName("EnableReminder")).IsChecked = true;
        Click(editor, "OptionsButton");
        var options = editor.OwnedWindows.OfType<ChecklistNotificationOptionsDialog>().Single();
        Assert.AreSame(editor, options.Owner);
        Assert.AreEqual(Visibility.Collapsed, ((StackPanel)options.FindName("StylePicker")).Visibility);
        Assert.IsFalse(((Button)editor.FindName("ApplyButton")).IsEnabled);
        Assert.IsFalse(((ComboBox)editor.FindName("StyleChoice")).IsEnabled);
        window.EditReminder(item);
        Assert.AreSame(options, editor.OwnedWindows.OfType<ChecklistNotificationOptionsDialog>().Single());
        ((ComboBox)options.FindName("SoundChoice")).SelectedValue = ChecklistWindowsNotificationSound.Silent;
        Click(options, "CancelButton");
        Assert.IsNull(editor.NotificationOptions);
        Assert.IsTrue(((Button)editor.FindName("ApplyButton")).IsEnabled);

        Click(editor, "OptionsButton");
        options = editor.OwnedWindows.OfType<ChecklistNotificationOptionsDialog>().Single();
        ((ComboBox)options.FindName("SoundChoice")).SelectedValue = ChecklistWindowsNotificationSound.Silent;
        Click(options, "ApplyButton");
        Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, editor.NotificationOptions?.StandardSound);
        Assert.IsNull(item.NotificationOptions);
        Click(editor, "OptionsButton");
        options = editor.OwnedWindows.OfType<ChecklistNotificationOptionsDialog>().Single();
        window.Close();
        Assert.IsFalse(editor.IsVisible);
        Assert.IsFalse(options.IsVisible);
        Assert.IsNull(item.NotificationOptions);
    });

    [TestMethod]
    public void ApplyingSchedulePreservesOtherTaskEdits() => WithTasks((window, model) =>
    {
        var item = model.AddItem()!;
        var editor = Open(window, item);
        var due = DateTime.Today.AddDays(2);
        ((DatePicker)editor.FindName("DueDate")).SelectedDate = due;
        item.Text = "new title";
        item.Notes = "new notes";
        item.IsExpanded = true;
        Apply(editor);
        Assert.IsFalse(editor.IsVisible);
        Assert.AreEqual(due, item.DueDate);
        Assert.AreEqual("new title", item.Text);
        Assert.AreEqual("new notes", item.Notes);
        Assert.IsTrue(item.IsExpanded);
    });

    [TestMethod]
    [DataRow("rescheduled")]
    [DataRow("completed")]
    [DataRow("deleted")]
    public void StaleDraftLeavesTheCurrentTaskStateUntouched(string change) => WithTasks((window, model) =>
    {
        var item = model.AddItem()!;
        var editor = Open(window, item);
        ((DatePicker)editor.FindName("DueDate")).SelectedDate = DateTime.Today.AddDays(2);
        var newerDate = DateTime.Today.AddDays(3);
        if (change == "rescheduled") item.DueDate = newerDate;
        else if (change == "completed") item.IsChecked = true;
        else model.RemoveItem(item);

        Apply(editor);
        Assert.IsTrue(editor.IsVisible);
        Assert.AreEqual(Visibility.Visible, ((TextBlock)editor.FindName("ErrorText")).Visibility);
        Assert.AreEqual(change != "deleted", model.Items.Contains(item));
        Assert.AreEqual(change == "completed", item.IsChecked);
        Assert.AreEqual(change == "rescheduled" ? newerDate : (DateTime?)null, item.DueDate);
    });

    [TestMethod]
    public void CommonControlsFitAndTheFooterStaysOutsideTheScrollArea() => WpfTestHost.Invoke(() =>
    {
        var due = DateTime.Today.AddDays(2);
        var dialog = new ChecklistReminderDialog(null, due);
        Show(dialog, () =>
        {
            var collapsedHeight = dialog.ActualHeight;
            var time = (ChecklistTimePicker)dialog.FindName("DueTime");
            ((CheckBox)time.FindName("UseTime")).IsChecked = true;
            ((CheckBox)dialog.FindName("EnableReminder")).IsChecked = true;
            dialog.UpdateLayout();
            Assert.IsTrue(dialog.ActualHeight > collapsedHeight);
            var scroll = (ScrollViewer)dialog.FindName("ScheduleFields");
            Assert.AreEqual(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Height = dialog.MinHeight;
            ((ComboBox)dialog.FindName("ReminderMode")).SelectedIndex = 1;
            dialog.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            Assert.IsTrue(((Button)dialog.FindName("ApplyButton")).IsVisible);
            Assert.IsTrue(((Button)dialog.FindName("CancelButton")).IsVisible);
            Apply(dialog);
        });
    });

    private static void Apply(ChecklistReminderDialog dialog) => Click(dialog, "ApplyButton");

    [TestMethod]
    public void RepeatControlsAppearOnlyForStandardAndValidateCustomValues() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistReminderDialog(DateTimeOffset.UtcNow.AddDays(2), null);
        Show(dialog, () =>
        {
            var toggle = (CheckBox)dialog.FindName("EnableRepeat");
            var repeat = (StackPanel)dialog.FindName("RepeatControls");
            var fields = (StackPanel)dialog.FindName("RepeatFields");
            var interval = (ComboBox)dialog.FindName("RepeatInterval");
            var style = (ComboBox)dialog.FindName("StyleChoice");
            Assert.IsFalse(toggle.IsChecked == true);
            Assert.AreEqual(Visibility.Collapsed, fields.Visibility);
            toggle.IsChecked = true;
            CollectionAssert.AreEqual(new[] { "5 minutes", "10 minutes", "15 minutes", "30 minutes", "60 minutes", "Custom interval" },
                interval.Items.Cast<string>().ToArray());
            Assert.AreEqual("10 minutes", interval.SelectedItem);
            style.SelectedValue = ChecklistNotificationStyle.Persistent;
            Assert.AreEqual(Visibility.Collapsed, repeat.Visibility);
            style.SelectedValue = ChecklistNotificationStyle.Urgent;
            Assert.AreEqual(Visibility.Collapsed, repeat.Visibility);
            style.SelectedValue = ChecklistNotificationStyle.Standard;
            var remind = (CheckBox)dialog.FindName("EnableReminder");
            remind.IsChecked = false;
            Assert.AreEqual(Visibility.Collapsed, repeat.Visibility);
            remind.IsChecked = true;
            Assert.AreEqual(Visibility.Visible, repeat.Visibility);
            Assert.AreEqual(Visibility.Visible, fields.Visibility);

            interval.SelectedIndex = 5;
            var minutes = (TextBox)dialog.FindName("CustomRepeatMinutes");
            minutes.Text = "1441";
            Apply(dialog);
            Assert.IsTrue(dialog.IsVisible);
            minutes.Text = "1440";
            ((ComboBox)dialog.FindName("RepeatStop")).SelectedIndex = 1;
            var count = (TextBox)dialog.FindName("RepeatLimit");
            count.Text = "0";
            Apply(dialog);
            Assert.IsTrue(dialog.IsVisible);
            count.Text = "3";
            Apply(dialog);
            Assert.AreEqual(new ChecklistReminderRepeatPolicy(1440, 3), dialog.RepeatPolicy);
        });
    });

    [TestMethod]
    public void ModelessRepeatEditorKeepsDeliveryProgressAndCancelDiscardsDraftChanges() => WithTasks((window, model) =>
    {
        var task = model.AddItem()!;
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var policy = new ChecklistReminderRepeatPolicy(10, null);
        Assert.IsTrue(model.TrySetSchedule(task, null, null, start, false, task.NotificationStyle, null, out _, policy));
        var editor = Open(window, task);
        var id = task.ReminderRepeat!.Id;
        var request = new ChecklistReminder(task.Id, start, task.Text, RepeatDelivery: new(id, start));
        Assert.IsTrue(model.TryMarkRemindersHandled([request], start, out _));
        var delivered = task.ReminderRepeat;
        task.Notes = "edited while scheduling";
        Apply(editor);
        Assert.IsFalse(editor.IsVisible);
        Assert.AreEqual(delivered, task.ReminderRepeat);
        Assert.AreEqual("edited while scheduling", task.Notes);

        editor = Open(window, task);
        ((CheckBox)editor.FindName("EnableRepeat")).IsChecked = false;
        Click(editor, "CancelButton");
        Assert.AreEqual(delivered, task.ReminderRepeat);

        editor = Open(window, task);
        Assert.IsTrue(model.TrySetSchedule(task, null, null, start.AddHours(1), false, task.NotificationStyle, null, out _, policy));
        Apply(editor);
        Assert.IsTrue(editor.IsVisible);
        Assert.AreNotEqual(id, task.ReminderRepeat!.Id);
        Assert.AreEqual(start.AddHours(1), task.ReminderAtUtc);
    });

    private static void Click(Window window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static ChecklistReminderDialog Open(ChecklistWindow window, ChecklistItem item)
    {
        window.EditReminder(item);
        return window.OwnedWindows.OfType<ChecklistReminderDialog>().Last();
    }

    private static void WithTasks(Action<ChecklistWindow, ChecklistWindowViewModel> test) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var model = new ChecklistWindowViewModel(new ChecklistContentService(directory.Path), Dispatcher.CurrentDispatcher, action => action(), false);
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), model);
        try { window.Show(); test(window, model); }
        finally { if (window.IsVisible) window.Close(); }
    });

    private static void Show(Window dialog, Action action)
    {
        dialog.Show();
        try
        {
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            action();
            Assert.IsFalse(dialog.IsVisible);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }
}
