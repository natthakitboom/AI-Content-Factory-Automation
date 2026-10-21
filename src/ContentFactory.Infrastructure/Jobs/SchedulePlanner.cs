using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Jobs;

/// <param name="Key">Local date, time and zone; stable across clock rollbacks.</param>
/// <param name="Skipped">True when the local time does not exist that day (DST gap).</param>
public sealed record ScheduleSlot(string Key, DateTimeOffset Instant, DateOnly LocalDate, bool Skipped);

public sealed record SlotEvaluation(
    IReadOnlyList<ScheduleSlot> Due,
    IReadOnlyList<ScheduleSlot> Missed,
    IReadOnlyList<ScheduleSlot> Skipped);

/// <summary>
/// Pure schedule policy. A slot is a local date and time in an IANA zone, resolved to a UTC
/// instant. A slot that is at most 60 minutes old may run once; an older one is reported as
/// missed and never replayed, so waking a sleeping Mac cannot burst a day of work. A local
/// time that does not exist is skipped with a warning, and a repeated local time uses its
/// first occurrence only.
/// </summary>
public static class SchedulePlanner
{
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(60);

    public static TimeZoneInfo ResolveZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ValidationException(
                "SCHEDULE_TIMEZONE_INVALID", "The timezone must be a known IANA zone such as Asia/Bangkok.");
        }
    }

    public static SlotEvaluation Evaluate(
        string timezone,
        TimeOnly localTime,
        IReadOnlyCollection<int> daysOfWeek,
        DateTimeOffset now,
        DateTimeOffset? lastSlot,
        int lookbackDays = 7)
    {
        var zone = ResolveZone(timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var due = new List<ScheduleSlot>();
        var missed = new List<ScheduleSlot>();
        var skipped = new List<ScheduleSlot>();

        for (var offset = -lookbackDays; offset <= 0; offset++)
        {
            var date = today.AddDays(offset);
            if (!daysOfWeek.Contains((int)date.DayOfWeek))
            {
                continue;
            }

            var local = date.ToDateTime(localTime, DateTimeKind.Unspecified);
            var key = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{date:yyyy-MM-dd}T{localTime:HH:mm}@{timezone}");
            ScheduleSlot slot;
            if (zone.IsInvalidTime(local))
            {
                // The nominal instant uses the offset in force just before the gap.
                var before = zone.GetUtcOffset(local.AddHours(-4));
                slot = new ScheduleSlot(key, new DateTimeOffset(local, before), date, Skipped: true);
            }
            else
            {
                var utcOffset = zone.IsAmbiguousTime(local)
                    ? zone.GetAmbiguousTimeOffsets(local).Max()
                    : zone.GetUtcOffset(local);
                slot = new ScheduleSlot(key, new DateTimeOffset(local, utcOffset), date, Skipped: false);
            }

            if (slot.Instant > now || (lastSlot is { } last && slot.Instant <= last))
            {
                continue;
            }

            if (slot.Skipped)
            {
                skipped.Add(slot);
            }
            else if (now - slot.Instant <= CatchUpWindow)
            {
                due.Add(slot);
            }
            else
            {
                missed.Add(slot);
            }
        }

        return new SlotEvaluation(due, missed, skipped);
    }
}
