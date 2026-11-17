using System.Windows;
using System.Windows.Controls;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class ChecklistNotificationOptionsDialog : Window
{
    internal ChecklistNotificationOptions Options { get; private set; }
    internal ChecklistNotificationStyle NotificationStyle => _style;
    private ChecklistNotificationOptions _draft;
    private ChecklistNotificationStyle _style;
    private ChecklistNotificationPreview? _preview;
    private bool _ready;
    private bool _closed;

    internal ChecklistNotificationOptionsDialog(ChecklistNotificationOptions options, ChecklistNotificationStyle style = ChecklistNotificationStyle.Urgent)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppThemeManager.ApplyTitleBar(this);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Options = _draft = options.Normalize();
        _style = style;
        Choices(AlertSize, [("Full screen", ChecklistAlertSize.FullScreen), ("Centered window", ChecklistAlertSize.Large)],
            Options.Size == ChecklistAlertSize.Percentage ? ChecklistAlertSize.Large : Options.Size);
        Choices(Acknowledgment, [("Four-digit confirmation code", ChecklistAcknowledgment.Code), ("Acknowledgment button", ChecklistAcknowledgment.Button)],
            Options.Acknowledgment == ChecklistAcknowledgment.Hold ? ChecklistAcknowledgment.Code : Options.Acknowledgment);
        Choices(PreviewStyle, [("Standard Windows", ChecklistNotificationStyle.Standard), ("Persistent Windows", ChecklistNotificationStyle.Persistent), ("Urgent Noted", ChecklistNotificationStyle.Urgent)], style);
        RepeatInterval.ItemsSource = new[] { 2, 5, 10, 15, 30, 60, Options.RepeatSeconds }.Distinct().Order().ToArray();
        RepeatInterval.SelectedItem = Options.RepeatSeconds;
        Repeat.IsChecked = Options.RepeatSound;
        AllMonitors.IsChecked = Options.Monitor == ChecklistAlertMonitor.All;
        ShowSoundChoices();
        _ready = true;
        WindowAppearance.ApplyNotificationContrast(this, SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += PreferencesChanged;
        Closed += (_, _) => { _closed = true; SystemParameters.StaticPropertyChanged -= PreferencesChanged; StopPreview(); };
    }

    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        { if (!_closed) WindowAppearance.ApplyNotificationContrast(this, SystemParameters.HighContrast); });
    }

    private static void Choices<T>(ComboBox box, (string Label, T Value)[] choices, T selected)
    {
        box.DisplayMemberPath = "Value";
        box.SelectedValuePath = "Key";
        box.ItemsSource = choices.Select(choice => new KeyValuePair<T, string>(choice.Value, choice.Label)).ToArray();
        box.SelectedValue = selected;
    }

    private void SaveChoices()
    {
        if (_style == ChecklistNotificationStyle.Urgent && SoundChoice.SelectedValue is ChecklistUrgentSound urgent)
            _draft = _draft with { UrgentSound = urgent };
        else if (SoundChoice.SelectedValue is ChecklistWindowsNotificationSound sound)
            _draft = _style == ChecklistNotificationStyle.Standard ? _draft with { StandardSound = sound } : _draft with { PersistentSound = sound };
        if (_style == ChecklistNotificationStyle.Urgent)
            _draft = _draft with
            {
                Size = (ChecklistAlertSize)AlertSize.SelectedValue,
                Monitor = AllMonitors.IsChecked == true ? ChecklistAlertMonitor.All : ChecklistAlertMonitor.Primary,
                MonitorDeviceName = null, TextSize = 28, PulseAccent = true,
                Acknowledgment = (ChecklistAcknowledgment)Acknowledgment.SelectedValue,
                CustomSoundPath = _draft.UrgentSound == ChecklistUrgentSound.Custom ? _draft.CustomSoundPath : null,
                RepeatSound = Repeat.IsChecked == true, RepeatSeconds = (int)RepeatInterval.SelectedItem
            };
    }

    private void Style_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        SaveChoices();
        StopPreview();
        _style = (ChecklistNotificationStyle)PreviewStyle.SelectedValue;
        ShowSoundChoices();
    }

    private void ShowSoundChoices()
    {
        var urgent = _style == ChecklistNotificationStyle.Urgent;
        UrgentOptions.Visibility = urgent ? Visibility.Visible : Visibility.Collapsed;
        WindowsHint.Visibility = urgent ? Visibility.Collapsed : Visibility.Visible;
        if (urgent)
            Choices(SoundChoice, [("Alarm clock", ChecklistUrgentSound.Alarm), ("Chime", ChecklistUrgentSound.Chime),
                ("Digital beep", ChecklistUrgentSound.Digital), ("Bell", ChecklistUrgentSound.Bell), ("Silent", ChecklistUrgentSound.Silent)],
                _draft.UrgentSound == ChecklistUrgentSound.Custom ? ChecklistUrgentSound.Alarm : _draft.UrgentSound);
        else
        {
            (string, ChecklistWindowsNotificationSound)[] choices = [("Windows default", ChecklistWindowsNotificationSound.Default),
                ("Reminder", ChecklistWindowsNotificationSound.Reminder), ("Silent", ChecklistWindowsNotificationSound.Silent)];
            if (_style == ChecklistNotificationStyle.Persistent) choices = [.. choices, ("Repeating alarm", ChecklistWindowsNotificationSound.RepeatingAlarm)];
            Choices(SoundChoice, choices, _style == ChecklistNotificationStyle.Standard ? _draft.StandardSound : _draft.PersistentSound);
        }
    }

    private ChecklistNotificationOptions ReadOptions()
    {
        SaveChoices();
        return _draft;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { Options = ReadOptions(); DialogResult = true; }
    private void Preview_Click(object sender, RoutedEventArgs e) => StartPreview(soundOnly: false);
    private void PlaySound_Click(object sender, RoutedEventArgs e) => StartPreview(soundOnly: true);
    private void StartPreview(bool soundOnly)
    {
        StopPreview();
        _preview = new(this);
        if (!_preview.Show(_style, ReadOptions(), out var error, soundOnly)) ErrorText.Text = error;
    }
    private void StopPreview_Click(object sender, RoutedEventArgs e) => StopPreview();
    private void StopPreview() { _preview?.Dispose(); _preview = null; }
}
