using CampusPass.Application.Abstractions;
using CampusPass.Application.Services;
using CampusPass.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace CampusPass.Web.Controllers;

// NOTE: no authentication in the prototype. Real version: [Authorize(Roles = "Admin")].
public class AdminController(ReportingService reports, IOfficerRepository officers, ISectorRepository sectors) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new AdminPageModel
    {
        Stats = reports.Overview(),
        Recent = reports.RecentTickets(),
        Officers = officers.All(),
        SectorsById = sectors.All().ToDictionary(s => s.Id)
    });

    /// <summary>Flip an officer on/off duty. Daily capacity (and future batch sizes) recalculate automatically.</summary>
    [HttpPost]
    public IActionResult ToggleDuty(int id)
    {
        var o = officers.Get(id);
        if (o is not null)
        {
            officers.SetOnDuty(id, !o.OnDuty);
            TempData["Info"] = $"{o.Name} is now {(o.OnDuty ? "on duty" : "off duty")}.";
        }
        return RedirectToAction(nameof(Index));
    }
}
