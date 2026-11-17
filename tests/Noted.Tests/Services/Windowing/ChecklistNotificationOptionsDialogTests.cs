using System.Windows;
using System.Windows.Controls;
using Noted.Models;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class ChecklistNotificationOptionsDialogTests
{
    [TestMethod]
    public void ChangingStandardSoundKeepsOtherNotificationOptions() => WpfTestHost.Invoke(() =>
    {
        var original = new ChecklistNotificationOptions { UrgentSound = ChecklistUrgentSound.Custom,
            CustomSoundPath = @"C:\legacy.wav", Acknowledgment = ChecklistAcknowledgment.Hold,
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
            sound.SelectedValue = ChecklistUrgentSound.Bell;
            styles.SelectedValue = ChecklistNotificationStyle.Standard;
            Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, sound.SelectedValue);
            styles.SelectedValue = ChecklistNotificationStyle.Urgent;
            Assert.AreEqual(ChecklistUrgentSound.Bell, sound.SelectedValue);
            dialog.GetType().GetMethod("Apply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(dialog, [dialog, new RoutedEventArgs()]);
        });
        Assert.AreEqual(ChecklistUrgentSound.Bell, dialog.Options.UrgentSound);
        Assert.AreEqual(ChecklistWindowsNotificationSound.Silent, dialog.Options.StandardSound);
        Assert.AreEqual(ChecklistAlertMonitor.Primary, dialog.Options.Monitor);
    });

    [TestMethod]
    public void BundledSoundChoicesAreValidShortPcmClips() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        foreach (var name in new[] { "Chime", "Digital", "Bell" })
        {
            var resource = Application.GetResourceStream(new Uri("/Noted;component/Resources/Reminder" + name + ".wav", UriKind.Relative));
            Assert.IsNotNull(resource);
            var path = directory.File(name + ".wav");
            using (var input = resource.Stream)
            using (var output = System.IO.File.Create(path)) input.CopyTo(output);
            Assert.AreEqual(1.3, Noted.Services.ChecklistUrgentReminderAudio.ValidateWav(path), 0.01);
        }
    });

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
