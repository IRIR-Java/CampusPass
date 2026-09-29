using System.Globalization;
using CampusPass.Domain;

namespace CampusPass.Application;

/// <summary>One batch: a time window on a service day ("Batch 3 · Tue 2:00 – 3:00 PM").</summary>
public record Window(DateTime Start, DateTime End, int Batch);

/// <summary>When offices serve students: Mon–Fri, opening hours, lunch, public holidays, split into time windows.</summary>
public class WorkingCalendar
{
    private readonly SystemSettings _s;
    private readonly HashSet<DateOnly> _holidays;

    public WorkingCalendar(SystemSettings settings)
    {
        _s = settings;
        _holidays = settings.Holidays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => DateOnly.TryParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .Where(d => d.HasValue).Select(d => d!.Value).ToHashSet();
    }

    public int WindowMinutes => Math.Clamp(_s.WindowMinutes, 15, 240);

    public bool IsServiceDay(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(d);

    /// <summary>All windows of a day, skipping any that overlap lunch.</summary>
    public IReadOnlyList<Window> Windows(DateOnly day)
    {
        var list = new List<Window>();
        if (!IsServiceDay(day)) return list;
        var t = day.ToDateTime(_s.OpenTime);
        var close = day.ToDateTime(_s.CloseTime);
        var lunchStart = day.ToDateTime(_s.LunchStart);
        var lunchEnd = day.ToDateTime(_s.LunchEnd);
        var n = 0;
        while (t.AddMinutes(WindowMinutes) <= close)
        {
            var end = t.AddMinutes(WindowMinutes);
            if (t < lunchEnd && end > lunchStart) { t = lunchEnd; continue; }   // overlaps lunch: resume after it
            list.Add(new Window(t, end, ++n));
            t = end;
        }
        return list;
    }

    /// <summary>The next <paramref name="count"/> service days starting at <paramref name="from"/> (inclusive).</summary>
    public IReadOnlyList<DateOnly> ServiceDays(DateOnly from, int count)
    {
        var days = new List<DateOnly>();
        for (var d = from; days.Count < count && d < from.AddDays(120); d = d.AddDays(1))
            if (IsServiceDay(d)) days.Add(d);
        return days;
    }

    public int BatchOf(DateTime windowStart) =>
        Windows(DateOnly.FromDateTime(windowStart)).FirstOrDefault(w => w.Start == windowStart)?.Batch ?? 1;
}
