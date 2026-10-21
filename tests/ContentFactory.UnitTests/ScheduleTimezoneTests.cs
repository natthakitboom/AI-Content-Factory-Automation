using ContentFactory.Infrastructure.Jobs;

namespace ContentFactory.UnitTests;

public sealed class ScheduleTimezoneTests
{
    private static readonly int[] Everyday = [0, 1, 2, 3, 4, 5, 6];

    [Fact]
    public void BangkokScheduleUsesUtcInstant()
    {
        var now = new DateTimeOffset(2026, 10, 21, 2, 5, 0, TimeSpan.Zero);

        var result = SchedulePlanner.Evaluate(
            "Asia/Bangkok", new TimeOnly(9, 0), Everyday, now, lastSlot: null);

        var slot = Assert.Single(result.Due);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero), slot.Instant);
        Assert.Equal("2026-10-21T09:00@Asia/Bangkok", slot.Key);
    }

    [Fact]
    public void DstMissingTimeSkippedWithWarning()
    {
        // 02:30 does not exist in New York on 2026-03-08 (clocks jump 02:00 -> 03:00).
        var now = new DateTimeOffset(2026, 3, 8, 7, 40, 0, TimeSpan.Zero);

        var result = SchedulePlanner.Evaluate(
            "America/New_York", new TimeOnly(2, 30), Everyday, now, lastSlot: null);

        Assert.Empty(result.Due);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("2026-03-08T02:30@America/New_York", skipped.Key);
    }

    [Fact]
    public void DstRepeatedTimeRunsOnce()
    {
        // 01:30 happens twice on 2026-11-01 (05:30Z and 06:30Z); only the first counts.
        var first = new DateTimeOffset(2026, 11, 1, 5, 40, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 11, 1, 6, 40, 0, TimeSpan.Zero);

        var firstPass = SchedulePlanner.Evaluate(
            "America/New_York", new TimeOnly(1, 30), Everyday, first, lastSlot: null);
        var slot = Assert.Single(firstPass.Due);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), slot.Instant);

        var secondPass = SchedulePlanner.Evaluate(
            "America/New_York", new TimeOnly(1, 30), Everyday, second, lastSlot: slot.Instant);
        Assert.Empty(secondPass.Due);
        Assert.Empty(secondPass.Missed);
    }

    [Fact]
    public void SlotsOlderThanSixtyMinutesAreMissedNotDue()
    {
        var now = new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

        var result = SchedulePlanner.Evaluate(
            "Asia/Bangkok", new TimeOnly(9, 0), Everyday, now, lastSlot: null);

        Assert.Empty(result.Due);
        Assert.NotEmpty(result.Missed);
        Assert.All(result.Missed, slot => Assert.True(now - slot.Instant > SchedulePlanner.CatchUpWindow));
    }

    [Fact]
    public void OnlyConfiguredWeekdaysProduceSlots()
    {
        // 2026-10-21 is a Wednesday (3).
        var now = new DateTimeOffset(2026, 10, 21, 2, 5, 0, TimeSpan.Zero);

        var result = SchedulePlanner.Evaluate(
            "Asia/Bangkok", new TimeOnly(9, 0), [1, 5], now, lastSlot: null);

        Assert.Empty(result.Due);
    }

    [Fact]
    public void UnknownTimezoneIsRejected()
    {
        Assert.Throws<ContentFactory.Core.Exceptions.ValidationException>(() =>
            SchedulePlanner.Evaluate(
                "Mars/Olympus", new TimeOnly(9, 0), Everyday, DateTimeOffset.UtcNow, null));
    }
}
