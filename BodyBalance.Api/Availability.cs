namespace BodyBalance.Api;

public record Practitioner(int PractitionerId, string DisplayName, string TimeZoneId,
    int MinimumNoticeHours = 0, int SlotIntervalMinutes = 30, string? Location = null);
public record Service(int ServiceId, string Name, int DurationMinutes, decimal Price,
    string CurrencyCode, string? Description, decimal? RegularPrice, decimal? FirstTimeClientPrice);
public record TimeWindow(DateTime StartAtUtc, DateTime EndAtUtc);
public record WeeklyHours(int DayOfWeek, TimeSpan StartTimeLocal, TimeSpan EndTimeLocal,
    DateTime EffectiveFrom, DateTime? EffectiveThrough);

public static class Availability
{
    public static List<TimeWindow> Calculate(DateTime month, TimeZoneInfo zone,
        IEnumerable<WeeklyHours> rules, IEnumerable<TimeWindow> added,
        IEnumerable<TimeWindow> blocked, DateTime nowUtc, int minimumNoticeHours = 0)
    {
        var rangeStart = ToUtc(month, zone);
        var rangeEnd = ToUtc(month.AddMonths(1), zone);
        var windows = new List<TimeWindow>(added);
        var weeklyHours = rules.ToArray();

        for (var day = month; day < month.AddMonths(1); day = day.AddDays(1))
        {
            foreach (var rule in weeklyHours.Where(r => r.DayOfWeek == (int)day.DayOfWeek
                && day >= r.EffectiveFrom && (r.EffectiveThrough is null || day <= r.EffectiveThrough)))
            {
                var start = day.Add(rule.StartTimeLocal);
                var end = day.Add(rule.EndTimeLocal);
                // Daylight Saving: Do not publish an interval with an ambiguous or nonexistent boundary.
                if (zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start)
                    || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(end))
                    continue;

                windows.Add(new(ToUtc(start, zone), ToUtc(end, zone)));
            }
        }

        // Booking Notice: Use elapsed UTC hours, including weekends and daylight-saving changes.
        var earliestStart = nowUtc.AddHours(minimumNoticeHours);
        var lowerBound = earliestStart > rangeStart ? earliestStart : rangeStart;
        var merged = new List<TimeWindow>();
        foreach (var window in windows
            .Select(w => new TimeWindow(w.StartAtUtc < lowerBound ? lowerBound : w.StartAtUtc,
                w.EndAtUtc > rangeEnd ? rangeEnd : w.EndAtUtc))
            .Where(w => w.StartAtUtc < w.EndAtUtc).OrderBy(w => w.StartAtUtc))
        {
            if (merged.Count == 0 || merged[^1].EndAtUtc < window.StartAtUtc)
                merged.Add(window);
            else if (window.EndAtUtc > merged[^1].EndAtUtc)
                merged[^1] = merged[^1] with { EndAtUtc = window.EndAtUtc };
        }

        // Availability: Blocks and existing reservations always override added or recurring hours.
        foreach (var block in blocked)
        {
            var remaining = new List<TimeWindow>();
            foreach (var window in merged)
            {
                if (block.StartAtUtc >= window.EndAtUtc || block.EndAtUtc <= window.StartAtUtc)
                {
                    remaining.Add(window);
                    continue;
                }
                if (window.StartAtUtc < block.StartAtUtc)
                    remaining.Add(new(window.StartAtUtc, block.StartAtUtc));
                if (block.EndAtUtc < window.EndAtUtc)
                    remaining.Add(new(block.EndAtUtc, window.EndAtUtc));
            }
            merged = remaining;
        }

        return merged;
    }

    public static List<TimeWindow> Slots(IEnumerable<TimeWindow> windows, TimeZoneInfo zone,
        int durationMinutes, int slotIntervalMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotIntervalMinutes);
        var result = new List<TimeWindow>();
        foreach (var window in windows)
        {
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(window.StartAtUtc, zone);
            var localEnd = TimeZoneInfo.ConvertTimeFromUtc(window.EndAtUtc, zone);
            for (var day = localStart.Date; day <= localEnd.Date; day = day.AddDays(1))
            {
                // Slot Alignment: Keep starts on the local clock grid after notice clipping or blocked time.
                for (var minute = 0; minute < 1440; minute += slotIntervalMinutes)
                {
                    var local = day.AddMinutes(minute);
                    if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) continue;
                    var start = ToUtc(local, zone);
                    var end = start.AddMinutes(durationMinutes);
                    if (start >= window.StartAtUtc && end <= window.EndAtUtc)
                        result.Add(new(start, end));
                }
            }
        }
        return result.Distinct().OrderBy(s => s.StartAtUtc).ToList();
    }

    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
}
