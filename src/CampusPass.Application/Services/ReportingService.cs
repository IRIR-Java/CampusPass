using CampusPass.Application.Abstractions;
using CampusPass.Domain.Entities;

namespace CampusPass.Application.Services;

public record SectorStats(
    Sector Sector,
    int OnDutyOfficers,
    int DailyCapacity,
    int Waiting,
    int Active,
    int ResolvedTotal,
    double? AvgWaitMinutes,
    double? AvgHandleMinutes,
    IReadOnlyList<BatchLoad> UpcomingBatches);

public record BatchLoad(DateOnly Date, int Booked, int Capacity);

/// <summary>Read-only numbers for the Admin / Queue Manager dashboard.</summary>
public class ReportingService(
    ISectorRepository sectors,
    IOfficerRepository officers,
    IBatchRepository batches,
    ITicketRepository tickets,
    BatchingService batching,
    IClock clock)
{
    public IReadOnlyList<SectorStats> Overview()
    {
        var today = DateOnly.FromDateTime(clock.Now);
        var all = tickets.All();

        return sectors.All().Select(s =>
        {
            var mine = all.Where(t => t.SectorId == s.Id).ToList();
            var called = mine.Where(t => t.CalledAt.HasValue).ToList();
            var resolved = mine.Where(t => t.ResolvedAt.HasValue && t.CalledAt.HasValue).ToList();

            return new SectorStats(
                s,
                officers.BySector(s.Id).Count(o => o.OnDuty),
                batching.DailyCapacity(s),
                mine.Count(t => t.Status == TicketStatus.Waiting),
                mine.Count(t => t.Status is TicketStatus.Called or TicketStatus.InProgress),
                mine.Count(t => t.Status == TicketStatus.Resolved),
                called.Count == 0 ? null : Math.Round(called.Average(t => (t.CalledAt!.Value - t.CreatedAt).TotalMinutes), 1),
                resolved.Count == 0 ? null : Math.Round(resolved.Average(t => (t.ResolvedAt!.Value - t.CalledAt!.Value).TotalMinutes), 1),
                batches.All().Where(b => b.SectorId == s.Id && b.Date >= today).OrderBy(b => b.Date).Take(5)
                    .Select(b => new BatchLoad(b.Date, batching.Load(b), b.Capacity)).ToList());
        }).ToList();
    }

    public IReadOnlyList<Ticket> RecentTickets(int take = 30) =>
        tickets.All().OrderByDescending(t => t.CreatedAt).Take(take).ToList();
}
