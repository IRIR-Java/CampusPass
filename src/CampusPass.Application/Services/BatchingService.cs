using CampusPass.Application.Abstractions;
using CampusPass.Domain.Entities;

namespace CampusPass.Application.Services;

/// <summary>
/// Smart batching: decides WHICH service day a ticket is placed in.
///  - Daily capacity = on-duty officers x the sector's per-officer throughput.
///  - Normal tickets may only fill 80% of a day (20% reserved for urgent cases).
///  - Urgent tickets may use 100% of the day.
///  - After the cutoff hour, or on weekends, "today" is no longer available.
/// </summary>
public class BatchingService(
    IOfficerRepository officers,
    IBatchRepository batches,
    ITicketRepository tickets,
    IClock clock)
{
    public const int CutoffHour = 16;          // no new same-day slots after 4pm
    public const double UrgentReserve = 0.20;  // share of each day held back for urgent tickets
    public const int MaxDaysAhead = 30;

    public int DailyCapacity(Sector sector)
    {
        var onDuty = officers.BySector(sector.Id).Count(o => o.OnDuty);
        return Math.Max(1, onDuty * sector.TicketsPerOfficerPerDay);
    }

    /// <summary>Finds (or creates) the earliest batch with room for this ticket.</summary>
    public Batch Assign(Sector sector, Urgency urgency)
    {
        var capacity = DailyCapacity(sector);
        var normalLimit = Math.Max(1, (int)Math.Floor(capacity * (1 - UrgentReserve)));
        var limit = urgency == Urgency.Urgent ? capacity : normalLimit;

        var day = FirstServiceDay();
        for (var i = 0; i < MaxDaysAhead; i++, day = NextServiceDay(day))
        {
            var batch = batches.Find(sector.Id, day) ?? batches.Add(new Batch { SectorId = sector.Id, Date = day });
            batch.Capacity = capacity; // keep in sync if staffing changed

            if (Load(batch) < limit) return batch;
        }

        // Extremely overloaded sector: still return the last day rather than fail the student.
        return batches.Find(sector.Id, day) ?? batches.Add(new Batch { SectorId = sector.Id, Date = day, Capacity = capacity });
    }

    public int Load(Batch batch) => tickets.All().Count(t => t.BatchId == batch.Id);

    private DateOnly FirstServiceDay()
    {
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var day = IsWeekend(today) || now.Hour >= CutoffHour ? NextServiceDay(today) : today;
        return day;
    }

    public static DateOnly NextServiceDay(DateOnly from)
    {
        var d = from.AddDays(1);
        while (IsWeekend(d)) d = d.AddDays(1);
        return d;
    }

    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
