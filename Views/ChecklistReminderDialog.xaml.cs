using System.Globalization;
using System.Windows;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class ChecklistReminderDialog : Window
{
    internal DateTimeOffset? ReminderAtUtc { get; private set; }

    private readonly ChecklistNotificationOptions _defaults;
    private ChecklistNotificationOptions? _custom;
    private ChecklistNotificationPreview? _preview;
    internal ChecklistNotificationStyle NotificationStyle => (ChecklistNotificationStyle)Math.Max(0, StyleChoice.SelectedIndex);
    internal ChecklistNotificationOptions? NotificationOptions => CustomOptions.IsChecked == true ? _custom ?? _defaults : null;

    internal ChecklistReminderDialog(DateTimeOffset? reminder, DateTime? dueDate,
        ChecklistNotificationStyle style = ChecklistNotificationStyle.Standard, ChecklistNotificationOptions? options = null,
        ChecklistNotificationOptions? defaults = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppThemeManager.ApplyTitleBar(this);
        _defaults = (defaults ?? new()).Normalize(); _custom = options?.Normalize();
        StyleChoice.SelectedIndex = (int)style; CustomOptions.IsChecked = options is not null;
        Closed += (_, _) => { _preview?.Dispose(); _preview = null; };
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

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChecklistNotificationOptionsDialog(_custom ?? _defaults, NotificationStyle) { Owner = this };
        if (dialog.ShowDialog() == true) { _custom = dialog.Options; CustomOptions.IsChecked = true; }
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        _preview?.Dispose(); _preview = new(this);
        if (!_preview.Show(NotificationStyle, NotificationOptions ?? _defaults, out var error))
        { ErrorText.Text = error; ErrorText.Visibility = Visibility.Visible; }
    }
    private void StopPreview_Click(object sender, RoutedEventArgs e) { _preview?.Dispose(); _preview = null; }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        ReminderAtUtc = null;
        DialogResult = true;
    }
}
