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

    private static TimeWindow Window(int day, int startHour, int endHour) => new(
        new(2026, 10, day, startHour, 0, 0, DateTimeKind.Utc),
        new(2026, 10, day, endHour, 0, 0, DateTimeKind.Utc));
}
