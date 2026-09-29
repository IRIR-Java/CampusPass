using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Web.Controllers;

public class StaffDashPage
{
    public UserAccount Me { get; init; } = null!;
    public StaffDashboard Stats { get; init; } = null!;
    public IReadOnlyList<Ticket> Queue { get; init; } = Array.Empty<Ticket>();
    public Ticket? Current { get; init; }
    public WorkingCalendar Calendar { get; init; } = null!;
}

public class ServePage
{
    public UserAccount Me { get; init; } = null!;
    public Ticket? Current { get; init; }
    public Clearance? Fee { get; init; }
    public IReadOnlyList<Ticket> NextUp { get; init; } = Array.Empty<Ticket>();
    public IReadOnlyList<Department> Others { get; init; } = Array.Empty<Department>();
    public int WaitingCount { get; init; }
    public WorkingCalendar Calendar { get; init; } = null!;
}

public class StudentLookupPage
{
    public string? Q { get; init; }
    public IReadOnlyList<StudentRecord> Matches { get; init; } = Array.Empty<StudentRecord>();
    public Clearance? Selected { get; init; }
    public IReadOnlyList<Ticket> Tickets { get; init; } = Array.Empty<Ticket>();
    public bool HasLogin { get; init; }
}

/// <summary>The service officer's workspace. The officer is whoever is signed in: their id never comes from a form field.</summary>
[Authorize(Roles = nameof(Role.Staff))]
public class StaffController(IAppDb db, QueueService queue, ClearanceService clearance, ReportService reports, IClock clock) : Controller
{
    private async Task<UserAccount> Me() => await this.CurrentAsync(db) ?? throw new RuleException("Please sign in again.");
    private async Task<WorkingCalendar> Cal() => new(await db.GetAsync());

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var me = await Me();
        return View(new StaffDashPage
        {
            Me = me,
            Stats = await reports.StaffAsync(me),
            Queue = me.DepartmentId is int d ? await queue.TodayQueueAsync(d) : Array.Empty<Ticket>(),
            Current = await queue.CurrentForStaffAsync(me.Id),
            Calendar = await Cal()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Queue()
    {
        var me = await Me();
        ViewData["Calendar"] = await Cal();
        ViewData["Me"] = me;
        return View(me.DepartmentId is int d ? await queue.TodayQueueAsync(d) : Array.Empty<Ticket>());
    }

    /// <summary>The "Call Next" screen: the student being served, their fee position, and every action on one page.</summary>
    [HttpGet]
    public async Task<IActionResult> Serve()
    {
        var me = await Me();
        var current = await queue.CurrentForStaffAsync(me.Id);
        var today = me.DepartmentId is int d ? await queue.TodayQueueAsync(d) : Array.Empty<Ticket>();
        return View(new ServePage
        {
            Me = me,
            Current = current,
            Fee = current is null ? null : await clearance.GetAsync(current.RegNo),
            NextUp = today.Where(t => t.Status == TicketStatus.Waiting).Take(5).ToList(),
            WaitingCount = today.Count(t => t.Status == TicketStatus.Waiting),
            Others = await db.Departments.Where(x => x.IsActive && x.Id != me.DepartmentId).OrderBy(x => x.SortOrder).ToListAsync(),
            Calendar = await Cal()
        });
    }

    [HttpPost]
    public async Task<IActionResult> CallNext()
    {
        if (await queue.CallNextAsync(await Me()) is null) TempData["Info"] = "Nobody is waiting right now.";
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> Call(int id)
    {
        await queue.CallAsync(await Me(), id);
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> Recall(int id)
    {
        await queue.RecallAsync(id, await Me());
        TempData["Success"] = "The student was notified again.";
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> Start(int id)
    {
        await queue.StartAsync(id, await Me());
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> Complete(int id, string? note, bool next = false)
    {
        var me = await Me();
        await queue.CompleteAsync(id, me, note);
        TempData["Success"] = "Service completed.";
        if (next && await queue.CallNextAsync(me) is null) TempData["Info"] = "That was the last student waiting.";
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> NoShow(int id)
    {
        await queue.NoShowAsync(id, await Me());
        TempData["Info"] = "Marked as not here.";
        return RedirectToAction(nameof(Serve));
    }

    [HttpPost]
    public async Task<IActionResult> Transfer(int id, int to)
    {
        await queue.TransferAsync(id, await Me(), to);
        TempData["Success"] = "Sent to the right office. The student keeps the same ticket.";
        return RedirectToAction(nameof(Serve));
    }

    /// <summary>All tickets of the department, searchable and filterable.</summary>
    [HttpGet]
    public async Task<IActionResult> Tickets(string? q, string status = "all", int days = 7)
    {
        var me = await Me();
        var from = clock.Now.Date.AddDays(-Math.Clamp(days, 1, 90) + 1);
        var query = db.Tickets.Where(t => t.DepartmentId == me.DepartmentId && (t.CreatedAt >= from || t.WindowStart >= from));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim().ToUpperInvariant();
            query = query.Where(t => t.Number.Contains(k) || t.RegNo.Contains(k) || t.StudentName.ToUpper().Contains(k));
        }
        if (Enum.TryParse<TicketStatus>(status, true, out var st)) query = query.Where(t => t.Status == st);
        ViewData["Q"] = q; ViewData["Status"] = status; ViewData["Days"] = days;
        return View(await query.OrderByDescending(t => t.WindowStart).ThenByDescending(t => t.Id).Take(300).ToListAsync());
    }

    /// <summary>Look a student up: fee position, gatepass, and their tickets. Nobody has to ask "what is your balance?".</summary>
    [HttpGet]
    public async Task<IActionResult> Students(string? q, string? reg)
    {
        var matches = new List<StudentRecord>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim().ToUpperInvariant();
            matches = await db.Students.Where(s => s.RegNo.Contains(k) || s.Name.ToUpper().Contains(k)).OrderBy(s => s.RegNo).Take(25).ToListAsync();
            if (matches.Count == 1) reg = matches[0].RegNo;
        }
        var sel = reg is null ? null : await clearance.GetAsync(reg);
        var key = AccountService.Normalize(reg);
        return View(new StudentLookupPage
        {
            Q = q, Matches = matches, Selected = sel,
            Tickets = sel is null ? Array.Empty<Ticket>() : await db.Tickets.Include(t => t.Department).Where(t => t.RegNo == key).OrderByDescending(t => t.Id).Take(20).ToListAsync(),
            HasLogin = sel is not null && await db.Users.AnyAsync(u => u.Username == key)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Reports(int days = 7)
    {
        var me = await Me();
        ViewData["Days"] = days;
        ViewData["Me"] = me;
        return View(await reports.RangeAsync(days, me.DepartmentId));
    }

    [HttpGet]
    public async Task<IActionResult> Settings() => View(await Me());

    [HttpPost]
    public async Task<IActionResult> Settings(bool onDuty, string? counter)
    {
        var me = await Me();
        me.OnDuty = onDuty;
        me.Counter = string.IsNullOrWhiteSpace(counter) ? null : counter.Trim()[..Math.Min(counter.Trim().Length, 30)];
        await db.SaveChangesAsync();
        TempData["Success"] = onDuty ? "You're on duty. Batch sizes include you." : "You're off duty. New bookings won't count on you.";
        return RedirectToAction(nameof(Settings));
    }
}
