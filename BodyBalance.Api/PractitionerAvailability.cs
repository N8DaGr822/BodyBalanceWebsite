using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BodyBalance.Api;

public sealed record WorkingPeriod(int DayOfWeek, string Start, string End);
public sealed record WeeklyHoursChange(string? Version, WorkingPeriod[]? Hours);
public sealed record TimeOffChange(DateTime StartDate, DateTime EndDate, bool AllDay, string? StartTime, string? EndTime);
public sealed record TimeOffEntry(int Id, DateTime StartAtUtc, DateTime EndAtUtc);
public sealed record AvailabilitySettings(string TimeZoneId, DateTime Today, string Version, WeeklyHours[] Rules, TimeOffEntry[] TimeOff);

public static class PractitionerAvailability
{
    public static string Version(IEnumerable<WeeklyHours> rules) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rules)));

    public static WeeklyHours[] ValidateHours(WorkingPeriod[]? hours, DateTime today)
    {
        if (hours is null || hours.Length > 21) throw new BookingException("Provide up to three working periods per day.");
        var rules = hours.Select(h =>
        {
            if (h is null || h.DayOfWeek is < 0 or > 6) throw new BookingException("Choose a valid day of the week.");
            var start = ParseTime(h.Start);
            var end = ParseTime(h.End);
            if (start >= end) throw new BookingException("Working hours must end after they start on the same day.");
            return new WeeklyHours(h.DayOfWeek, start, end, today, null);
        }).OrderBy(h => h.DayOfWeek).ThenBy(h => h.StartTimeLocal).ToArray();
        foreach (var day in rules.GroupBy(r => r.DayOfWeek))
        {
            if (day.Count() > 3) throw new BookingException("Provide up to three working periods per day.");
            var periods = day.ToArray();
            for (var i = 1; i < periods.Length; i++)
                if (periods[i].StartTimeLocal < periods[i - 1].EndTimeLocal)
                    throw new BookingException("Working periods on the same day cannot overlap.");
        }
        return rules;
    }

    public static TimeWindow ValidateTimeOff(TimeOffChange change, TimeZoneInfo zone, DateTime today)
    {
        if (change.StartDate.TimeOfDay != TimeSpan.Zero || change.EndDate.TimeOfDay != TimeSpan.Zero
            || change.StartDate.Date < today || change.StartDate.Year > 2100 || change.EndDate.Year > 2100
            || change.EndDate < change.StartDate)
            throw new BookingException("Choose a date range starting today or later (through 2100).");
        var start = DateTime.SpecifyKind(change.StartDate.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(change.EndDate.Date, DateTimeKind.Unspecified);
        if (change.AllDay) end = end.AddDays(1);
        else
        {
            start = start.Add(ParseTime(change.StartTime));
            end = end.Add(ParseTime(change.EndTime));
        }
        if (end <= start) throw new BookingException("Time off must end after it starts.");
        if (zone.IsInvalidTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(start) || zone.IsAmbiguousTime(end))
            throw new BookingException("Choose times outside the daylight-saving clock change.");
        return new(Availability.ToUtc(start, zone), Availability.ToUtc(end, zone));
    }

    // Blazor Time Inputs: Change events append zero seconds even when the control edits only hours and minutes.
    private static TimeSpan ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm':00'"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToTimeSpan() : throw new BookingException("Enter a valid time in hours and minutes.");
}
