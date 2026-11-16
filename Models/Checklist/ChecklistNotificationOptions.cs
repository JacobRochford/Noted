namespace Noted.Models;

// numeric values are persistence contracts.
public enum ChecklistAlertSize { FullScreen = 0, Large = 1, Percentage = 2 }
public enum ChecklistAlertMonitor { Foreground = 0, Primary = 1, Selected = 2, All = 3 }
public enum ChecklistAcknowledgment { Code = 0, Button = 1, Hold = 2 }
public enum ChecklistUrgentSound { Alarm = 0, Silent = 1, Custom = 2 }
public enum ChecklistWindowsNotificationSound { Default = 0, Silent = 1, Reminder = 2, RepeatingAlarm = 3 }

public sealed record ChecklistNotificationOptions
{
    public ChecklistAlertSize Size { get; init; } = ChecklistAlertSize.FullScreen;
    public int ScreenPercent { get; init; } = 80;
    public ChecklistAlertMonitor Monitor { get; init; } = ChecklistAlertMonitor.Foreground;
    public string? MonitorDeviceName { get; init; }
    public bool PulseAccent { get; init; } = true;
    public int TextSize { get; init; } = 28;
    public ChecklistUrgentSound UrgentSound { get; init; } = ChecklistUrgentSound.Alarm;
    public string? CustomSoundPath { get; init; }
    public bool RepeatSound { get; init; } = true;
    public int RepeatSeconds { get; init; } = 10;
    public ChecklistAcknowledgment Acknowledgment { get; init; } = ChecklistAcknowledgment.Code;
    public int HoldSeconds { get; init; } = 2;
    public ChecklistWindowsNotificationSound StandardSound { get; init; } = ChecklistWindowsNotificationSound.Default;
    public ChecklistWindowsNotificationSound PersistentSound { get; init; } = ChecklistWindowsNotificationSound.Reminder;

    internal ChecklistNotificationOptions Normalize() => this with
    {
        Size = Enum.IsDefined(Size) ? Size : ChecklistAlertSize.FullScreen,
        Monitor = Enum.IsDefined(Monitor) ? Monitor : ChecklistAlertMonitor.Foreground,
        Acknowledgment = Enum.IsDefined(Acknowledgment) ? Acknowledgment : ChecklistAcknowledgment.Code,
        UrgentSound = Enum.IsDefined(UrgentSound) ? UrgentSound : ChecklistUrgentSound.Alarm,
        ScreenPercent = Math.Clamp(ScreenPercent, 30, 100), TextSize = Math.Clamp(TextSize, 18, 64),
        RepeatSeconds = Math.Clamp(RepeatSeconds, 2, 3600), HoldSeconds = Math.Clamp(HoldSeconds, 1, 10),
        StandardSound = Enum.IsDefined(StandardSound) && StandardSound != ChecklistWindowsNotificationSound.RepeatingAlarm
            ? StandardSound : ChecklistWindowsNotificationSound.Default,
        PersistentSound = Enum.IsDefined(PersistentSound) ? PersistentSound : ChecklistWindowsNotificationSound.Reminder
    };
}
