using CampusPass.Domain;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Application;

/// <summary>A bookable time window with how full it is.</summary>
public record Slot(DateTime Start, DateTime End, int Batch, int Booked, int Capacity)
{
    public int Left => Math.Max(0, Capacity - Booked);
    public bool Full => Booked >= Capacity;
}

/// <summary>One row of the "Live Queue" list: numbers and states only, never names.</summary>
public record LiveRow(int Position, string Number, TicketStatus Status, DateTime Time, bool Mine);

/// <summary>Everything the student's ticket screen shows.</summary>
public record TicketView(
    Ticket Ticket, Department Department, int Batch, int Position, int WindowTotal,
    int Ahead, int EtaMinutes, bool IsToday, bool Almost, IReadOnlyList<LiveRow> Live, int ServedInWindow);

/// <summary>
/// Smart batching + bank-style ticketing.
/// Students either JOIN THE QUEUE (placed in the earliest time window with room) or BOOK AN APPOINTMENT (pick a window).
/// Window capacity = staff on duty × window length ÷ the department's average service time, so the students booked
/// into a window can actually be served in it. Queue tickets are never placed before the student's faculty
/// registration day. Staff call students in order: earliest window, then first come, first served.
/// </summary>
public class QueueService(IAppDb db, IClock clock, NotificationService notify)
{
    // Single instance deployment: one gate for all queue writes, so two officers can never call the same student
    // and two students can never take the last place in a window. (With several servers, use DB row locking.)
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public const int AlmostThreshold = 2;

    private async Task<WorkingCalendar> CalendarAsync() => new(await db.GetAsync());

    public async Task<int> StaffOnDutyAsync(int departmentId) =>
        await db.Users.CountAsync(u => u.Role == Role.Staff && u.DepartmentId == departmentId && u.OnDuty && !u.Disabled);

    public static int Capacity(int windowMinutes, int staffOnDuty, int avgMinutes) =>
        Math.Max(1, Math.Max(1, staffOnDuty) * windowMinutes / Math.Max(1, avgMinutes));

    // ---------------------------------------------------------------- availability

    /// <summary>Windows of a day for a department, with bookings. Past windows (and the current one if nearly over) are left out.</summary>
    public async Task<IReadOnlyList<Slot>> SlotsAsync(Department dept, DateOnly day)
    {
        var cal = await CalendarAsync();
        var now = clock.Now;
        var windows = cal.Windows(day).Where(w => w.End > now.AddMinutes(dept.AvgServiceMinutes)).ToList();
        if (windows.Count == 0) return Array.Empty<Slot>();

        var from = day.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(1);
        var booked = await db.Tickets
            .Where(t => t.DepartmentId == dept.Id && t.WindowStart >= from && t.WindowStart < to && t.Status != TicketStatus.Cancelled)
            .GroupBy(t => t.WindowStart).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var cap = Capacity(cal.WindowMinutes, await StaffOnDutyAsync(dept.Id), dept.AvgServiceMinutes);

        return windows.Select(w => new Slot(w.Start, w.End, w.Batch, booked.FirstOrDefault(b => b.Key == w.Start)?.Count ?? 0, cap)).ToList();
    }

    /// <summary>Service days a student may book, starting at their faculty's registration day if that is later than today.</summary>
    public async Task<IReadOnlyList<DateOnly>> BookableDaysAsync(string? faculty)
    {
        var s = await db.GetAsync();
        var cal = new WorkingCalendar(s);
        var today = DateOnly.FromDateTime(clock.Now);
        var floor = await FacultyFloorAsync(faculty);
        var start = floor is DateOnly f && f > today ? f : today;
        return cal.ServiceDays(start, Math.Clamp(s.BookingDaysAhead, 1, 30));
    }

    public async Task<DateOnly?> FacultyFloorAsync(string? faculty)
    {
        if (string.IsNullOrWhiteSpace(faculty)) return null;
        var row = await db.FacultyDays.FirstOrDefaultAsync(f => f.Faculty == faculty);
        return row?.Date;
    }

