using System.Globalization;

namespace Noted.Helpers;

internal static class ChecklistReminderTime
{
    internal static DateTime DefaultLocalTime(DateTime? dueDate, DateTimeOffset now, TimeZoneInfo zone, TimeSpan? dueTime = null)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var candidate = dueDate?.Date.Add(dueTime ?? TimeSpan.FromHours(9));
        if (candidate is { } due && due > localNow && !zone.IsInvalidTime(due) && !zone.IsAmbiguousTime(due))
            return due;
        var next = localNow.AddMinutes(30 - localNow.Minute % 30);
        next = new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, 0);
        while (zone.IsInvalidTime(next) || zone.IsAmbiguousTime(next)) next = next.AddMinutes(30);
        return next;
    }

    internal static bool TryGetUtc(DateTime? date, string time, DateTimeOffset now, TimeZoneInfo zone,
        out DateTimeOffset? utc, out string? error)
    {
        if (TimeOnly.TryParseExact(time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
            return TryGetUtc(date, clock.ToTimeSpan(), now, zone, out utc, out error);
        utc = null;
        error = "Choose a valid date and time.";
        return false;
    }

    internal static bool TryGetUtc(DateTime? date, TimeSpan? time, DateTimeOffset now, TimeZoneInfo zone,
        out DateTimeOffset? utc, out string? error)
    {
        utc = null;
        error = null;
        if (date is null || time is null || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            error = "Choose a valid date and time.";
        else
        {
            var local = DateTime.SpecifyKind(date.Value.Date.Add(time.Value), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
                error = "This local time does not exist because the clocks change. Choose another time.";
            else if (zone.IsAmbiguousTime(local))
                error = "This local time occurs twice because the clocks change. Choose another time.";
            else
            {
                utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc <= now) error = "Choose a notification time in the future.";
            }
        }
        return error is null;
    }
}
