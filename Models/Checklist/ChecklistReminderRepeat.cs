namespace Noted.Models;

public sealed record ChecklistReminderRepeatPolicy(int IntervalMinutes, int? AdditionalAlertLimit)
{
    internal bool IsValid => IntervalMinutes is >= 1 and <= 1440 && AdditionalAlertLimit is null or >= 1 and <= 10;
}

public sealed record ChecklistReminderRepeat(
    Guid Id, ChecklistReminderRepeatPolicy Policy, DateTimeOffset? NextSlotUtc,
    DateTimeOffset? LastSlotUtc = null, long AdditionalAlertsSent = 0)
{
    internal static ChecklistReminderRepeat Start(ChecklistReminderRepeatPolicy policy, DateTimeOffset firstSlot) =>
        new(Guid.NewGuid(), policy, firstSlot.ToUniversalTime());

    internal bool IsValid(DateTimeOffset firstSlot, bool firstDelivered)
    {
        if (Id == Guid.Empty || Policy is not { IsValid: true } || AdditionalAlertsSent < 0 ||
            AdditionalAlertsSent > DateTimeOffset.MaxValue.UtcTicks / TimeSpan.TicksPerMinute) return false;
        if (!firstDelivered) return LastSlotUtc is null && NextSlotUtc == firstSlot && AdditionalAlertsSent == 0;
        if (LastSlotUtc is not { } last || last < firstSlot || (AdditionalAlertsSent == 0) != (last == firstSlot)) return false;
        return NextSlotUtc is not { } next || next > last && HasRemaining(Policy, AdditionalAlertsSent) &&
            (next - firstSlot).Ticks % TimeSpan.FromMinutes(Policy.IntervalMinutes).Ticks == 0;
    }

    internal ChecklistReminderRepeat Accept(DateTimeOffset firstSlot, DateTimeOffset slot, DateTimeOffset acceptedAt)
    {
        var sent = AdditionalAlertsSent + (LastSlotUtc.HasValue ? 1 : 0);
        // tolerate ordinary timer latency while keeping a full interval after a late catch-up.
        var boundary = Math.Max(slot.UtcTicks, acceptedAt.UtcTicks - TimeSpan.FromSeconds(5).Ticks);
        return this with
        {
            LastSlotUtc = slot,
            AdditionalAlertsSent = sent,
            NextSlotUtc = HasRemaining(Policy, sent) ? NextOnCadence(firstSlot, Policy, boundary, fullInterval: true) : null
        };
    }

    internal ChecklistReminderRepeat ChangePolicy(ChecklistReminderRepeatPolicy policy, DateTimeOffset firstSlot, DateTimeOffset now)
    {
        if (Policy == policy) return this;
        if (LastSlotUtc is not { } last) return this with { Policy = policy };

        var next = NextSlotUtc;
        if (!HasRemaining(policy, AdditionalAlertsSent)) next = null;
        else if (policy.IntervalMinutes != Policy.IntervalMinutes || next is null)
            next = NextOnCadence(firstSlot, policy, Math.Max(last.UtcTicks, now.UtcTicks), fullInterval: false);
        return this with { Policy = policy, NextSlotUtc = next };
    }

    private static bool HasRemaining(ChecklistReminderRepeatPolicy policy, long sent) =>
        policy.AdditionalAlertLimit is not { } limit || sent < limit;

    private static DateTimeOffset? NextOnCadence(DateTimeOffset first, ChecklistReminderRepeatPolicy policy, long boundary, bool fullInterval)
    {
        var interval = TimeSpan.FromMinutes(policy.IntervalMinutes).Ticks;
        var elapsed = boundary - first.UtcTicks;
        var intervals = elapsed / interval + 1;
        if (fullInterval && elapsed % interval != 0) intervals++;
        var ticks = first.UtcTicks + intervals * interval;
        return ticks <= DateTimeOffset.MaxValue.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }
}
