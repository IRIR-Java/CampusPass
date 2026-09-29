using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Web.Controllers;

public class StudentHome
{
    public UserAccount User { get; init; } = null!;
    public Clearance? Clearance { get; init; }
    public IReadOnlyList<Department> Services { get; init; } = Array.Empty<Department>();
    public Ticket? Active { get; init; }
    public SystemSettings Settings { get; init; } = null!;
}

public class ModePage
{
    public Department Department { get; init; } = null!;
    public Slot? NextSlot { get; init; }
    public IReadOnlyList<(DateOnly Day, IReadOnlyList<Slot> Slots)> Days { get; init; } = Array.Empty<(DateOnly, IReadOnlyList<Slot>)>();
    public Ticket? Existing { get; init; }
    public DateOnly? FacultyDay { get; init; }
}

public class QueuePage
{
    public string Tab { get; init; } = "active";
    public IReadOnlyList<Ticket> Tickets { get; init; } = Array.Empty<Ticket>();
    public TicketView? Live { get; init; }
    public IClock Clock { get; init; } = null!;
}

public class ProfilePage
{
    public UserAccount User { get; init; } = null!;
    public StudentRecord? Record { get; init; }
    public Ticket? Active { get; init; }
    public SystemSettings Settings { get; init; } = null!;
}

public class GatepassPage
{
    public Clearance? Clearance { get; init; }
    public string? QrSvg { get; init; }
    public SystemSettings Settings { get; init; } = null!;
}

/// <summary>The student app: home, request service (select → mode → ticket), ticket tracking, my queue, profile, gatepass.</summary>
[Authorize(Roles = nameof(Role.Student))]
public class StudentController(IAppDb db, QueueService queue, ClearanceService clearance, IClock clock) : Controller
{
    private async Task<UserAccount> Me() => await this.CurrentAsync(db) ?? throw new RuleException("Please sign in again.");

    private async Task<IReadOnlyList<Department>> Services() =>
        await db.Departments.Where(d => d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync();

    private async Task<Ticket?> ActiveTicket(int userId) =>
        await db.Tickets.Include(t => t.Department).Where(t => t.StudentUserId == userId &&
            (t.Status == TicketStatus.Waiting || t.Status == TicketStatus.Called || t.Status == TicketStatus.Serving))
            .OrderBy(t => t.WindowStart).FirstOrDefaultAsync();

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var me = await Me();
        await clearance.EnsurePassAsync(me.RegNo);   // cleared students simply have a gatepass
        return View(new StudentHome
        {
            User = me,
            Clearance = await clearance.GetAsync(me.RegNo),
            Services = await Services(),
            Active = await ActiveTicket(me.Id),
            Settings = await db.GetAsync()
        });
    }

    /// <summary>Step 1: select a service.</summary>
    [HttpGet]
    [ActionName("Request")]
    public async Task<IActionResult> SelectService() => View("Request", await Services());

    /// <summary>Step 2: choose how to be served (join the queue now, or book a time).</summary>
    [HttpGet]
    public async Task<IActionResult> Mode(int id)
    {
        var me = await Me();
        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.IsActive);
        if (dept is null) return RedirectToAction("Request");
        var existing = await queue.OpenTicketAsync(me.Id, dept.Id);
        if (existing is not null) return RedirectToAction(nameof(Ticket), new { id = existing.Id, existing = true });

        var record = me.RegNo is null ? null : await db.Students.FirstOrDefaultAsync(s => s.RegNo == me.RegNo);
        var days = new List<(DateOnly, IReadOnlyList<Slot>)>();
        foreach (var d in await queue.BookableDaysAsync(record?.Faculty))
        {
            var slots = await queue.SlotsAsync(dept, d);
            if (slots.Count > 0) days.Add((d, slots));
            if (days.Count == 5) break;
        }
        return View(new ModePage
        {
            Department = dept,
            NextSlot = await queue.NextSlotAsync(dept, record?.Faculty),
            Days = days,
            FacultyDay = await queue.FacultyFloorAsync(record?.Faculty)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Mode(int id, TicketMode mode, string? slot, string? description)
    {
        var me = await Me();
        DateTime? start = DateTime.TryParseExact(slot, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var s) ? s : null;
        var (ticket, existing) = await queue.RequestAsync(me, id, mode, start, description);
        return RedirectToAction(nameof(Ticket), new { id = ticket.Id, created = !existing, existing });
    }

    /// <summary>Ticket details / almost your turn / it's your turn: the same page adapts to the moment.</summary>
    [HttpGet]
    public async Task<IActionResult> Ticket(int id, bool created = false, bool existing = false)
    {
        var view = await queue.ViewAsync(id, (await Me()).Id);
        if (view is null) return RedirectToAction(nameof(Queue));
        ViewData["Created"] = created;
        ViewData["Existing"] = existing;
        ViewData["Now"] = clock.Now;
        return View(view);
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id)
    {
        await queue.CancelAsync(id, (await Me()).Id);
        TempData["Success"] = "Ticket cancelled. Your place was given to the next student.";
        return RedirectToAction(nameof(Queue), new { tab = "history" });
    }

    [HttpPost]
    public async Task<IActionResult> OnMyWay(int id)
    {
        await queue.OnMyWayAsync(id, (await Me()).Id);
        TempData["Success"] = "Great, the office knows you're coming.";
        return RedirectToAction(nameof(Ticket), new { id });
    }

    /// <summary>My Queue: Active / History / Completed, plus the live queue of the current batch.</summary>
    [HttpGet]
    public async Task<IActionResult> Queue(string tab = "active")
    {
        var me = await Me();
        var all = await queue.StudentTicketsAsync(me.Id);
        tab = tab is "history" or "completed" ? tab : "active";
        var list = tab switch
        {
            "completed" => all.Where(t => t.Status == TicketStatus.Completed).ToList(),
            "history" => all.Where(t => t.Status is TicketStatus.Cancelled or TicketStatus.NoShow).ToList(),
            _ => all.Where(t => t.IsOpen).OrderBy(t => t.WindowStart).ToList()
        };
        var first = tab == "active" ? list.FirstOrDefault() : null;
        return View(new QueuePage { Tab = tab, Tickets = list, Live = first is null ? null : await queue.ViewAsync(first.Id, me.Id), Clock = clock });
    }

    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var me = await Me();
        return View(new ProfilePage
        {
            User = me,
            Record = me.RegNo is null ? null : await db.Students.FirstOrDefaultAsync(s => s.RegNo == me.RegNo),
            Active = await ActiveTicket(me.Id),
            Settings = await db.GetAsync()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Gatepass()
    {
        var me = await Me();
        var pass = await clearance.EnsurePassAsync(me.RegNo);
        string? svg = null;
        if (pass is not null)
        {
            var url = $"{Request.Scheme}://{Request.Host}/Verify?t={Uri.EscapeDataString(await clearance.TokenAsync(pass))}";
            svg = Qr.Svg(url);
        }
        return View(new GatepassPage { Clearance = await clearance.GetAsync(me.RegNo), QrSvg = svg, Settings = await db.GetAsync() });
    }

    [HttpGet]
    public async Task<IActionResult> Help() => View(await db.GetAsync());
}
