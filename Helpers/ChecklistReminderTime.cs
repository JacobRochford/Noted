using System.Globalization;

namespace Noted.Helpers;

internal static class ChecklistReminderTime
{
    internal static DateTime DefaultLocalTime(DateTime? dueDate, DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var candidate = dueDate?.Date.AddHours(9);
        if (candidate is { } due && due > localNow && !zone.IsInvalidTime(due) && !zone.IsAmbiguousTime(due))
            return due;
        return TimeZoneInfo.ConvertTime(now.AddHours(1), zone).DateTime;
    }

    internal static bool TryGetUtc(DateTime? date, string time, DateTimeOffset now, TimeZoneInfo zone,
        out DateTimeOffset? utc, out string? error)
    {
        utc = null;
        error = null;
        if (date is null || !TimeOnly.TryParseExact(time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var clock))
            error = "Enter a valid date and a 24-hour time, such as 14:30.";
        else
        {
            var local = DateTime.SpecifyKind(date.Value.Date.Add(clock.ToTimeSpan()), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
                error = "This local time does not exist because the clocks change. Choose another time.";
            else if (zone.IsAmbiguousTime(local))
                error = "This local time occurs twice because the clocks change. Choose another time.";
            else
            {
                utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc <= now) error = "Choose a reminder time in the future.";
            }
        }
        return error is null;
    }
}
