using CampusPass.Application.Abstractions;
using CampusPass.Application.Services;
using CampusPass.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace CampusPass.Web.Controllers;

// NOTE: the prototype has no login. An officer is "picked" from a list.
// Real version: ASP.NET Core Identity + [Authorize(Roles = "Officer")], officerId taken from the signed-in user.
public class OfficerController(QueueService queue, IOfficerRepository officers, ISectorRepository sectors) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new OfficerPickerModel(officers.All(), sectors.All().ToDictionary(s => s.Id)));

    [HttpGet]
    public IActionResult Desk(int id)
    {
        var officer = officers.Get(id);
        if (officer is null) return NotFound();

        var q = queue.GetSectorQueue(officer.SectorId);
        return View(new DeskPageModel
        {
            Officer = officer,
            Queue = q,
            Current = q.Active.FirstOrDefault(t => t.OfficerId == id)
        });
    }

    /// <summary>The "Call Next" button.</summary>
    [HttpPost]
    public IActionResult CallNext(int id) => Try(
        () =>
        {
            var t = queue.CallNext(id);
            if (t is null)
            {
                TempData["Info"] = "Nobody is waiting for today's batch. Enjoy the quiet ☕";
                return RedirectToAction(nameof(Desk), new { id });
            }
            return RedirectToAction(nameof(Ticket), new { id = t.Id, officerId = id });
        },
        () => RedirectToAction(nameof(Desk), new { id }));

    /// <summary>Ticket details page: the student's problem is already on screen.</summary>
    [HttpGet]
    public IActionResult Ticket(int id, int officerId)
    {
        var officer = officers.Get(officerId);
        var ticket = queue.GetTicket(id);
        if (officer is null || ticket is null) return NotFound();

        if (ticket.OfficerId != officerId)
        {
            TempData["Error"] = $"{ticket.Number} is not assigned to you.";
            return RedirectToAction(nameof(Desk), new { id = officerId });
        }

        return View(new OfficerTicketPageModel
        {
            Officer = officer,
            Ticket = ticket,
            Sector = sectors.Get(ticket.SectorId)!,
            OtherSectors = sectors.All().Where(s => s.Id != ticket.SectorId).ToList()
        });
    }

    [HttpPost]
    public IActionResult Start(int id, int officerId) => Try(
        () => { queue.Start(id, officerId); return RedirectToAction(nameof(Ticket), new { id, officerId }); },
        () => RedirectToAction(nameof(Ticket), new { id, officerId }));

    [HttpPost]
    public IActionResult Resolve(int id, int officerId, string notes) => Try(
        () =>
        {
            var t = queue.Resolve(id, officerId, notes);
            TempData["Success"] = $"{t.Number} resolved ✅";
            return RedirectToAction(nameof(Desk), new { id = officerId });
        },
        () => RedirectToAction(nameof(Ticket), new { id, officerId }));

    [HttpPost]
    public IActionResult NoShow(int id, int officerId) => Try(
        () =>
        {
            var t = queue.MarkNoShow(id, officerId);
            TempData["Info"] = $"{t.Number} marked as no-show.";
            return RedirectToAction(nameof(Desk), new { id = officerId });
        },
        () => RedirectToAction(nameof(Ticket), new { id, officerId }));

    [HttpPost]
    public IActionResult Reroute(int id, int officerId, int newSectorId, string reason) => Try(
        () =>
        {
            var t = queue.Reroute(id, officerId, newSectorId, reason);
            TempData["Success"] = $"{t.Number} re-routed to {sectors.Get(newSectorId)!.Name}. The student keeps their place in line.";
            return RedirectToAction(nameof(Desk), new { id = officerId });
        },
        () => RedirectToAction(nameof(Ticket), new { id, officerId }));

    private IActionResult Try(Func<IActionResult> action, Func<IActionResult> onError)
    {
        try { return action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            TempData["Error"] = ex.Message;
            return onError();
        }
    }
}
