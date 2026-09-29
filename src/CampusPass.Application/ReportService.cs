using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

public record DeptCount(Department Department, int Count);

public record AdminDashboard(
    int Students, int Cleared, int PassesIssued, int Departments, int Staff, int StaffOnDuty,
    IReadOnlyList<DeptCount> RequestsToday, IReadOnlyList<Ticket> Recent,
    int OnTrackPercent, double? AvgWaitMinutes, int WaitingNow, int ServedToday);

public record StaffDashboard(int Waiting, int InService, int ServedToday, int ServedByMe, int OnTrackPercent, double? AvgWaitMinutes, int LaterDays);

public record DeptReport(Department Department, int Requests, int Completed, int NoShow, int Cancelled, double? AvgWait, double? AvgService);

/// <summary>Numbers for dashboards and reports. Aggregation is done in memory: SQLite can't sum decimals or diff dates.</summary>
public class ReportService(IAppDb db, IClock clock)
{
    private (DateTime From, DateTime To) Day()
    {
        var from = clock.Now.Date;
        return (from, from.AddDays(1));
    }

    /// <summary>% of today's served students who were served within their booked window: "on track".</summary>
    private static int OnTrack(IEnumerable<Ticket> today)
    {
        var served = today.Where(t => t.Status == TicketStatus.Completed && t.ServingAt.HasValue).ToList();
        return served.Count == 0 ? 100 : (int)Math.Round(100.0 * served.Count(t => t.ServingAt <= t.WindowEnd) / served.Count);
    }

    /// <summary>Wait = from the later of booking and window start, until called.</summary>
    private static double? AvgWait(IEnumerable<Ticket> list)
    {
        var called = list.Where(t => t.CalledAt.HasValue).Select(t => (t.CalledAt!.Value - (t.WindowStart > t.CreatedAt ? t.WindowStart : t.CreatedAt)).TotalMinutes)
            .Select(m => Math.Max(0, m)).ToList();
        return called.Count == 0 ? null : Math.Round(called.Average(), 0);
    }

    public async Task<AdminDashboard> AdminAsync()
    {
        var (from, to) = Day();
        var now = clock.Now;
        var depts = await db.Departments.Where(d => d.IsActive).OrderBy(d => d.SortOrder).ToListAsync();
        var today = await db.Tickets.Where(t => (t.CreatedAt >= from && t.CreatedAt < to) || (t.WindowStart >= from && t.WindowStart < to)).ToListAsync();
        var students = await db.Students.Select(s => new { s.TotalFee, s.Paid }).ToListAsync();
        var recent = await db.Tickets.Include(t => t.Department).OrderByDescending(t => t.UpdatedAt).Take(6).ToListAsync();
        var staff = await db.Users.Where(u => u.Role == Role.Staff && !u.Disabled).Select(u => u.OnDuty).ToListAsync();

        return new AdminDashboard(
            students.Count,
            students.Count(s => s.TotalFee - s.Paid <= 0),
            await db.Gatepasses.CountAsync(p => !p.Revoked && p.ExpiresAt > now),
            depts.Count, staff.Count, staff.Count(x => x),
            depts.Select(d => new DeptCount(d, today.Count(t => t.DepartmentId == d.Id && t.CreatedAt >= from))).ToList(),
            recent,
            OnTrack(today), AvgWait(today),
            today.Count(t => t.Status == TicketStatus.Waiting && t.WindowStart < to),
            today.Count(t => t.Status == TicketStatus.Completed && t.ClosedAt >= from));
    }

    public async Task<StaffDashboard> StaffAsync(UserAccount staff)
    {
        var (from, to) = Day();
        var mine = await db.Tickets.Where(t => t.DepartmentId == staff.DepartmentId && t.WindowStart >= from && t.WindowStart < to).ToListAsync();
        var later = await db.Tickets.CountAsync(t => t.DepartmentId == staff.DepartmentId && t.Status == TicketStatus.Waiting && t.WindowStart >= to);
        return new StaffDashboard(
            mine.Count(t => t.Status == TicketStatus.Waiting),
            mine.Count(t => t.Status is TicketStatus.Called or TicketStatus.Serving),
            mine.Count(t => t.Status == TicketStatus.Completed),
            mine.Count(t => t.Status == TicketStatus.Completed && t.StaffUserId == staff.Id),
            OnTrack(mine), AvgWait(mine), later);
    }

    public async Task<IReadOnlyList<DeptReport>> RangeAsync(int days, int? departmentId = null)
    {
        var to = clock.Now.Date.AddDays(1);
        var from = to.AddDays(-Math.Clamp(days, 1, 365));
        var depts = await db.Departments.Where(d => departmentId == null || d.Id == departmentId).OrderBy(d => d.SortOrder).ToListAsync();
        var list = await db.Tickets.Where(t => t.CreatedAt >= from && t.CreatedAt < to && (departmentId == null || t.DepartmentId == departmentId)).ToListAsync();
        return depts.Select(d =>
        {
            var x = list.Where(t => t.DepartmentId == d.Id).ToList();
            var service = x.Where(t => t.ServingAt.HasValue && t.ClosedAt.HasValue && t.Status == TicketStatus.Completed)
                .Select(t => (t.ClosedAt!.Value - t.ServingAt!.Value).TotalMinutes).ToList();
            return new DeptReport(d, x.Count, x.Count(t => t.Status == TicketStatus.Completed), x.Count(t => t.Status == TicketStatus.NoShow),
                x.Count(t => t.Status == TicketStatus.Cancelled), AvgWait(x), service.Count == 0 ? null : Math.Round(service.Average(), 1));
        }).ToList();
    }
}
