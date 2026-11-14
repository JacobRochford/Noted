using System.Globalization;
using System.Text.Json;

namespace Noted.Services;

internal sealed record ChecklistReminderActivation(Guid? TaskId, DateTimeOffset? BatchUtc)
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
        if (arguments.TryGetValue("taskId", out var task) && Guid.TryParse(task, out var id) && id != Guid.Empty &&
            !arguments.ContainsKey("batchUtc"))
            activation = new(id, null);
        else if (!arguments.ContainsKey("taskId") && arguments.TryGetValue("batchUtc", out var batch) &&
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
                ((result.TaskId is { } id && id != Guid.Empty && result.BatchUtc is null) ||
                 (result.TaskId is null && result.BatchUtc is not null)) ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
