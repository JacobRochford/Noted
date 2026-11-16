using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class ChecklistNotificationOptionsDialog : Window
{
    internal ChecklistNotificationOptions Options { get; private set; }
    private ChecklistNotificationPreview? _preview;
    private bool _closed;
    internal ChecklistNotificationOptionsDialog(ChecklistNotificationOptions options, ChecklistNotificationStyle style = ChecklistNotificationStyle.Urgent)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AppThemeManager.ApplyTitleBar(this);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Options = options.Normalize();
        Choices(AlertSize, [("Full screen", ChecklistAlertSize.FullScreen), ("Large centered window", ChecklistAlertSize.Large), ("Percentage of screen", ChecklistAlertSize.Percentage)], Options.Size);
        Choices(AlertMonitor, [("Current foreground monitor", ChecklistAlertMonitor.Foreground), ("Primary monitor", ChecklistAlertMonitor.Primary), ("Selected monitor", ChecklistAlertMonitor.Selected), ("All monitors", ChecklistAlertMonitor.All)], Options.Monitor);
        Choices(Acknowledgment, [("Four-digit confirmation code", ChecklistAcknowledgment.Code), ("Acknowledgment button", ChecklistAcknowledgment.Button), ("Hold, then release", ChecklistAcknowledgment.Hold)], Options.Acknowledgment);
        Choices(UrgentSound, [("Built-in alarm", ChecklistUrgentSound.Alarm), ("Silent", ChecklistUrgentSound.Silent), ("Custom WAV", ChecklistUrgentSound.Custom)], Options.UrgentSound);
        Choices(StandardSound, [("Windows default", ChecklistWindowsNotificationSound.Default), ("Silent", ChecklistWindowsNotificationSound.Silent), ("Reminder", ChecklistWindowsNotificationSound.Reminder)], Options.StandardSound);
        Choices(PersistentSound, [("Windows default", ChecklistWindowsNotificationSound.Default), ("Silent", ChecklistWindowsNotificationSound.Silent), ("Reminder", ChecklistWindowsNotificationSound.Reminder), ("Repeating alarm", ChecklistWindowsNotificationSound.RepeatingAlarm)], Options.PersistentSound);
        Choices(PreviewStyle, [("Standard Windows", ChecklistNotificationStyle.Standard), ("Persistent Windows", ChecklistNotificationStyle.Persistent), ("Urgent Noted", ChecklistNotificationStyle.Urgent)], style);
        var displays = DisplayService.GetDisplays();
        Choices(SelectedMonitor, displays.Select(display => (display.DeviceName + (display.IsPrimary ? " (primary)" : ""), display.DeviceName)).ToArray(), Options.MonitorDeviceName ?? displays.FirstOrDefault()?.DeviceName ?? "");
        ScreenPercent.Text = Options.ScreenPercent.ToString(CultureInfo.InvariantCulture);
        TextSize.Text = Options.TextSize.ToString(CultureInfo.InvariantCulture);
        HoldSeconds.Text = Options.HoldSeconds.ToString(CultureInfo.InvariantCulture);
        RepeatSeconds.Text = Options.RepeatSeconds.ToString(CultureInfo.InvariantCulture);
        Pulse.IsChecked = Options.PulseAccent; Repeat.IsChecked = Options.RepeatSound; CustomSound.Text = Options.CustomSoundPath ?? "";
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
        box.DisplayMemberPath = "Value"; box.SelectedValuePath = "Key";
        box.ItemsSource = choices.Select(choice => new KeyValuePair<T, string>(choice.Value, choice.Label)).ToArray();
        box.SelectedValue = selected;
    }
    private bool ReadOptions(out ChecklistNotificationOptions options)
    {
        options = Options;
        if (!int.TryParse(ScreenPercent.Text, out var percent) || percent is < 30 or > 100 ||
            !int.TryParse(TextSize.Text, out var text) || text is < 18 or > 64 ||
            !int.TryParse(HoldSeconds.Text, out var hold) || hold is < 1 or > 10 ||
            !int.TryParse(RepeatSeconds.Text, out var repeat) || repeat is < 2 or > 3600)
        { ErrorText.Text = "Enter values within the ranges shown."; return false; }
        options = new()
        {
            Size = (ChecklistAlertSize)AlertSize.SelectedValue, ScreenPercent = percent,
            Monitor = (ChecklistAlertMonitor)AlertMonitor.SelectedValue, MonitorDeviceName = SelectedMonitor.SelectedValue as string,
            TextSize = text, PulseAccent = Pulse.IsChecked == true, Acknowledgment = (ChecklistAcknowledgment)Acknowledgment.SelectedValue,
            HoldSeconds = hold, UrgentSound = (ChecklistUrgentSound)UrgentSound.SelectedValue, CustomSoundPath = CustomSound.Text.Trim(),
            RepeatSound = Repeat.IsChecked == true, RepeatSeconds = repeat,
            StandardSound = (ChecklistWindowsNotificationSound)StandardSound.SelectedValue,
            PersistentSound = (ChecklistWindowsNotificationSound)PersistentSound.SelectedValue
        };
        ErrorText.Text = "";
        return true;
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (ReadOptions(out var options)) { Options = options; DialogResult = true; } }
    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PCM WAV audio (*.wav)|*.wav", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            try { ChecklistUrgentReminderAudio.ValidateWav(dialog.FileName); CustomSound.Text = dialog.FileName; UrgentSound.SelectedValue = ChecklistUrgentSound.Custom; }
            catch (Exception exception) when (exception is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
            { ErrorText.Text = exception.Message; }
        }
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadOptions(out var options)) return;
        StopPreview();
        _preview = new ChecklistNotificationPreview(this);
        if (!_preview.Show((ChecklistNotificationStyle)PreviewStyle.SelectedValue, options, out var error)) ErrorText.Text = error;
    }
    private void StopPreview_Click(object sender, RoutedEventArgs e) => StopPreview();
    private void StopPreview() { _preview?.Dispose(); _preview = null; }
}
