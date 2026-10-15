using Xunit;

namespace BodyBalance.Api.Tests;

public class PractitionerAvailabilityTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly DateTime Today = new(2026, 1, 1);

    [Fact]
    public void SplitHoursAreOrderedAndEffectiveToday()
    {
        var hours = PractitionerAvailability.ValidateHours([
            new(2, "13:00", "17:00"), new(1, "09:00", "12:00"), new(2, "09:00", "12:00")], Today);
        Assert.Equal([1, 2, 2], hours.Select(h => h.DayOfWeek).ToArray());
        Assert.All(hours, h => { Assert.Equal(Today, h.EffectiveFrom); Assert.Null(h.EffectiveThrough); });
        Assert.Equal(TimeSpan.FromHours(9), hours[1].StartTimeLocal);
    }

    [Theory]
    [InlineData("09:00", "17:00")]
    [InlineData("09:00:00", "17:00:00")]
    public void BrowserTimeFormatsWorkForHoursAndPartialDayTimeOff(string start, string end)
    {
        var hours = PractitionerAvailability.ValidateHours([new(1, start, end)], Today);
        Assert.Equal(TimeSpan.FromHours(9), hours[0].StartTimeLocal);
        Assert.Equal(TimeSpan.FromHours(17), hours[0].EndTimeLocal);
        var window = PractitionerAvailability.ValidateTimeOff(new(Today, Today, false, start, end), Central, Today);
        Assert.Equal(TimeSpan.FromHours(8), window.EndAtUtc - window.StartAtUtc);
    }

    [Theory]
    [InlineData(1, "09:00:30", "17:00")]
    [InlineData(-1, "09:00", "17:00")]
    [InlineData(7, "09:00", "17:00")]
    [InlineData(1, "17:00", "09:00")]
    [InlineData(1, "09:00", "09:00")]
    [InlineData(1, "24:00", "17:00")]
    [InlineData(1, "invalid", "17:00")]
    public void InvalidWorkingPeriodsAreRejected(int day, string start, string end) =>
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateHours([new(day, start, end)], Today));

    [Fact]
    public void OverlapsAreRejectedButAdjacentPeriodsAreAllowed()
    {
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateHours([
            new(1, "09:00", "13:00"), new(1, "12:00", "17:00")], Today));
        Assert.Equal(2, PractitionerAvailability.ValidateHours([
            new(1, "09:00", "12:00"), new(1, "12:00", "17:00")], Today).Length);
        Assert.Empty(PractitionerAvailability.ValidateHours([], Today));
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateHours(null, Today));
    }

    [Theory]
    [InlineData(3, 8, 23)]
    [InlineData(11, 1, 25)]
    public void AllDayTimeOffUsesLocalMidnightsAcrossDaylightSaving(int month, int day, int duration)
    {
        var date = new DateTime(2026, month, day);
        var window = PractitionerAvailability.ValidateTimeOff(new(date, date, true, null, null), Central, Today);
        Assert.Equal(TimeSpan.FromHours(duration), window.EndAtUtc - window.StartAtUtc);
        Assert.Equal(date, TimeZoneInfo.ConvertTimeFromUtc(window.StartAtUtc, Central));
        Assert.Equal(date.AddDays(1), TimeZoneInfo.ConvertTimeFromUtc(window.EndAtUtc, Central));
    }

    [Fact]
    public void AllDayRangeIncludesLastDay()
    {
        var window = PractitionerAvailability.ValidateTimeOff(new(Today, Today.AddDays(2), true, null, null), Central, Today);
        Assert.Equal(TimeSpan.FromDays(3), window.EndAtUtc - window.StartAtUtc);
    }

    [Theory]
    [InlineData(3, 8, "02:30", "04:00")]
    [InlineData(11, 1, "01:30", "03:00")]
    [InlineData(3, 8, "01:00", "02:30")]
    public void AmbiguousOrMissingClockTimesAreRejected(int month, int day, string start, string end)
    {
        var date = new DateTime(2026, month, day);
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateTimeOff(new(date, date, false, start, end), Central, Today));
    }

    [Fact]
    public void InvalidTimeOffDatesAndReversedHoursAreRejected()
    {
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateTimeOff(new(Today.AddDays(-1), Today, true, null, null), Central, Today));
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateTimeOff(new(Today, Today.AddDays(-1), true, null, null), Central, Today));
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateTimeOff(new(Today, Today, false, "17:00", "09:00"), Central, Today));
        Assert.Throws<BookingException>(() => PractitionerAvailability.ValidateTimeOff(new(Today.AddHours(1), Today, true, null, null), Central, Today));
    }

    [Fact]
    public void PartialDayTimeOffRemovesOnlyTheBlockedSlots()
    {
        var date = new DateTime(2026, 1, 5);
        var rules = PractitionerAvailability.ValidateHours([new(1, "09:00", "17:00")], Today);
        var block = PractitionerAvailability.ValidateTimeOff(new(date, date, false, "12:00", "13:00"), Central, Today);
        var windows = Availability.Calculate(Today, Central, rules, [], [block], Today.AddDays(-1));
        var slots = Availability.Slots(windows, Central, 60, 30);
        Assert.DoesNotContain(slots, s => s.StartAtUtc < block.EndAtUtc && s.EndAtUtc > block.StartAtUtc);
        Assert.Contains(slots, s => s.EndAtUtc == block.StartAtUtc);
        Assert.Contains(slots, s => s.StartAtUtc == block.EndAtUtc);
    }

    [Fact]
    public void VersionDetectsConcurrentHoursAndEffectiveDateChanges()
    {
        WeeklyHours[] original = [new(1, TimeSpan.FromHours(9), TimeSpan.FromHours(17), Today, null)];
        Assert.Equal(PractitionerAvailability.Version(original), PractitionerAvailability.Version(original.ToArray()));
        Assert.NotEqual(PractitionerAvailability.Version(original), PractitionerAvailability.Version([original[0] with { EndTimeLocal = TimeSpan.FromHours(16) }]));
        Assert.NotEqual(PractitionerAvailability.Version(original), PractitionerAvailability.Version([original[0] with { EffectiveThrough = Today.AddDays(7) }]));
    }
}
