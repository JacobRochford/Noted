using System.Globalization;
using System.Security.Cryptography;

namespace Noted.Models;

// numeric values are persisted in checklist.json.
public enum ChecklistNotificationStyle
{
    Standard = 0,
    Persistent = 1,
    Urgent = 2
}

public sealed record ChecklistReminderOccurrence
{
    public Guid Id { get; init; }
    public DateTimeOffset ScheduledAtUtc { get; init; }
    public string ConfirmationCode { get; init; } = "";
    public ChecklistNotificationOptions? Options { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public DateTimeOffset? SilencedAtUtc { get; init; }

    internal bool IsValid => Id != Guid.Empty && ConfirmationCode is { Length: 4 } &&
        ConfirmationCode.All(character => character is >= '0' and <= '9');

    internal static ChecklistReminderOccurrence Create(DateTimeOffset scheduledAt, string? previousCode = null)
    {
        string code;
        do { code = RandomNumberGenerator.GetInt32(10000).ToString("D4", CultureInfo.InvariantCulture); }
        while (code == previousCode);
        return new() { Id = Guid.NewGuid(), ScheduledAtUtc = scheduledAt.ToUniversalTime(), ConfirmationCode = code };
    }
}