    /// <summary>The window "Join Queue" would give right now (shown before the student commits).</summary>
    public async Task<Slot?> NextSlotAsync(Department dept, string? faculty)
    {
        foreach (var day in await BookableDaysAsync(faculty))
            foreach (var slot in await SlotsAsync(dept, day))
                if (!slot.Full) return slot;
        return null;
    }

    // ---------------------------------------------------------------- student actions

    public async Task<Ticket?> OpenTicketAsync(int studentUserId, int departmentId) =>
        await db.Tickets.FirstOrDefaultAsync(t => t.StudentUserId == studentUserId && t.DepartmentId == departmentId &&
            (t.Status == TicketStatus.Waiting || t.Status == TicketStatus.Called || t.Status == TicketStatus.Serving));

    /// <summary>
    /// Creates a ticket. Mode Queue = earliest window with room; Mode Appointment = the chosen window.
    /// A student who already has an open ticket at this department gets that one back (keeps their place).
    /// </summary>
    public async Task<(Ticket Ticket, bool Existing)> RequestAsync(UserAccount student, int departmentId, TicketMode mode, DateTime? windowStart, string? description)
    {
        await Gate.WaitAsync();
        try
        {
            var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId && d.IsActive)
                       ?? throw new RuleException("That service is not available.");
            if (await OpenTicketAsync(student.Id, dept.Id) is Ticket open) return (open, true);

            var record = student.RegNo is null ? null : await db.Students.FirstOrDefaultAsync(s => s.RegNo == student.RegNo);
            Slot? slot;
            if (mode == TicketMode.Appointment)
            {
                if (windowStart is null) throw new RuleException("Choose a day and time.");
                var day = DateOnly.FromDateTime(windowStart.Value);
                if (!(await BookableDaysAsync(record?.Faculty)).Contains(day)) throw new RuleException("That day can't be booked.");
                slot = (await SlotsAsync(dept, day)).FirstOrDefault(s => s.Start == windowStart.Value)
                       ?? throw new RuleException("That time is no longer available.");
                if (slot.Full) throw new RuleException("That time just filled up. Pick another one.");
            }
            else
            {
                slot = await NextSlotAsync(dept, record?.Faculty) ?? throw new RuleException("This service is fully booked for the coming days. Try again tomorrow.");
            }

            var now = clock.Now;
            // Highest number issued with this prefix + 1 (counting would reuse numbers after transfers out).
            var prefix = dept.Code + "-";
            var issued = await db.Tickets.Where(t => t.Number.StartsWith(prefix)).Select(t => t.Number).ToListAsync();
            var seq = issued.Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
            var ticket = new Ticket
            {
                Number = $"{dept.Code}-{seq:000}",
                DepartmentId = dept.Id,
                StudentUserId = student.Id,
                RegNo = student.RegNo ?? student.Username,
                StudentName = student.DisplayName,
                Mode = mode,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(description.Trim().Length, 500)],
                Status = TicketStatus.Waiting,
                WindowStart = slot.Start,
                WindowEnd = slot.End,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            notify.Add(student.Id, $"Ticket {ticket.Number} created",
                $"{dept.Name} · Batch {slot.Batch}, {Describe(slot.Start, slot.End, now)}.", $"/Student/Ticket/{ticket.Id}");
            await db.SaveChangesAsync();
            return (ticket, false);
        }
        finally { Gate.Release(); }
    }

    public async Task CancelAsync(int ticketId, int studentUserId)
    {
        await Gate.WaitAsync();
        try
        {
            var t = await OwnTicketAsync(ticketId, studentUserId);
            if (t.Status is not (TicketStatus.Waiting or TicketStatus.Called)) throw new RuleException("This ticket can no longer be cancelled.");
            t.Status = TicketStatus.Cancelled;
            t.ClosedAt = t.UpdatedAt = clock.Now;
            await db.SaveChangesAsync();
            await NotifyAlmostAsync(t.DepartmentId);
        }
        finally { Gate.Release(); }
    }

    public async Task OnMyWayAsync(int ticketId, int studentUserId)
    {
        var t = await OwnTicketAsync(ticketId, studentUserId);
        if (t.Status is not (TicketStatus.Waiting or TicketStatus.Called)) throw new RuleException("This ticket is not waiting any more.");
        t.OnTheWay = true;
        t.UpdatedAt = clock.Now;
        await db.SaveChangesAsync();
    }

    private async Task<Ticket> OwnTicketAsync(int ticketId, int studentUserId) =>
        await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.StudentUserId == studentUserId)
        ?? throw new RuleException("Ticket not found.");

    // ---------------------------------------------------------------- what the student sees

    public async Task<TicketView?> ViewAsync(int ticketId, int? studentUserId = null)
    {
        var t = await db.Tickets.Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t is null || (studentUserId is int uid && t.StudentUserId != uid)) return null;

        var cal = await CalendarAsync();
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var isToday = DateOnly.FromDateTime(t.WindowStart) == today;

        var window = await db.Tickets
            .Where(x => x.DepartmentId == t.DepartmentId && x.WindowStart == t.WindowStart && x.Status != TicketStatus.Cancelled)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
        var position = Math.Max(1, window.FindIndex(x => x.Id == t.Id) + 1);

        var ahead = 0;
        if (t.Status == TicketStatus.Waiting)
        {
            var dayStart = DateOnly.FromDateTime(t.WindowStart).ToDateTime(TimeOnly.MinValue);
            var waiting = await db.Tickets
                .Where(x => x.DepartmentId == t.DepartmentId && x.Status == TicketStatus.Waiting && x.WindowStart >= dayStart && x.WindowStart <= t.WindowStart)
                .Select(x => new { x.Id, x.WindowStart, x.CreatedAt }).ToListAsync();
            ahead = waiting.Count(x => x.WindowStart < t.WindowStart || (x.WindowStart == t.WindowStart && (x.CreatedAt < t.CreatedAt || (x.CreatedAt == t.CreatedAt && x.Id < t.Id))));
        }

        var eta = 0;
        if (t.Status == TicketStatus.Waiting)
        {
            var staff = Math.Max(1, await StaffOnDutyAsync(t.DepartmentId));
            var byQueue = (int)Math.Ceiling(ahead * (double)t.Department!.AvgServiceMinutes / staff);
            var untilWindow = (int)Math.Ceiling(Math.Max(0, (t.WindowStart - now).TotalMinutes));
            eta = Math.Max(byQueue, untilWindow);
        }

        var live = window.Select((x, i) => new LiveRow(i + 1, x.Number, x.Status, x.ClosedAt ?? x.ServingAt ?? x.CalledAt ?? x.CreatedAt, x.Id == t.Id)).ToList();
        var almost = t.Status == TicketStatus.Waiting && isToday && ahead <= AlmostThreshold && t.WindowStart <= now.AddMinutes(cal.WindowMinutes);

        return new TicketView(t, t.Department!, cal.BatchOf(t.WindowStart), position, window.Count, ahead, eta, isToday, almost, live,
            window.Count(x => x.Status is TicketStatus.Completed or TicketStatus.NoShow));
    }

    public async Task<IReadOnlyList<Ticket>> StudentTicketsAsync(int studentUserId) =>
        await db.Tickets.Include(t => t.Department).Where(t => t.StudentUserId == studentUserId)
            .OrderByDescending(t => t.Id).Take(100).ToListAsync();

    // ---------------------------------------------------------------- staff actions

    /// <summary>Today's queue for a department: in service first, then waiting in call order.</summary>
    public async Task<IReadOnlyList<Ticket>> TodayQueueAsync(int departmentId)
    {
        var (from, to) = Today();
        var list = await db.Tickets
            .Where(t => t.DepartmentId == departmentId && t.WindowStart >= from && t.WindowStart < to &&
                        (t.Status == TicketStatus.Waiting || t.Status == TicketStatus.Called || t.Status == TicketStatus.Serving))
            .ToListAsync();
        return list.OrderBy(t => t.Status == TicketStatus.Waiting ? 1 : 0).ThenBy(t => t.WindowStart).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
    }

    public async Task<Ticket?> CurrentForStaffAsync(int staffUserId) =>
        await db.Tickets.Include(t => t.Department)
            .FirstOrDefaultAsync(t => t.StaffUserId == staffUserId && (t.Status == TicketStatus.Called || t.Status == TicketStatus.Serving));

    /// <summary>"Call next": the first waiting student of today in fair order. Returns null when nobody is waiting.</summary>
    public Task<Ticket?> CallNextAsync(UserAccount staff) => CallAsync(staff, null);

    /// <summary>Calls a specific waiting ticket (the "Call" button on a queue row), or the next one when <paramref name="ticketId"/> is null.</summary>
    public async Task<Ticket?> CallAsync(UserAccount staff, int? ticketId)
    {
        if (staff.DepartmentId is not int deptId) throw new RuleException("Your account is not linked to a department.");
        await Gate.WaitAsync();
        try
        {
            if (await CurrentForStaffAsync(staff.Id) is Ticket busy)
                throw new RuleException($"Finish {busy.Number} before calling the next student.");

            var (from, to) = Today();
            var waiting = await db.Tickets.Include(t => t.Department)
                .Where(t => t.DepartmentId == deptId && t.Status == TicketStatus.Waiting && t.WindowStart >= from && t.WindowStart < to)
                .ToListAsync();
            var next = ticketId is int id
                ? waiting.FirstOrDefault(t => t.Id == id) ?? throw new RuleException("That ticket is no longer waiting.")
                : waiting.OrderBy(t => t.WindowStart).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).FirstOrDefault();
            if (next is null) return null;

            var now = clock.Now;
            next.Status = TicketStatus.Called;
            next.CalledAt = next.UpdatedAt = now;
            next.StaffUserId = staff.Id;
            next.StaffName = staff.DisplayName;
            next.Counter = staff.Counter;
            await db.SaveChangesAsync();

            NotifyCalled(next);
            await db.SaveChangesAsync();
            await NotifyAlmostAsync(deptId);
            return next;
        }
        finally { Gate.Release(); }
    }

    public async Task RecallAsync(int ticketId, UserAccount staff)
    {
        var t = await StaffTicketAsync(ticketId, staff);
        if (t.Status != TicketStatus.Called) throw new RuleException("Only a called student can be called again.");
        NotifyCalled(t);
        await db.SaveChangesAsync();
    }

    public async Task StartAsync(int ticketId, UserAccount staff)
    {
        var t = await StaffTicketAsync(ticketId, staff);
        if (t.Status != TicketStatus.Called) throw new RuleException("Call the student first.");
        t.Status = TicketStatus.Serving;
        t.ServingAt = t.UpdatedAt = clock.Now;
        await db.SaveChangesAsync();
    }

    public async Task CompleteAsync(int ticketId, UserAccount staff, string? note)
    {
        var t = await StaffTicketAsync(ticketId, staff);
        if (t.Status is not (TicketStatus.Called or TicketStatus.Serving)) throw new RuleException("This ticket is not being served.");
        var now = clock.Now;
        t.ServingAt ??= now;
        t.Status = TicketStatus.Completed;
        t.ClosedAt = t.UpdatedAt = now;
        t.Resolution = string.IsNullOrWhiteSpace(note) ? $"Served by {t.Department!.Name}." : note.Trim()[..Math.Min(note.Trim().Length, 1000)];
        notify.Add(t.StudentUserId, $"{t.Number} completed", t.Resolution, $"/Student/Ticket/{t.Id}", "done");
        await db.SaveChangesAsync();
    }

    public async Task NoShowAsync(int ticketId, UserAccount staff)
    {
        var t = await StaffTicketAsync(ticketId, staff);
        if (t.Status != TicketStatus.Called) throw new RuleException("Only a called student can be marked as not here.");
        t.Status = TicketStatus.NoShow;
        t.ClosedAt = t.UpdatedAt = clock.Now;
        notify.Add(t.StudentUserId, $"{t.Number} missed", "You were called but weren't there. You can request the service again.", $"/Student/Ticket/{t.Id}");
        await db.SaveChangesAsync();
    }

    /// <summary>Wrong desk: move the ticket to another department's earliest window. Same number, same student, no starting over.</summary>
    public async Task TransferAsync(int ticketId, UserAccount staff, int toDepartmentId)
    {
        await Gate.WaitAsync();
        try
        {
            var t = await StaffTicketAsync(ticketId, staff);
            var to = await db.Departments.FirstOrDefaultAsync(d => d.Id == toDepartmentId && d.IsActive) ?? throw new RuleException("Choose a department.");
            if (to.Id == t.DepartmentId) throw new RuleException("It's already at that department.");
            var slot = await NextSlotAsync(to, null) ?? throw new RuleException($"{to.Name} is fully booked.");

            t.TransferredFrom = t.Department!.Name;
            t.DepartmentId = to.Id;
            t.Status = TicketStatus.Waiting;
            t.WindowStart = slot.Start;
            t.WindowEnd = slot.End;
            t.StaffUserId = null; t.StaffName = null; t.Counter = null;
            t.CalledAt = null; t.ServingAt = null; t.OnTheWay = false; t.AlmostNotified = false;
            t.UpdatedAt = clock.Now;
            notify.Add(t.StudentUserId, $"{t.Number} moved to {to.Name}", $"{t.TransferredFrom} sent you to the right office. Batch {slot.Batch}, {Describe(slot.Start, slot.End, clock.Now)}.", $"/Student/Ticket/{t.Id}");
            await db.SaveChangesAsync();
        }
        finally { Gate.Release(); }
    }

    private async Task<Ticket> StaffTicketAsync(int ticketId, UserAccount staff)
    {
        var t = await db.Tickets.Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == ticketId) ?? throw new RuleException("Ticket not found.");
        if (t.StaffUserId != staff.Id) throw new RuleException("This ticket is being handled by someone else.");
        return t;
    }

    private void NotifyCalled(Ticket t) =>
        notify.Add(t.StudentUserId, "It's your turn!",
            $"Go to {t.Department!.Name}, {t.Department.Location}{(t.Counter is null ? "" : $" · {t.Counter}")}. Show ticket {t.Number}.",
            $"/Student/Ticket/{t.Id}", "called");

    /// <summary>Tells the next students in line (once each) that their turn is near.</summary>
    private async Task NotifyAlmostAsync(int departmentId)
    {
        var (from, to) = Today();
        var next = (await db.Tickets.Include(t => t.Department)
                .Where(t => t.DepartmentId == departmentId && t.Status == TicketStatus.Waiting && t.WindowStart >= from && t.WindowStart < to)
                .ToListAsync())
            .OrderBy(t => t.WindowStart).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(AlmostThreshold + 1);
        foreach (var t in next.Where(t => !t.AlmostNotified))
        {
            t.AlmostNotified = true;
            notify.Add(t.StudentUserId, "You're almost up!", $"Head to {t.Department!.Name}, {t.Department.Location}. Ticket {t.Number}.", $"/Student/Ticket/{t.Id}", "almost");
        }
        await db.SaveChangesAsync();
    }

    private (DateTime From, DateTime To) Today()
    {
        var from = DateOnly.FromDateTime(clock.Now).ToDateTime(TimeOnly.MinValue);
        return (from, from.AddDays(1));
    }

    /// <summary>"Today, 2:00 – 3:00 PM" or "Tue 6 Oct, 9:00 – 10:00 AM".</summary>
    public static string Describe(DateTime start, DateTime end, DateTime now)
    {
        var day = start.Date == now.Date ? "Today" : start.Date == now.Date.AddDays(1) ? "Tomorrow" : start.ToString("ddd d MMM");
        return $"{day}, {start:h:mm} – {end:h:mm tt}";
    }
}
