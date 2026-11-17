using System.Windows;
using System.Windows.Controls;
using Noted.Models;

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
            var period = (ComboBox)time.FindName("PeriodChoice");
            Assert.IsTrue(hour.Focus());
            hour.SelectedIndex = 1;
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

    private static void Apply(ChecklistReminderDialog dialog) => ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Show(Window dialog, Action action)
    {
        Exception? failure = null;
        dialog.Loaded += (_, _) =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        var accepted = dialog.ShowDialog();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.AreEqual(true, accepted);
    }
}
