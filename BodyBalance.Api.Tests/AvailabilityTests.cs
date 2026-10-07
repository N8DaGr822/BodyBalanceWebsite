using Xunit;

namespace BodyBalance.Api.Tests;

public class AvailabilityTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
    private static readonly DateTime Before = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnconfiguredScheduleHasNoAvailability()
    {
        Assert.Empty(Availability.Calculate(new(2026, 10, 1), Central, [], [], [], Before));
    }

    [Fact]
    public void BlockingIntervalSplitsAddedHours()
    {
        var result = Availability.Calculate(new(2026, 10, 1), Central, [],
            [Window(1, 14, 18)], [Window(1, 15, 16)], Before);
        Assert.Equal(new[] { Window(1, 14, 15), Window(1, 16, 18) }, result);
    }

    [Fact]
    public void TouchingReservationDoesNotBlockAvailableWindow()
    {
        var result = Availability.Calculate(new(2026, 10, 1), Central, [],
            [Window(1, 14, 15)], [Window(1, 15, 16)], Before);
        Assert.Equal(new[] { Window(1, 14, 15) }, result);
    }

    [Fact]
    public void OverlappingAndAdjacentHoursAreMerged()
    {
        var result = Availability.Calculate(new(2026, 10, 1), Central, [],
            [Window(1, 14, 16), Window(1, 15, 17), Window(1, 17, 18)], [], Before);
        Assert.Equal(new[] { Window(1, 14, 18) }, result);
    }

    [Fact]
    public void FullyBlockedAndPastHoursAreRemoved()
    {
        Assert.Empty(Availability.Calculate(new(2026, 10, 1), Central, [],
            [Window(1, 14, 18)], [Window(1, 13, 19)], Before));
        Assert.Empty(Availability.Calculate(new(2026, 10, 1), Central, [],
            [Window(1, 14, 18)], [], new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WeeklyHoursHonorEffectiveDatesAndCentralDaylightSaving()
    {
        WeeklyHours[] rules = [new(0, new(9, 0, 0), new(10, 0, 0), new(2026, 3, 1), new(2026, 3, 8))];
        var result = Availability.Calculate(new(2026, 3, 1), Central, rules, [], [], Before);
        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc), result[0].StartAtUtc);
        Assert.Equal(new DateTime(2026, 3, 8, 14, 0, 0, DateTimeKind.Utc), result[1].StartAtUtc);
    }

    [Theory]
    [InlineData(3, 8, 2)]
    [InlineData(11, 1, 1)]
    public void InvalidOrAmbiguousLocalBoundaryIsNotPublished(int month, int day, int hour)
    {
        var date = new DateTime(2026, month, day);
        WeeklyHours[] rules = [new(0, new(hour, 30, 0), new(4, 0, 0), date, date)];
        Assert.Empty(Availability.Calculate(new(2026, month, 1), Central, rules, [], [], Before));
    }

    [Fact]
    public void AddedHoursAreClippedToRequestedLocalMonth()
    {
        var result = Availability.Calculate(new(2026, 10, 1), Central, [],
            [new(new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc))], [], Before);
        Assert.Equal(new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc), Assert.Single(result).StartAtUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), result[0].EndAtUtc);
    }

    [Theory]
    [InlineData(30, 16, 22, 30)]
    [InlineData(60, 15, 22, 0)]
    public void MarySessionsStartOnHalfHoursAndFinishBySix(int duration, int count, int lastHour, int lastMinute)
    {
        var slots = Availability.Slots([Window(9, 15, 23)], Central, duration, 30);
        Assert.Equal(count, slots.Count);
        Assert.Equal(new DateTime(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc), slots[0].StartAtUtc);
        Assert.Equal(new DateTime(2026, 10, 9, lastHour, lastMinute, 0, DateTimeKind.Utc), slots[^1].StartAtUtc);
        Assert.Equal(new DateTime(2026, 10, 9, 23, 0, 0, DateTimeKind.Utc), slots[^1].EndAtUtc);
    }

    [Theory]
    [InlineData(0, 15, 0)]
    [InlineData(1, 15, 30)]
    public void FortyEightHourNoticeIncludesBoundaryAndRoundsUpToClockGrid(int seconds, int hour, int minute)
    {
        var now = new DateTime(2026, 10, 7, 15, 0, seconds, DateTimeKind.Utc);
        var windows = Availability.Calculate(new(2026, 10, 1), Central, [], [Window(9, 15, 23)], [], now, 48);
        var slots = Availability.Slots(windows, Central, 60, 30);
        Assert.Equal(new DateTime(2026, 10, 9, hour, minute, 0, DateTimeKind.Utc), slots[0].StartAtUtc);
        Assert.All(slots, slot => Assert.True(slot.StartAtUtc >= now.AddHours(48)));
    }

    [Fact]
    public void MaryWeeklyHoursExcludeSundayAndMonday()
    {
        var rules = Enumerable.Range(2, 5).Select(day => new WeeklyHours(day, new(10, 0, 0), new(18, 0, 0), new(2026, 10, 1), null));
        var windows = Availability.Calculate(new(2026, 10, 1), Central, rules, [], [], Before, 48);
        Assert.Equal(23, windows.Count);
        Assert.All(windows, window =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(window.StartAtUtc, Central);
            Assert.InRange((int)local.DayOfWeek, 2, 6);
            Assert.Equal(10, local.Hour);
            Assert.Equal(18, TimeZoneInfo.ConvertTimeFromUtc(window.EndAtUtc, Central).Hour);
        });
    }

    [Fact]
    public void SlotsCannotSpanBlockedTimeOrUseItsUnalignedEndAsAStart()
    {
        var block = new TimeWindow(new(2026, 10, 9, 16, 15, 0, DateTimeKind.Utc), new(2026, 10, 9, 17, 10, 0, DateTimeKind.Utc));
        var windows = Availability.Calculate(new(2026, 10, 1), Central, [], [Window(9, 15, 23)], [block], Before);
        var slots = Availability.Slots(windows, Central, 60, 30);
        Assert.Equal(new DateTime(2026, 10, 9, 17, 30, 0, DateTimeKind.Utc), slots[1].StartAtUtc);
        Assert.All(slots, slot => Assert.True(slot.EndAtUtc <= block.StartAtUtc || slot.StartAtUtc >= block.EndAtUtc));
    }

    [Theory]
    [InlineData(3, 6, 16, 15)]
    [InlineData(10, 30, 15, 16)]
    public void NoticeUsesElapsedHoursAcrossDaylightSaving(int month, int day, int nowHour, int openingHour)
    {
        var now = new DateTime(2026, month, day, nowHour, 0, 0, DateTimeKind.Utc);
        var date = new DateTime(2026, month, day).AddDays(2);
        WeeklyHours[] rules = [new((int)date.DayOfWeek, new(10, 0, 0), new(18, 0, 0), date, date)];
        var windows = Availability.Calculate(new(date.Year, date.Month, 1), Central, rules, [], [], now, 48);
        var slots = Availability.Slots(windows, Central, 60, 30);
        Assert.Equal(new DateTime(date.Year, date.Month, date.Day, Math.Max(nowHour, openingHour), 0, 0, DateTimeKind.Utc), slots[0].StartAtUtc);
        Assert.All(slots, slot => Assert.True(slot.StartAtUtc >= now.AddHours(48)));
    }

    [Fact]
    public void SlotsExcludeAmbiguousLocalStartTimes()
    {
        var slots = Availability.Slots([new(new(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc))], Central, 30, 30);
        Assert.DoesNotContain(slots, slot => TimeZoneInfo.ConvertTimeFromUtc(slot.StartAtUtc, Central).Hour == 1);
    }

    private static TimeWindow Window(int day, int startHour, int endHour) => new(
        new(2026, 10, day, startHour, 0, 0, DateTimeKind.Utc),
        new(2026, 10, day, endHour, 0, 0, DateTimeKind.Utc));
}
