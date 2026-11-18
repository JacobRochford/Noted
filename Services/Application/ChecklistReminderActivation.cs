using System.Globalization;
using System.Text.Json;

namespace Noted.Services;

internal sealed record ChecklistReminderActivation(Guid? TaskId, DateTimeOffset? BatchUtc, ChecklistRepeatDelivery? RepeatDelivery = null)
{
    internal const string PipeName = "Noted.ChecklistReminder";

    internal static bool IsNotificationLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal));

    internal static IReadOnlyList<string> FileArguments(string[] arguments) =>
        IsNotificationLaunch(arguments) ? [] : arguments;

    internal static bool TryParse(IReadOnlyDictionary<string, string> arguments, out ChecklistReminderActivation? activation)
    {
        activation = null;
        if (!arguments.TryGetValue("action", out var action) || action != "checklistReminder") return false;
        var hasSeries = arguments.TryGetValue("seriesId", out var series);
        var hasSlot = arguments.TryGetValue("slotUtc", out var slot);
        if (arguments.TryGetValue("taskId", out var task) && Guid.TryParse(task, out var id) && id != Guid.Empty &&
            !arguments.ContainsKey("batchUtc"))
        {
            if (!hasSeries && !hasSlot) activation = new(id, null);
            else if (hasSeries && hasSlot && Guid.TryParse(series, out var seriesId) && seriesId != Guid.Empty &&
                long.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks > 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                activation = new(id, null, new(seriesId, new DateTimeOffset(ticks, TimeSpan.Zero)));
        }
        else if (!hasSeries && !hasSlot && !arguments.ContainsKey("taskId") && arguments.TryGetValue("batchUtc", out var batch) &&
                 DateTimeOffset.TryParseExact(batch, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
            activation = new(null, utc.ToUniversalTime());
        return activation is not null;
    }

    internal string Serialize() => JsonSerializer.Serialize(this);

    internal static ChecklistReminderActivation? Deserialize(string payload)
    {
        if (payload.Length > 512) return null;
        try
        {
            var result = JsonSerializer.Deserialize<ChecklistReminderActivation>(payload);
            return result is not null &&
                ((result.TaskId is { } id && id != Guid.Empty && result.BatchUtc is null &&
                    (result.RepeatDelivery is not { } repeat || repeat.SeriesId != Guid.Empty && repeat.SlotUtc.UtcTicks > 0)) ||
                 (result.TaskId is null && result.BatchUtc is not null && result.RepeatDelivery is null)) ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
