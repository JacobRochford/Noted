using System.Globalization;
using System.Windows;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class ChecklistReminderDialog : ChecklistEditorWindow
{
    internal Func<ChecklistReminderDialog, string?>? Commit { private get; set; }
    internal DateTimeOffset? ReminderAtUtc { get; private set; }
    internal DateTime? ScheduledDueDate { get; private set; }
    internal TimeSpan? ScheduledDueTime { get; private set; }
    internal bool ReminderAtDueTime { get; private set; }
    private readonly DateTimeOffset? _originalReminder;
    private readonly bool _originalAtDueTime;
    private readonly DateTime? _originalDueDate;
    private readonly TimeSpan? _originalDueTime;

    private readonly ChecklistNotificationOptions _defaults;
    private ChecklistNotificationOptions? _custom;
    private ChecklistNotificationOptionsDialog? _optionsDialog;
    internal ChecklistNotificationStyle NotificationStyle => StyleChoice.SelectedValue is ChecklistNotificationStyle style ? style : ChecklistNotificationStyle.Standard;
    internal ChecklistNotificationOptions? NotificationOptions => _custom;

    internal ChecklistReminderDialog(DateTimeOffset? reminder, DateTime? dueDate,
        ChecklistNotificationStyle style = ChecklistNotificationStyle.Standard, ChecklistNotificationOptions? options = null,
        ChecklistNotificationOptions? defaults = null, TimeSpan? dueTime = null, bool reminderAtDueTime = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppThemeManager.ApplyTitleBar(this);
        _defaults = (defaults ?? new()).Normalize();
        _custom = options;
        StyleChoice.SelectedValue = style;
        UpdateResetVisibility();
        UpdateQuickDates();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        var local = reminder?.ToLocalTime().DateTime ??
            ChecklistReminderTime.DefaultLocalTime(dueDate, DateTimeOffset.UtcNow, TimeZoneInfo.Local, dueTime);
        ReminderDate.SelectedDate = local.Date;
        ReminderTime.SelectedTime = local.TimeOfDay;
        DueDate.SelectedDate = dueDate;
        DueTime.SelectedTime = dueTime;
        SuggestDueTime();
        _originalReminder = reminder;
        _originalAtDueTime = reminderAtDueTime;
        _originalDueDate = dueDate;
        _originalDueTime = dueTime;
        EnableReminder.IsChecked = reminder.HasValue;
        ReminderMode.SelectedIndex = reminder.HasValue && !reminderAtDueTime ? 1 : 0;
        UpdateReminderFields();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_optionsDialog is not null) return;
        if (!TryReadDate(DueDate, out var dueDate)) { ShowMessage("Enter a valid due date or clear it."); return; }
        if (DueTime.SelectedTime.HasValue && dueDate is null)
        { ShowMessage("Choose a due date, or turn off its time."); return; }
        ScheduledDueDate = dueDate;
        ScheduledDueTime = DueTime.SelectedTime;
        ReminderAtDueTime = EnableReminder.IsChecked == true && ReminderMode.SelectedIndex == 0;
        ReminderAtUtc = null;
        if (EnableReminder.IsChecked == true)
        {
            var date = dueDate;
            var time = ScheduledDueTime;
            if (!ReminderAtDueTime)
            {
                if (!TryReadDate(ReminderDate, out date)) { ShowMessage("Enter a valid reminder date."); return; }
                time = ReminderTime.SelectedTime;
            }
            if (date is null || time is null)
            { ShowMessage("Choose a due date and time, or choose another reminder time."); return; }
            // unchanged reminders may already be overdue or acknowledged; editing the due date must not rearm them.
            var unchanged = ReminderAtDueTime == _originalAtDueTime &&
                (ReminderAtDueTime ? date == _originalDueDate && time == _originalDueTime :
                    _originalReminder?.ToLocalTime().Date == date && _originalReminder?.ToLocalTime().TimeOfDay == time);
            if (unchanged) ReminderAtUtc = _originalReminder;
            else
            {
                if (!ChecklistReminderTime.TryGetUtc(date, time,
                        DateTimeOffset.UtcNow, TimeZoneInfo.Local, out var utc, out var error))
                { ShowMessage(error!); return; }
                ReminderAtUtc = utc;
            }
        }
        if (Commit?.Invoke(this) is { } errorMessage)
        {
            ShowMessage(errorMessage);
            return;
        }
        Close();
    }

    private static bool TryReadDate(System.Windows.Controls.DatePicker picker, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(picker.Text)) return true;
        if (!DateTime.TryParse(picker.Text, picker.Language.GetSpecificCulture(), DateTimeStyles.None, out var parsed)) return false;
        date = parsed.Date;
        return true;
    }

    private static DateTime NextMonday(DateTime today)
    {
        var days = (8 - (int)today.DayOfWeek) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    private void UpdateQuickDates()
    {
        var today = DateTime.Today;
        QuickToday.Content = $"Today · {today:d}";
        QuickTomorrow.Content = $"Tomorrow · {today.AddDays(1):d}";
        QuickNextWeek.Content = $"Next week · {NextMonday(today):d}";
        QuickToday.ToolTip = today.ToString("D");
        QuickTomorrow.ToolTip = today.AddDays(1).ToString("D");
        QuickNextWeek.ToolTip = NextMonday(today).ToString("D");
    }

    private void QuickDate_Click(object sender, RoutedEventArgs e)
    {
        UpdateQuickDates();
        var today = DateTime.Today;
        var choice = (sender as System.Windows.Controls.Button)?.Tag as string;
        DueDate.SelectedDate = choice switch
        {
            "tomorrow" => today.AddDays(1),
            "next-week" => NextMonday(today),
            _ => today
        };
    }

    private void ShowMessage(string message, bool isError = true)
    {
        ErrorText.Text = message;
        ErrorText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "NotedDangerBrush" : "NotedSecondaryTextBrush");
        ErrorText.Visibility = Visibility.Visible;
    }
    private void ClearDue_Click(object sender, RoutedEventArgs e)
    {
        DueDate.SelectedDate = null; DueDate.Text = ""; DueTime.SelectedTime = null;
        ShowMessage(ReminderMode.SelectedIndex == 1 && EnableReminder.IsChecked == true
            ? "The task date will be cleared. Its custom notification will remain."
            : "The task date, time and due-time notification will be cleared when you apply.", isError: false);
    }
    private void ReminderChoice_Changed(object sender, RoutedEventArgs e)
    {
        UpdateReminderFields();
        if (IsLoaded && EnableReminder.IsChecked == true && ReminderMode.SelectedIndex == 0 && !DueTime.SelectedTime.HasValue)
        {
            if (DueDate.SelectedDate is not { } date || date.Date < DateTime.Today)
            { ShowMessage("Choose a current or future task date, or use a custom notification time."); return; }
            var suggested = ChecklistReminderTime.DefaultLocalTime(date, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            DueTime.Suggest(suggested.TimeOfDay);
            if (suggested.Date != date.Date)
                ShowMessage($"The next suggested time is {suggested:g}. Choose that date or another future time; your task date has not been moved.", isError: false);
            ((System.Windows.Controls.CheckBox)DueTime.FindName("UseTime")).Focus();
        }
    }

    private void DueDate_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SuggestDueTime();
    private void SuggestDueTime()
    {
        if (DueTime is null || DueTime.SelectedTime.HasValue) return;
        var suggested = ChecklistReminderTime.DefaultLocalTime(DueDate.SelectedDate, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        DueTime.Suggest(suggested.TimeOfDay);
    }
    private void DueTime_Changed(object sender, RoutedEventArgs e)
    {
        if (DueTime.SelectedTime is null && EnableReminder.IsChecked == true && ReminderMode.SelectedIndex == 0)
        {
            EnableReminder.IsChecked = false;
            ShowMessage("Removing the task time also removes its due-time notification when you apply.", isError: false);
        }
    }
    private void ReminderMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateReminderFields();
        if (ErrorText is not null) ErrorText.Visibility = Visibility.Collapsed;
    }
    private void UpdateReminderFields()
    {
        if (ReminderControls is null || ReminderFields is null) return;
        ReminderControls.Visibility = EnableReminder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReminderFields.Visibility = ReminderMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (_optionsDialog is not null) return;
        var dialog = new ChecklistNotificationOptionsDialog(_custom ?? _defaults, NotificationStyle, allowStyleSelection: false)
        {
            Commit = options =>
            {
                _custom = options;
                UpdateResetVisibility();
                return null;
            }
        };
        _optionsDialog = dialog;
        ScheduleFields.IsEnabled = ApplyButton.IsEnabled = false;
        dialog.Closed += (_, _) =>
        {
            _optionsDialog = null;
            ScheduleFields.IsEnabled = ApplyButton.IsEnabled = true;
        };
        dialog.ShowFor(this);
    }

    internal void ActivateEditor() => (_optionsDialog as Window ?? this).Activate();

    private void ResetOptions_Click(object sender, RoutedEventArgs e) { _custom = null; UpdateResetVisibility(); }
    private void UpdateResetVisibility() => ResetOptions.Visibility = _custom is null ? Visibility.Collapsed : Visibility.Visible;
}
