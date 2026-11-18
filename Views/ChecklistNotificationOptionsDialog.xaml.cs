using System.Windows;
using System.Windows.Controls;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class ChecklistNotificationOptionsDialog : ChecklistEditorWindow
{
    internal Func<ChecklistNotificationOptions, string?>? Commit { private get; set; }
    internal ChecklistNotificationOptions Options { get; private set; }
    internal ChecklistNotificationStyle NotificationStyle => _style;
    private ChecklistNotificationOptions _draft;
    private ChecklistNotificationStyle _style;
    private ChecklistNotificationPreview? _preview;
    private bool _ready;
    private bool _closed;

    internal ChecklistNotificationOptionsDialog(ChecklistNotificationOptions options,
        ChecklistNotificationStyle style = ChecklistNotificationStyle.Urgent, bool allowStyleSelection = true)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppThemeManager.ApplyTitleBar(this);
        Options = _draft = options.Normalize();
        _style = style;
        (string, ChecklistAlertSize)[] sizes = [("Full screen", ChecklistAlertSize.FullScreen), ("Centered window", ChecklistAlertSize.Large)];
        if (Options.Size == ChecklistAlertSize.Percentage) sizes = [.. sizes, ("Custom size (saved)", ChecklistAlertSize.Percentage)];
        Choices(AlertSize, sizes, Options.Size);
        (string, ChecklistAcknowledgment)[] methods = [("Four-digit confirmation code", ChecklistAcknowledgment.Code), ("Acknowledgment button", ChecklistAcknowledgment.Button)];
        if (Options.Acknowledgment == ChecklistAcknowledgment.Hold) methods = [.. methods, ("Hold to acknowledge (saved)", ChecklistAcknowledgment.Hold)];
        Choices(Acknowledgment, methods, Options.Acknowledgment);
        Choices(PreviewStyle, [("Standard Windows", ChecklistNotificationStyle.Standard), ("Persistent Windows", ChecklistNotificationStyle.Persistent), ("Urgent Noted", ChecklistNotificationStyle.Urgent)], style);
        if (!allowStyleSelection)
        {
            StylePicker.Visibility = Visibility.Collapsed;
            FixedStyle.Text = ((KeyValuePair<ChecklistNotificationStyle, string>)PreviewStyle.SelectedItem).Value;
            FixedStyle.Visibility = Visibility.Visible;
        }
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
        {
            var monitor = Options.Monitor == ChecklistAlertMonitor.All ? ChecklistAlertMonitor.Primary : Options.Monitor;
            if (AllMonitors.IsChecked == true) monitor = ChecklistAlertMonitor.All;
            _draft = _draft with
            {
                UrgentSound = urgent,
                CustomSoundPath = urgent == ChecklistUrgentSound.Custom ? Options.CustomSoundPath : null,
                Size = (ChecklistAlertSize)AlertSize.SelectedValue,
                Monitor = monitor,
                MonitorDeviceName = monitor == Options.Monitor ? Options.MonitorDeviceName : null,
                Acknowledgment = (ChecklistAcknowledgment)Acknowledgment.SelectedValue,
                RepeatSound = Repeat.IsChecked == true,
                RepeatSeconds = (int)RepeatInterval.SelectedItem
            };
        }
        else if (SoundChoice.SelectedValue is ChecklistWindowsNotificationSound sound)
            _draft = _style == ChecklistNotificationStyle.Standard ? _draft with { StandardSound = sound } : _draft with { PersistentSound = sound };
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
        PlaySound.Visibility = urgent ? Visibility.Visible : Visibility.Collapsed;
        WindowsHint.Visibility = urgent ? Visibility.Collapsed : Visibility.Visible;
        if (urgent)
        {
            (string, ChecklistUrgentSound)[] choices = [("Alarm clock", ChecklistUrgentSound.Alarm), ("Chime", ChecklistUrgentSound.Chime),
                ("Digital beep", ChecklistUrgentSound.Digital), ("Bell", ChecklistUrgentSound.Bell), ("Silent", ChecklistUrgentSound.Silent)];
            if (Options.UrgentSound == ChecklistUrgentSound.Custom) choices = [.. choices, ("Custom WAV (saved)", ChecklistUrgentSound.Custom)];
            Choices(SoundChoice, choices, _draft.UrgentSound);
        }
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var options = ReadOptions();
        if (Commit?.Invoke(options) is { } error)
        {
            ShowError(error);
            return;
        }
        Options = options;
        Close();
    }
    private void Preview_Click(object sender, RoutedEventArgs e) => StartPreview(soundOnly: false);
    private void PlaySound_Click(object sender, RoutedEventArgs e) => StartPreview(soundOnly: true);
    private void StartPreview(bool soundOnly)
    {
        StopPreview();
        _preview = new(this);
        if (!_preview.Show(_style, ReadOptions(), out var error, soundOnly)) ShowError(error);
    }
    private void StopPreview_Click(object sender, RoutedEventArgs e) => StopPreview();
    private void StopPreview() { _preview?.Dispose(); _preview = null; ShowError(null); }
    private void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }
}
