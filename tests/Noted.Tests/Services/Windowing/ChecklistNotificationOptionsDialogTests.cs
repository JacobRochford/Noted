using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Noted.Models;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class ChecklistNotificationOptionsDialogTests
{
    [TestMethod]
    public void ChangingStandardSoundKeepsOtherNotificationOptions() => WpfTestHost.Invoke(() =>
    {
        var original = new ChecklistNotificationOptions { UrgentSound = ChecklistUrgentSound.Silent,
            Acknowledgment = ChecklistAcknowledgment.Hold,
            PersistentSound = ChecklistWindowsNotificationSound.RepeatingAlarm };
        var dialog = new ChecklistNotificationOptionsDialog(original, ChecklistNotificationStyle.Standard);
        Show(dialog, () =>
        {
            Assert.AreEqual(Visibility.Collapsed, ((StackPanel)dialog.FindName("UrgentOptions")).Visibility);
            ((ComboBox)dialog.FindName("SoundChoice")).SelectedValue = ChecklistWindowsNotificationSound.Silent;
            dialog.GetType().GetMethod("Apply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(dialog, [dialog, new RoutedEventArgs()]);
        });
        Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, dialog.Options.StandardSound);
        Assert.AreEqual(original.UrgentSound, dialog.Options.UrgentSound);
        Assert.AreEqual(original.Acknowledgment, dialog.Options.Acknowledgment);
        Assert.AreEqual(original.PersistentSound, dialog.Options.PersistentSound);
    });

    [TestMethod]
    public void SwitchingNotificationStylesKeepsSelectedSounds() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistNotificationOptionsDialog(new(), ChecklistNotificationStyle.Standard);
        Show(dialog, () =>
        {
            var styles = (ComboBox)dialog.FindName("PreviewStyle");
            var sound = (ComboBox)dialog.FindName("SoundChoice");
            sound.SelectedValue = ChecklistWindowsNotificationSound.Silent;
            styles.SelectedValue = ChecklistNotificationStyle.Urgent;
            Assert.AreEqual(Visibility.Visible, ((StackPanel)dialog.FindName("UrgentOptions")).Visibility);
            var alarm = (CheckBox)dialog.FindName("PlayAlarmSound");
            Assert.IsTrue(alarm.IsChecked == true);
            Assert.IsFalse(sound.IsVisible);
            alarm.IsChecked = false;
            Assert.IsFalse(((Button)dialog.FindName("PlaySound")).IsEnabled);
            styles.SelectedValue = ChecklistNotificationStyle.Standard;
            Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, sound.SelectedValue);
            styles.SelectedValue = ChecklistNotificationStyle.Urgent;
            Assert.IsFalse(alarm.IsChecked == true);
            dialog.GetType().GetMethod("Apply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(dialog, [dialog, new RoutedEventArgs()]);
        });
        Assert.AreEqual(ChecklistUrgentSound.Silent, dialog.Options.UrgentSound);
        Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, dialog.Options.StandardSound);
        Assert.AreEqual(ChecklistAlertMonitor.Primary, dialog.Options.Monitor);
    });

    [TestMethod]
    public void GlobalUrgentOptionsAndPreviewControlsFitTheDefaultLayout() => WpfTestHost.Invoke(() =>
    {
        var dialog = new ChecklistNotificationOptionsDialog(new());
        Show(dialog, () =>
        {
            Assert.IsTrue(((ComboBox)dialog.FindName("PreviewStyle")).IsVisible);
            Assert.IsTrue(((CheckBox)dialog.FindName("PlayAlarmSound")).IsVisible);
            Assert.IsFalse(((ComboBox)dialog.FindName("SoundChoice")).IsVisible);
            Assert.IsTrue(((Button)dialog.FindName("PlaySound")).IsVisible);
            Assert.AreEqual(Visibility.Collapsed, ((ScrollViewer)dialog.FindName("OptionsFields")).ComputedVerticalScrollBarVisibility);
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
    });

    [TestMethod]
    public void ChangingUrgentSoundKeepsSavedPresentationSettings() => WpfTestHost.Invoke(() =>
    {
        var original = new ChecklistNotificationOptions
        {
            Size = ChecklistAlertSize.Percentage, Monitor = ChecklistAlertMonitor.Selected, MonitorDeviceName = "saved display",
            TextSize = 42, PulseAccent = false, Acknowledgment = ChecklistAcknowledgment.Hold
        };
        var dialog = new ChecklistNotificationOptionsDialog(original);
        Show(dialog, () =>
        {
            ((CheckBox)dialog.FindName("PlayAlarmSound")).IsChecked = false;
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.AreEqual(original with { UrgentSound = ChecklistUrgentSound.Silent }, dialog.Options);
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
