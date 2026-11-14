using System.Globalization;
using System.Windows;
using Noted.Helpers;

namespace Noted;

public partial class ChecklistReminderDialog : Window
{
    internal DateTimeOffset? ReminderAtUtc { get; private set; }

    internal ChecklistReminderDialog(DateTimeOffset? reminder, DateTime? dueDate)
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        var local = reminder?.ToLocalTime().DateTime ??
            ChecklistReminderTime.DefaultLocalTime(dueDate, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        ReminderDate.SelectedDate = local.Date;
        ReminderTime.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        RemoveButton.IsEnabled = reminder is not null;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DateTime? date = DateTime.TryParse(ReminderDate.Text, ReminderDate.Language.GetSpecificCulture(),
            DateTimeStyles.None, out var parsed) ? parsed.Date : null;
        if (!ChecklistReminderTime.TryGetUtc(date, ReminderTime.Text, DateTimeOffset.UtcNow, TimeZoneInfo.Local,
                out var utc, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ReminderAtUtc = utc;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        ReminderAtUtc = null;
        DialogResult = true;
    }
}
